using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;
using Microsoft.Extensions.Logging;

namespace IndustrialDataAnalyzer.Services.Devices;

/// <summary>Coordinates validated configuration, connections, and parsed frames.</summary>
public sealed class DeviceManager : IDeviceManager, IDisposable
{
    private const int FrameQueueCapacity = 4096;
    private readonly IDeviceRepository repository;
    private readonly ICommunicationChannelFactory factory;
    private readonly IFrameParser parser;
    private readonly ILogger<DeviceManager> logger;
    private readonly ICapturePersistence? persistence;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, ICommunicationChannel> channels = [];
    private readonly Dictionary<Guid, Guid> activeSessions = [];
    private readonly Channel<QueuedFrame> frameQueue;
    private readonly Task frameWorker;
    private IReadOnlyList<DeviceDefinition> devices = [];
    private bool disposed;
    private long droppedFrames;

    public DeviceManager(IDeviceRepository repository, ICommunicationChannelFactory factory,
        IFrameParser parser, ILogger<DeviceManager> logger, ICapturePersistence? persistence = null)
    {
        this.repository = repository;
        this.factory = factory;
        this.parser = parser;
        this.logger = logger;
        this.persistence = persistence;
        frameQueue = Channel.CreateBounded<QueuedFrame>(
            new BoundedChannelOptions(FrameQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            }, _ =>
            {
                long dropped = Interlocked.Increment(ref droppedFrames);
                if (dropped == 1 || dropped % 1000 == 0)
                    logger.LogWarning("Frame queue full; {Dropped} oldest frames discarded", dropped);
            });
        frameWorker = Task.Run(ProcessFramesAsync);
    }

    public int PendingFrameCount => frameQueue.Reader.Count;
    public long DroppedFrameCount => Interlocked.Read(ref droppedFrames);

    public event Action? DevicesChanged;
    public event Action<DeviceStateChange>? StateChanged;
    public event Action<ParsedFrame>? FrameProcessed;
    public IReadOnlyList<DeviceDefinition> Devices => devices;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            IReadOnlyList<DeviceDefinition> loaded = await repository.LoadAsync(cancellationToken).ConfigureAwait(false);
            foreach (DeviceDefinition device in loaded)
            {
                Validate(device);
            }
            if (loaded.Select(item => item.Id).Distinct().Count() != loaded.Count ||
                loaded.Select(item => item.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != loaded.Count)
            {
                throw new InvalidDataException("设备配置存在重复的编号或标识。");
            }
            devices = [.. loaded];
            PublishDevicesChanged();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task UpsertAsync(DeviceDefinition device, CancellationToken cancellationToken = default)
    {
        Validate(device);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (devices.Any(item => item.Id != device.Id &&
                string.Equals(item.Code, device.Code, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("设备编号已存在。");
            }
            DeviceDefinition[] updated = [.. devices.Where(item => item.Id != device.Id), device];
            await RemoveChannelAsync(device.Id).ConfigureAwait(false);
            await repository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            devices = updated;
            PublishDevicesChanged();
            logger.LogInformation("Device {DeviceId} saved", device.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (devices.All(item => item.Id != id))
            {
                return;
            }
            DeviceDefinition[] updated = [.. devices.Where(item => item.Id != id)];
            await RemoveChannelAsync(id).ConfigureAwait(false);
            await repository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            devices = updated;
            PublishDevicesChanged();
            logger.LogInformation("Device {DeviceId} deleted", id);
        }
        finally
        {
            gate.Release();
        }
    }

    public ConnectionState GetState(Guid id)
    {
        lock (channels)
        {
            return channels.TryGetValue(id, out ICommunicationChannel? channel)
                ? channel.State : ConnectionState.Disconnected;
        }
    }

    public async Task ConnectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            DeviceDefinition device = devices.FirstOrDefault(item => item.Id == id)
                ?? throw new KeyNotFoundException("找不到该设备。");
            if (GetState(id) is ConnectionState.Connected or ConnectionState.Connecting)
            {
                throw new InvalidOperationException("设备已连接或正在连接。");
            }
            await RemoveChannelAsync(id).ConfigureAwait(false);
            ICommunicationChannel channel = factory.Create(device);
            Guid? sessionId;
            try
            {
                sessionId = persistence is null ? null :
                    await persistence.BeginSessionAsync(device, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await channel.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            channel.FrameReceived += frame =>
            {
                if (sessionId.HasValue) persistence!.TryAccept(sessionId.Value, frame, device.Parser);
                EnqueueFrame(frame, device.Parser, channel);
            };
            channel.StateChanged += change =>
            {
                PublishState(change);
                if (change.State == ConnectionState.Faulted)
                    _ = EndFaultedSessionAsync(id, channel);
            };
            lock (channels)
            {
                channels[id] = channel;
            }
            if (sessionId.HasValue) activeSessions[id] = sessionId.Value;
            try
            {
                await channel.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try { await EndActiveSessionAsync(id, CaptureSessionState.Failed).ConfigureAwait(false); }
                catch (Exception exception) { logger.LogError(exception, "Could not close failed capture session for {DeviceId}", id); }
                if (channel.State != ConnectionState.Faulted)
                {
                    await RemoveChannelAsync(id).ConfigureAwait(false);
                }
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DisconnectAsync(Guid id)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await RemoveChannelAsync(id).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task SendAsync(Guid id, string text, CancellationToken cancellationToken = default)
    {
        ICommunicationChannel channel;
        lock (channels)
        {
            channel = channels.TryGetValue(id, out ICommunicationChannel? found) &&
                found.State == ConnectionState.Connected ? found :
                throw new InvalidOperationException("设备尚未连接。");
        }
        return channel.SendAsync(text, cancellationToken);
    }

    public async Task DisconnectAllAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Guid[] ids;
            lock (channels)
            {
                ids = [.. channels.Keys.Concat(activeSessions.Keys).Distinct()];
            }
            foreach (Guid id in ids)
            {
                try
                {
                    await RemoveChannelAsync(id).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Could not disconnect device {DeviceId}", id);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        await DisconnectAllAsync().ConfigureAwait(false);
        frameQueue.Writer.TryComplete();
        await frameWorker.ConfigureAwait(false);
        if (persistence is not null) await persistence.DisposeAsync().ConfigureAwait(false);
        disposed = true;
        gate.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task RemoveChannelAsync(Guid id)
    {
        ICommunicationChannel? channel;
        CaptureSessionState finalState;
        lock (channels)
        {
            channels.TryGetValue(id, out channel);
            finalState = channel?.State == ConnectionState.Faulted
                ? CaptureSessionState.Failed : CaptureSessionState.Completed;
        }
        if (channel is not null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            lock (channels)
            {
                if (channels.TryGetValue(id, out ICommunicationChannel? tracked) && ReferenceEquals(channel, tracked))
                    channels.Remove(id);
            }
        }
        await EndActiveSessionAsync(id, finalState).ConfigureAwait(false);
    }

    private async Task EndActiveSessionAsync(Guid id, CaptureSessionState state)
    {
        if (persistence is null || !activeSessions.TryGetValue(id, out Guid sessionId)) return;
        await persistence.EndSessionAsync(sessionId, state).ConfigureAwait(false);
        activeSessions.Remove(id);
    }

    private async Task EndFaultedSessionAsync(Guid id, ICommunicationChannel channel)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (channels)
                if (!channels.TryGetValue(id, out ICommunicationChannel? current) || !ReferenceEquals(current, channel)) return;
            await EndActiveSessionAsync(id, CaptureSessionState.Failed).ConfigureAwait(false);
        }
        catch (Exception exception) { logger.LogError(exception, "Could not close faulted capture session for {DeviceId}", id); }
        finally { gate.Release(); }
    }

    private void EnqueueFrame(RawFrame raw, ParserOptions options, ICommunicationChannel channel)
    {
        if (channel.State == ConnectionState.Connected)
        {
            frameQueue.Writer.TryWrite(new QueuedFrame(raw, options, channel));
        }
    }

    private async Task ProcessFramesAsync()
    {
        await foreach (QueuedFrame queued in frameQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            lock (channels)
            {
                if (!channels.TryGetValue(queued.Raw.DeviceId, out ICommunicationChannel? current) ||
                    !ReferenceEquals(current, queued.Channel) || current.State != ConnectionState.Connected)
                {
                    continue;
                }
            }
            try
            {
                ProcessFrame(queued.Raw, queued.Options);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Frame processing failed for {DeviceId}", queued.Raw.DeviceId);
            }
        }
    }

    private void ProcessFrame(RawFrame raw, ParserOptions options)
    {
        ParsedFrame result = parser.Parse(raw, options);
        if (!result.IsSuccess)
        {
            logger.LogWarning("Parse failed for {DeviceId}: {Error}; raw={Raw}", raw.DeviceId,
                result.Error, raw.Text.Length > 512 ? raw.Text[..512] + "…" : raw.Text);
        }
        else
        {
            logger.LogDebug("Frame {Source} from {DeviceId}: {Raw} => {Field}={Value}{Unit}",
                raw.Source, raw.DeviceId, raw.Text.Length > 512 ? raw.Text[..512] + "…" : raw.Text,
                result.FieldName, result.Value, result.Unit);
        }
        if (FrameProcessed is not null)
        {
            foreach (Action<ParsedFrame> handler in FrameProcessed.GetInvocationList())
            {
                try { handler(result); }
                catch (Exception exception) { logger.LogError(exception, "Frame observer failed for {DeviceId}", raw.DeviceId); }
            }
        }
    }

    private void PublishState(DeviceStateChange change)
    {
        if (StateChanged is null) return;
        foreach (Action<DeviceStateChange> handler in StateChanged.GetInvocationList())
        {
            try { handler(change); }
            catch (Exception exception) { logger.LogError(exception, "State observer failed for {DeviceId}", change.DeviceId); }
        }
    }

    private void PublishDevicesChanged()
    {
        if (DevicesChanged is null) return;
        foreach (Action handler in DevicesChanged.GetInvocationList())
        {
            try { handler(); }
            catch (Exception exception) { logger.LogError(exception, "Device catalog observer failed"); }
        }
    }

    private static void Validate(DeviceDefinition device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Id == Guid.Empty || string.IsNullOrWhiteSpace(device.Name) || string.IsNullOrWhiteSpace(device.Code))
        {
            throw new ArgumentException("设备名称、编号和标识不能为空。");
        }
        if (device.Kind == DeviceKind.Serial)
        {
            SerialOptions serial = device.Serial;
            if (string.IsNullOrWhiteSpace(serial.PortName) || serial.BaudRate <= 0 ||
                serial.DataBits is < 5 or > 8 || string.IsNullOrEmpty(serial.Terminator) ||
                !Enum.IsDefined(serial.StopBits) || !Enum.IsDefined(serial.Parity) || !Enum.IsDefined(serial.FlowControl))
            {
                throw new ArgumentException("串口参数无效。");
            }
            _ = Encoding.GetEncoding(serial.EncodingName);
        }
        else if (device.Kind == DeviceKind.Simulator)
        {
            SimulatorOptions simulator = device.Simulator;
            if (simulator.IntervalMilliseconds < 50 || simulator.IntervalMilliseconds > 3_600_000 ||
                !double.IsFinite(simulator.Minimum) || !double.IsFinite(simulator.Maximum) ||
                simulator.Minimum > simulator.Maximum || !Enum.IsDefined(simulator.Measurement))
            {
                throw new ArgumentException("模拟设备的采样间隔或数据范围无效。");
            }
        }
        else
        {
            throw new ArgumentException("设备类型无效。");
        }
        if (string.IsNullOrWhiteSpace(device.Parser.FieldName) || !Enum.IsDefined(device.Parser.Mode) ||
            device.Parser.FieldIndex < 0 ||
            (device.Parser.Mode == ParserMode.Delimited && string.IsNullOrEmpty(device.Parser.Separator)))
        {
            throw new ArgumentException("解析规则无效。");
        }
        if (device.Parser.Mode == ParserMode.Regex)
        {
            _ = new Regex(device.Parser.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed record QueuedFrame(RawFrame Raw, ParserOptions Options, ICommunicationChannel Channel);
}
