using System.Threading.Channels;
using System.Text.Json;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;
using Microsoft.Extensions.Logging;

namespace IndustrialDataAnalyzer.Services.Devices;

/// <summary>Reliable, bounded ingestion independent of the lossy live display queue.</summary>
public sealed class CapturePersistenceService : ICapturePersistence
{
    private const int Capacity = 4096;
    private const int BatchSize = 100;
    private readonly IMeasurementRepository repository;
    private readonly IFrameParser parser;
    private readonly ILogger<CapturePersistenceService> logger;
    private readonly string journalPath;
    private readonly string lossJournalPath;
    private readonly TimeSpan shutdownDrainTimeout;
    private readonly TimeSpan sessionDrainTimeout;
    private readonly TimeSpan workerStopTimeout;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Channel<QueuedCapture> queue = Channel.CreateBounded<QueuedCapture>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly object gate = new();
    private readonly SemaphoreSlim disposeGate = new(1, 1);
    private readonly SemaphoreSlim initializeGate = new(1, 1);
    private readonly Dictionary<Guid, SessionContext> sessions = [];
    private readonly Dictionary<Guid, SessionContext> latestByDevice = [];
    private List<QueuedCapture> inFlight = [];
    private List<QueuedCapture>? recoverySnapshot;
    private readonly Task worker;
    private bool initialized;
    private bool disposed;
    private bool closing;
    private bool forcedStop;
    private string? storageError;

    public CapturePersistenceService(IMeasurementRepository repository, IFrameParser parser,
        IApplicationPaths paths, ILogger<CapturePersistenceService> logger)
        : this(repository, parser, paths, logger, TimeSpan.FromSeconds(15)) { }

    internal CapturePersistenceService(IMeasurementRepository repository, IFrameParser parser,
        IApplicationPaths paths, ILogger<CapturePersistenceService> logger, TimeSpan shutdownDrainTimeout,
        TimeSpan? sessionDrainTimeout = null, TimeSpan? workerStopTimeout = null)
    {
        this.repository = repository;
        this.parser = parser;
        this.logger = logger;
        journalPath = Path.Combine(paths.DataDirectory, "pending-capture.jsonl");
        lossJournalPath = Path.Combine(paths.DataDirectory, "pending-loss.json");
        this.shutdownDrainTimeout = shutdownDrainTimeout;
        this.sessionDrainTimeout = sessionDrainTimeout ?? TimeSpan.FromSeconds(15);
        this.workerStopTimeout = workerStopTimeout ?? TimeSpan.FromSeconds(7);
        worker = Task.Run(WriteLoopAsync);
    }

    public string? StorageError { get { lock (gate) return storageError; } }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized) return;
            await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await ReplayJournalAsync(cancellationToken).ConfigureAwait(false);
            lock (gate) { initialized = true; storageError = null; }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            lock (gate) storageError = "数据库初始化失败：" + exception.Message;
            logger.LogError(exception, "Measurement database initialization failed");
        }
        finally { initializeGate.Release(); }
    }

    public async Task<Guid> BeginSessionAsync(DeviceDefinition device, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (closing) throw new InvalidOperationException("存储服务正在关闭，不能建立新会话。");
        if (!initialized) await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!initialized) throw new IOException(StorageError ?? "数据库不可用，不能启动采集。");
        lock (gate)
        {
            if (latestByDevice.TryGetValue(device.Id, out SessionContext? previous) && previous.Active)
                throw new InvalidOperationException("该设备上次采集会话尚未完成落盘，请先停止并重试。");
        }
        var session = new CaptureSession(Guid.NewGuid(), device.Id, device.Name, DateTimeOffset.UtcNow,
            null, 0, 0, 0, 0, CaptureSessionState.Active);
        try { await repository.CreateSessionAsync(session, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            lock (gate) storageError = "数据库创建会话失败：" + exception.Message;
            logger.LogError(exception, "Could not create capture session for {DeviceId}", device.Id);
            throw new IOException(StorageError, exception);
        }
        lock (gate)
        {
            var context = new SessionContext(session.Id, device.Id);
            sessions.Add(session.Id, context);
            latestByDevice[device.Id] = context;
            storageError = null;
        }
        return session.Id;
    }

    public bool TryAccept(Guid sessionId, RawFrame frame, ParserOptions options)
    {
        lock (gate)
        {
            if (closing || !sessions.TryGetValue(sessionId, out SessionContext? context) || !context.Active || context.Stopping)
                return false;
            if (context.Pending == 0 && context.Drained.Task.IsCompleted)
                context.Drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Pending++;
            if (queue.Writer.TryWrite(new QueuedCapture(Guid.NewGuid(), sessionId, frame, options))) return true;
            context.Pending--;
            context.Lost++;
            context.Error = $"存储队列已满，已拒收 {context.Lost} 条报文。";
            if (context.Lost == 1 || context.Lost % 1000 == 0)
                logger.LogError("Capture queue overflow for {DeviceId}; lost={Lost}", frame.DeviceId, context.Lost);
            return false;
        }
    }

    public async Task EndSessionAsync(Guid sessionId, CaptureSessionState state = CaptureSessionState.Completed,
        CancellationToken cancellationToken = default)
    {
        if (state is not (CaptureSessionState.Completed or CaptureSessionState.Failed))
            throw new ArgumentOutOfRangeException(nameof(state), "结束会话需要正常结束或异常结束状态。");
        SessionContext context;
        lock (gate)
        {
            if (!sessions.TryGetValue(sessionId, out context!)) return;
        }
        await context.EndGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Task drained;
            CaptureSessionState finalState;
            lock (gate)
            {
                if (!sessions.ContainsKey(sessionId)) return;
                context.Stopping = true;
                context.DesiredTerminalState ??= state;
                finalState = context.DesiredTerminalState.Value;
                context.State = CaptureSessionState.Stopping;
                drained = context.Pending == 0 ? Task.CompletedTask : context.Drained.Task;
            }
            await UpdateDatabaseStateAsync(context, CaptureSessionState.Stopping).ConfigureAwait(false);
            try { await drained.WaitAsync(sessionDrainTimeout, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException exception)
            {
                string error;
                lock (gate)
                {
                    context.State = CaptureSessionState.WaitingForStorage;
                    context.Error = $"仍有 {context.Pending} 条待落盘；数据库恢复后可重试停止。";
                    error = context.Error;
                }
                await UpdateDatabaseStateAsync(context, CaptureSessionState.WaitingForStorage).ConfigureAwait(false);
                throw new IOException(error, exception);
            }
            try { await repository.CompleteSessionAsync(sessionId, finalState, context.Lost, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                lock (gate)
                {
                    context.State = CaptureSessionState.WaitingForStorage;
                    storageError = "结束采集会话失败：" + exception.Message;
                    context.Error = storageError;
                }
                await UpdateDatabaseStateAsync(context, CaptureSessionState.WaitingForStorage).ConfigureAwait(false);
                logger.LogError(exception, "Could not finish capture session {SessionId}", sessionId);
                throw new IOException(StorageError, exception);
            }
            lock (gate)
            {
                context.Active = false;
                context.State = finalState;
                context.Error = context.Lost > 0 ? $"存储积压曾导致 {context.Lost} 条报文丢失。" : null;
                sessions.Remove(sessionId);
                storageError = null;
            }
        }
        finally { context.EndGate.Release(); }
    }

    private async Task UpdateDatabaseStateAsync(SessionContext context, CaptureSessionState state)
    {
        try { await repository.SetSessionStateAsync(context.Id, state).ConfigureAwait(false); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not record intermediate capture state {State} for {SessionId}",
                state, context.Id);
        }
    }

    public CaptureStatus GetStatus(Guid deviceId)
    {
        lock (gate)
        {
            if (!latestByDevice.TryGetValue(deviceId, out SessionContext? context))
                return new CaptureStatus(deviceId, null, null, 0, 0, 0, storageError);
            return new CaptureStatus(deviceId, context.Id, context.State, context.Saved,
                context.Pending, context.Lost, context.Error ?? storageError);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            lock (gate) closing = true;
            queue.Writer.TryComplete();
            if (!forcedStop)
            {
                try { await worker.WaitAsync(shutdownDrainTimeout).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    forcedStop = true;
                    shutdown.Cancel();
                    logger.LogWarning("Capture writer did not drain within {Timeout}; cancellation requested",
                        shutdownDrainTimeout);
                }
                catch (Exception exception)
                {
                    forcedStop = true;
                    shutdown.Cancel();
                    logger.LogError(exception, "Capture writer stopped unexpectedly");
                }
            }
            if (forcedStop)
            {
                try { await worker.WaitAsync(workerStopTimeout).ConfigureAwait(false); }
                catch (TimeoutException exception)
                {
                    string error = "后台数据库写入任务仍未停止；未生成恢复文件，待写数据未确认为已保存。";
                    SetRecoveryError(error);
                    logger.LogCritical(exception, "Unsafe to snapshot capture queue while writer is still running");
                    throw new IOException(error, exception);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Capture writer ended with an error; preserving remaining frames");
                }
                await SpoolRemainingAsync().ConfigureAwait(false);
            }
            else if (initialized)
            {
                await FinishRemainingSessionsAsync().ConfigureAwait(false);
                try { await repository.CheckpointAndBackupAsync().ConfigureAwait(false); }
                catch (Exception exception) { logger.LogError(exception, "Could not checkpoint or back up measurement database"); }
            }
            disposed = true;
            shutdown.Dispose();
        }
        finally { disposeGate.Release(); }
    }

    private async Task SpoolRemainingAsync()
    {
        // The worker has completed. It cannot dequeue frames or mutate inFlight during this snapshot.
        if (recoverySnapshot is null)
        {
            var snapshot = new List<QueuedCapture>();
            lock (gate) snapshot.AddRange(inFlight);
            while (queue.Reader.TryRead(out QueuedCapture? item)) snapshot.Add(item);
            recoverySnapshot = snapshot;
        }
        List<QueuedCapture> remaining = recoverySnapshot;
        Dictionary<Guid, long> losses;
        long pending;
        lock (gate)
        {
            losses = sessions.Values.Where(context => context.Lost > 0)
                .ToDictionary(context => context.Id, context => context.Lost);
            pending = sessions.Values.Sum(context => context.Pending);
        }
        if (pending != remaining.Count)
        {
            string error = $"恢复快照与待写计数不一致（待写 {pending}、快照 {remaining.Count}）；未生成恢复文件。";
            SetRecoveryError(error);
            throw new IOException(error);
        }
        try
        {
            if (remaining.Count > 0 || losses.Count > 0)
                await Task.Run(() => SaveJournal(remaining, losses)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            string error = "恢复文件写入失败：" + exception.Message + "；待写数据未确认为已保存。";
            SetRecoveryError(error);
            logger.LogCritical(exception, "Could not write capture recovery files");
            throw new IOException(error, exception);
        }
        string notice = $"数据库未完成写入；{remaining.Count} 条待写报文已转存恢复文件，尚未计为已保存。";
        SetRecoveryError(notice);
        logger.LogError("Spooled {Count} pending frames for recovery at {Path}", remaining.Count, journalPath);
    }

    private async Task FinishRemainingSessionsAsync()
    {
        foreach (SessionContext context in sessions.Values.ToArray())
        {
            try
            {
                if (context.Pending != 0) throw new InvalidOperationException("后台任务结束但会话仍有待写记录。");
                CaptureSessionState finalState = context.DesiredTerminalState ?? CaptureSessionState.Completed;
                await repository.CompleteSessionAsync(context.Id, finalState, context.Lost).ConfigureAwait(false);
                lock (gate)
                {
                    context.Active = false;
                    context.State = finalState;
                    context.Error = context.Lost > 0 ? $"存储积压曾导致 {context.Lost} 条报文丢失。" : null;
                    sessions.Remove(context.Id);
                }
            }
            catch (Exception exception)
            {
                string error = "退出时结束采集会话失败：" + exception.Message;
                SetRecoveryError(error);
                logger.LogError(exception, "Could not finish session {SessionId} during shutdown", context.Id);
                try
                {
                    if (context.Lost > 0)
                        await Task.Run(() => SaveJournal([], new Dictionary<Guid, long> { [context.Id] = context.Lost })).ConfigureAwait(false);
                }
                catch (Exception journalException)
                {
                    string journalError = "恢复文件写入失败：" + journalException.Message;
                    SetRecoveryError(journalError);
                    logger.LogCritical(journalException, "Could not preserve lost count for {SessionId}", context.Id);
                    throw new IOException(journalError, journalException);
                }
                throw new IOException(error, exception);
            }
        }
    }

    private void SetRecoveryError(string message)
    {
        lock (gate)
        {
            storageError = message;
            foreach (SessionContext context in sessions.Values)
            {
                context.State = CaptureSessionState.WaitingForStorage;
                context.Error = message;
            }
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
        await foreach (QueuedCapture first in queue.Reader.ReadAllAsync(shutdown.Token).ConfigureAwait(false))
        {
            var pending = new List<QueuedCapture>(BatchSize) { first };
            lock (gate) inFlight = pending;
            await Task.Delay(100, shutdown.Token).ConfigureAwait(false);
            while (pending.Count < BatchSize && queue.Reader.TryRead(out QueuedCapture? next)) pending.Add(next);
            var records = new List<MeasurementRecord>(pending.Count);
            foreach (QueuedCapture item in pending)
            {
                ParsedFrame parsed;
                try { parsed = parser.Parse(item.Frame, item.Options); }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Parser failed for stored frame {DeviceId}", item.Frame.DeviceId);
                    parsed = new ParsedFrame(item.Frame, item.Options.FieldName, item.Options.Unit, null, exception.Message);
                }
                records.Add(new MeasurementRecord(item.Id, item.SessionId, item.Frame.DeviceId,
                    item.Frame.Timestamp.ToUniversalTime(), item.Frame.Source, item.Frame.Text,
                    parsed.FieldName, parsed.Value, parsed.Unit, parsed.Error));
            }
            int delay = 250;
            while (true)
            {
                try
                {
                    await repository.SaveBatchAsync(records, shutdown.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        storageError = null;
                        foreach (var group in records.GroupBy(record => record.SessionId))
                        {
                            if (!sessions.TryGetValue(group.Key, out SessionContext? context)) continue;
                            context.Saved += group.Count();
                            context.Pending -= group.Count();
                            context.Error = context.State == CaptureSessionState.WaitingForStorage
                                ? "待写数据已恢复，请重试停止以结束会话。"
                                : context.Lost > 0 ? $"存储队列溢出，已丢失 {context.Lost} 条报文。" : null;
                            if (context.Pending == 0) context.Drained.TrySetResult();
                        }
                    }
                    lock (gate) inFlight = [];
                    break;
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    lock (gate)
                    {
                        storageError = "数据库写入失败，正在重试：" + exception.Message;
                        foreach (QueuedCapture item in pending)
                            if (sessions.TryGetValue(item.SessionId, out SessionContext? context)) context.Error = storageError;
                    }
                    logger.LogError(exception, "Measurement batch write failed; retrying {Count} records", records.Count);
                    await Task.Delay(delay, shutdown.Token).ConfigureAwait(false);
                    delay = Math.Min(delay * 2, 5000);
                }
            }
        }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    private async Task ReplayJournalAsync(CancellationToken cancellationToken)
    {
        QueuedCapture[] queued = !File.Exists(journalPath) ? [] : await Task.Run(() => File.ReadLines(journalPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<QueuedCapture>(line)
                ?? throw new InvalidDataException("待恢复报文文件包含空记录。"))
            .ToArray(), cancellationToken).ConfigureAwait(false);
        foreach (QueuedCapture[] batch in queued.Chunk(BatchSize))
        {
            var records = batch.Select(item =>
            {
                ParsedFrame parsed;
                try { parsed = parser.Parse(item.Frame, item.Options); }
                catch (Exception exception)
                {
                    parsed = new ParsedFrame(item.Frame, item.Options.FieldName, item.Options.Unit, null, exception.Message);
                }
                return new MeasurementRecord(item.Id, item.SessionId, item.Frame.DeviceId,
                    item.Frame.Timestamp.ToUniversalTime(), item.Frame.Source, item.Frame.Text,
                    parsed.FieldName, parsed.Value, parsed.Unit, parsed.Error);
            }).ToArray();
            await repository.SaveBatchAsync(records, cancellationToken).ConfigureAwait(false);
        }
        if (File.Exists(lossJournalPath))
        {
            Dictionary<Guid, long> losses = JsonSerializer.Deserialize<Dictionary<Guid, long>>(
                await File.ReadAllTextAsync(lossJournalPath, cancellationToken).ConfigureAwait(false)) ?? [];
            foreach (var (sessionId, lost) in losses)
                await repository.SetLostCountAsync(sessionId, lost, cancellationToken).ConfigureAwait(false);
        }
        if (File.Exists(journalPath)) File.Delete(journalPath);
        if (File.Exists(lossJournalPath)) File.Delete(lossJournalPath);
        logger.LogInformation("Recovered {Count} pending capture frames", queued.Length);
    }

    private void SaveJournal(IReadOnlyList<QueuedCapture> remaining, IReadOnlyDictionary<Guid, long> losses)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        if (remaining.Count > 0)
        {
            var byId = new Dictionary<Guid, QueuedCapture>();
            if (File.Exists(journalPath))
            {
                foreach (string line in File.ReadLines(journalPath).Where(line => !string.IsNullOrWhiteSpace(line)))
                {
                    QueuedCapture existing = JsonSerializer.Deserialize<QueuedCapture>(line)
                        ?? throw new InvalidDataException("恢复文件包含空记录。");
                    byId[existing.Id] = existing;
                }
            }
            foreach (QueuedCapture item in remaining) byId[item.Id] = item;
            string temporary = journalPath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                foreach (QueuedCapture item in byId.Values) writer.WriteLine(JsonSerializer.Serialize(item));
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(temporary, journalPath, true);
        }
        if (losses.Count == 0) return;
        Dictionary<Guid, long> merged = File.Exists(lossJournalPath)
            ? JsonSerializer.Deserialize<Dictionary<Guid, long>>(File.ReadAllText(lossJournalPath)) ?? [] : [];
        foreach (var (sessionId, lost) in losses)
            merged[sessionId] = Math.Max(merged.GetValueOrDefault(sessionId), lost);
        string lossTemporary = lossJournalPath + ".tmp";
        using (var stream = new FileStream(lossTemporary, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(JsonSerializer.Serialize(merged));
            writer.Flush();
            stream.Flush(true);
        }
        File.Move(lossTemporary, lossJournalPath, true);
    }

    private sealed record QueuedCapture(Guid Id, Guid SessionId, RawFrame Frame, ParserOptions Options);

    private sealed class SessionContext(Guid id, Guid deviceId)
    {
        public Guid Id { get; } = id;
        public Guid DeviceId { get; } = deviceId;
        public bool Active { get; set; } = true;
        public bool Stopping { get; set; }
        public CaptureSessionState? DesiredTerminalState { get; set; }
        public SemaphoreSlim EndGate { get; } = new(1, 1);
        public CaptureSessionState State { get; set; } = CaptureSessionState.Active;
        public long Saved { get; set; }
        public long Pending { get; set; }
        public long Lost { get; set; }
        public string? Error { get; set; }
        public TaskCompletionSource Drained { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
