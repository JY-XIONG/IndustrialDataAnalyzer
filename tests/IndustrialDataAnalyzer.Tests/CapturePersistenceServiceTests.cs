using System.IO;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;
using IndustrialDataAnalyzer.Infrastructure.Communication;
using IndustrialDataAnalyzer.Infrastructure.Paths;
using IndustrialDataAnalyzer.Infrastructure.Persistence;
using IndustrialDataAnalyzer.Services.Devices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace IndustrialDataAnalyzer.Tests;

public sealed class CapturePersistenceServiceTests
{
    [Fact]
    public async Task TwoSimulatorsPersistInSeparateSessionsAndDeletedConfigurationKeepsHistory()
    {
        string root = NewRoot();
        try
        {
            var paths = new ApplicationPaths(root);
            var store = new SqliteMeasurementRepository(paths);
            await using var persistence = NewPersistence(store, paths);
            await persistence.InitializeAsync();
            var devices = new JsonDeviceRepository(paths, NullLogger<JsonDeviceRepository>.Instance);
            await using var manager = new DeviceManager(devices,
                new CommunicationChannelFactory(NullLoggerFactory.Instance), new FrameParser(),
                NullLogger<DeviceManager>.Instance, persistence);
            await manager.InitializeAsync();
            DeviceDefinition first = Simulator("A"), second = Simulator("B");
            await manager.UpsertAsync(first);
            await manager.UpsertAsync(second);
            await manager.ConnectAsync(first.Id);
            await manager.ConnectAsync(second.Id);
            await WaitUntilAsync(() => persistence.GetStatus(first.Id).SavedCount > 0 &&
                persistence.GetStatus(second.Id).SavedCount > 0);
            await manager.DisconnectAllAsync();
            Assert.Equal(0, persistence.GetStatus(first.Id).PendingCount);
            Assert.True(persistence.GetStatus(first.Id).SavedCount > 0);
            Assert.True(persistence.GetStatus(second.Id).SavedCount > 0);
            await manager.DeleteAsync(first.Id);

            HistoryPage history = await store.QueryAsync(new HistoryQuery(PageSize: 200));
            Assert.Contains(history.Records, item => item.DeviceId == first.Id && item.DeviceName == "A");
            Assert.Contains(history.Records, item => item.DeviceId == second.Id && item.DeviceName == "B");
            CaptureSession[] sessions = [.. await store.ListSessionsAsync(limit: 200)];
            Assert.Equal(2, sessions.Length);
            Assert.All(sessions, item => Assert.Equal(CaptureSessionState.Completed, item.State));
            Assert.NotEqual(sessions[0].Id, sessions[1].Id);
            Assert.All(history.Records, item => Assert.Contains(sessions, session => session.Id == item.SessionId && session.DeviceId == item.DeviceId));
            await using var reopened = new DeviceManager(devices,
                new CommunicationChannelFactory(NullLoggerFactory.Instance), new FrameParser(),
                NullLogger<DeviceManager>.Instance);
            await reopened.InitializeAsync();
            Assert.DoesNotContain(reopened.Devices, item => item.Id == first.Id);
            Assert.Contains((await new SqliteMeasurementRepository(paths).QueryAsync(new HistoryQuery())).Records,
                item => item.DeviceId == first.Id);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task WriteFailureReportsBacklogAndOverflowThenRecoversWithoutFalseSavedCount()
    {
        string root = NewRoot();
        try
        {
            var paths = new ApplicationPaths(root);
            var failing = new FailWritesRepository(new SqliteMeasurementRepository(paths)) { FailWrites = true };
            await using var persistence = NewPersistence(failing, paths);
            await persistence.InitializeAsync();
            DeviceDefinition device = Simulator("Burst");
            Guid session = await persistence.BeginSessionAsync(device);
            for (int index = 0; index < 4500; index++)
            {
                var frame = new RawFrame(device.Id, DateTimeOffset.UtcNow, FrameSource.Simulator, index.ToString());
                persistence.TryAccept(session, frame, device.Parser);
            }
            await WaitUntilAsync(() => persistence.StorageError is not null);
            CaptureStatus blocked = persistence.GetStatus(device.Id);
            Assert.Equal(0, blocked.SavedCount);
            Assert.True(blocked.PendingCount > 0);
            Assert.True(blocked.LostCount > 0);
            Assert.NotNull(blocked.Error);
            failing.FailWrites = false;
            await persistence.EndSessionAsync(session).WaitAsync(TimeSpan.FromSeconds(15));
            CaptureStatus recovered = persistence.GetStatus(device.Id);
            Assert.Equal(4500, recovered.SavedCount + recovered.LostCount);
            Assert.Equal(0, recovered.PendingCount);
            Assert.Equal(recovered.SavedCount, (await failing.QueryAsync(new HistoryQuery(PageSize: 1))).TotalCount);
            CaptureSession stored = Assert.Single(await failing.ListSessionsAsync());
            Assert.Equal(recovered.LostCount, stored.LostCount);
            Assert.Equal(4500, stored.TotalCount);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task FailedShutdownSpoolsAcceptedFramesForNextStartupReplay()
    {
        string root = NewRoot();
        try
        {
            var paths = new ApplicationPaths(root);
            var failing = new FailWritesRepository(new SqliteMeasurementRepository(paths)) { FailWrites = true };
            var persistence = new CapturePersistenceService(failing, new FrameParser(), paths,
                NullLogger<CapturePersistenceService>.Instance, TimeSpan.FromMilliseconds(200));
            await persistence.InitializeAsync();
            DeviceDefinition device = Simulator("Recover");
            Guid session = await persistence.BeginSessionAsync(device);
            int accepted = 0;
            for (int index = 0; index < 4500; index++)
                if (persistence.TryAccept(session,
                    new RawFrame(device.Id, DateTimeOffset.UtcNow, FrameSource.Simulator, index.ToString()), device.Parser))
                    accepted++;
            long lost = persistence.GetStatus(device.Id).LostCount;
            Assert.True(lost > 0);
            await WaitUntilAsync(() => persistence.StorageError is not null);
            await persistence.DisposeAsync();
            Assert.True(File.Exists(Path.Combine(paths.DataDirectory, "pending-capture.jsonl")));
            Assert.Equal(0, (await failing.QueryAsync(new HistoryQuery())).TotalCount);

            failing.FailWrites = false;
            await using var restarted = NewPersistence(failing, paths);
            await restarted.InitializeAsync();
            Assert.Null(restarted.StorageError);
            Assert.False(File.Exists(Path.Combine(paths.DataDirectory, "pending-capture.jsonl")));
            Assert.Equal(accepted, (await failing.QueryAsync(new HistoryQuery())).TotalCount);
            CaptureSession recovered = Assert.Single(await failing.ListSessionsAsync());
            Assert.Equal(CaptureSessionState.Interrupted, recovered.State);
            Assert.Equal(lost, recovered.LostCount);
            Assert.Equal(4500, recovered.TotalCount);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task NormalShutdownFlushesAcceptedFramesAndCompletesSession()
    {
        string root = NewRoot();
        try
        {
            var paths = new ApplicationPaths(root);
            var store = new SqliteMeasurementRepository(paths);
            var persistence = new CapturePersistenceService(store, new FrameParser(), paths,
                NullLogger<CapturePersistenceService>.Instance, TimeSpan.FromSeconds(3));
            await persistence.InitializeAsync();
            DeviceDefinition device = Simulator("Normal close");
            Guid session = await persistence.BeginSessionAsync(device);
            for (int index = 0; index < 20; index++)
                Assert.True(persistence.TryAccept(session,
                    new RawFrame(device.Id, DateTimeOffset.UtcNow, FrameSource.Simulator, index.ToString()), device.Parser));

            await persistence.DisposeAsync();

            Assert.Equal(20, persistence.GetStatus(device.Id).SavedCount);
            Assert.Equal(0, persistence.GetStatus(device.Id).PendingCount);
            Assert.Equal(CaptureSessionState.Completed, persistence.GetStatus(device.Id).State);
            Assert.Equal(20, (await store.QueryAsync(new HistoryQuery())).TotalCount);
            Assert.Equal(CaptureSessionState.Completed, Assert.Single(await store.ListSessionsAsync()).State);
            Assert.False(File.Exists(Path.Combine(paths.DataDirectory, "pending-capture.jsonl")));
            Assert.True(File.Exists(Path.Combine(paths.DataDirectory, "measurements.db.bak")));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task StopWaitsForStorageThenRetryCompletesAndFailureStateIsPreserved()
    {
        string root = NewRoot();
        try
        {
            var paths = new ApplicationPaths(root);
            var failing = new FailWritesRepository(new SqliteMeasurementRepository(paths)) { FailWrites = true };
            await using var persistence = new CapturePersistenceService(failing, new FrameParser(), paths,
                NullLogger<CapturePersistenceService>.Instance, TimeSpan.FromSeconds(3),
                sessionDrainTimeout: TimeSpan.FromMilliseconds(150));
            await persistence.InitializeAsync();
            DeviceDefinition device = Simulator("Retry stop");
            Guid session = await persistence.BeginSessionAsync(device);
            Assert.True(persistence.TryAccept(session,
                new RawFrame(device.Id, DateTimeOffset.UtcNow, FrameSource.Simulator, "12.5"), device.Parser));
            await WaitUntilAsync(() => persistence.StorageError is not null);

            await Assert.ThrowsAsync<IOException>(() => persistence.EndSessionAsync(session));
            Assert.Equal(CaptureSessionState.WaitingForStorage, persistence.GetStatus(device.Id).State);
            Assert.Equal(0, persistence.GetStatus(device.Id).SavedCount);
            Assert.Equal(CaptureSessionState.WaitingForStorage, Assert.Single(await failing.ListSessionsAsync()).State);

            failing.FailWrites = false;
            await persistence.EndSessionAsync(session);
            Assert.Equal(CaptureSessionState.Completed, persistence.GetStatus(device.Id).State);
            Assert.Equal(1, persistence.GetStatus(device.Id).SavedCount);
            Guid failedSession = await persistence.BeginSessionAsync(device);
            await persistence.EndSessionAsync(failedSession, CaptureSessionState.Failed);
            Assert.Equal(CaptureSessionState.Failed, persistence.GetStatus(device.Id).State);
            Assert.Contains(await failing.ListSessionsAsync(), item => item.Id == failedSession &&
                item.State == CaptureSessionState.Failed);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task UnresponsiveWriterPreventsRacyJournalUntilWriterActuallyStops()
    {
        string root = NewRoot();
        try
        {
            var paths = new ApplicationPaths(root);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failing = new FailWritesRepository(new SqliteMeasurementRepository(paths))
            {
                SaveOverride = async (_, _) =>
                {
                    started.TrySetResult();
                    await release.Task;
                    throw new IOException("延迟数据库故障");
                }
            };
            var persistence = new CapturePersistenceService(failing, new FrameParser(), paths,
                NullLogger<CapturePersistenceService>.Instance, TimeSpan.FromMilliseconds(100),
                workerStopTimeout: TimeSpan.FromMilliseconds(100));
            await persistence.InitializeAsync();
            DeviceDefinition device = Simulator("Slow writer");
            Guid session = await persistence.BeginSessionAsync(device);
            Assert.True(persistence.TryAccept(session,
                new RawFrame(device.Id, DateTimeOffset.UtcNow, FrameSource.Simulator, "8"), device.Parser));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            IOException timeout = await Assert.ThrowsAsync<IOException>(() => persistence.DisposeAsync().AsTask());
            Assert.Contains("仍未停止", timeout.Message);
            Assert.False(File.Exists(Path.Combine(paths.DataDirectory, "pending-capture.jsonl")));
            Assert.Equal(0, persistence.GetStatus(device.Id).SavedCount);
            Assert.Equal(1, persistence.GetStatus(device.Id).PendingCount);

            release.SetResult();
            await persistence.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(File.Exists(Path.Combine(paths.DataDirectory, "pending-capture.jsonl")));
            failing.SaveOverride = null;
            await using var restarted = NewPersistence(failing, paths);
            await restarted.InitializeAsync();
            Assert.Equal(1, (await failing.QueryAsync(new HistoryQuery())).TotalCount);
            Assert.Equal(CaptureSessionState.Interrupted, Assert.Single(await failing.ListSessionsAsync()).State);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task JournalWriteFailureReportsUnsavedDataAndRetryCanRecover()
    {
        string root = NewRoot();
        try
        {
            var paths = new ApplicationPaths(root);
            var failing = new FailWritesRepository(new SqliteMeasurementRepository(paths)) { FailWrites = true };
            var persistence = new CapturePersistenceService(failing, new FrameParser(), paths,
                NullLogger<CapturePersistenceService>.Instance, TimeSpan.FromMilliseconds(100));
            await persistence.InitializeAsync();
            DeviceDefinition device = Simulator("Journal failure");
            Guid session = await persistence.BeginSessionAsync(device);
            Assert.True(persistence.TryAccept(session,
                new RawFrame(device.Id, DateTimeOffset.UtcNow, FrameSource.Simulator, "9"), device.Parser));
            await WaitUntilAsync(() => persistence.StorageError is not null);
            string journalPath = Path.Combine(paths.DataDirectory, "pending-capture.jsonl");
            Directory.CreateDirectory(journalPath); // blocks the final atomic rename

            IOException failure = await Assert.ThrowsAsync<IOException>(() => persistence.DisposeAsync().AsTask());
            Assert.Contains("恢复文件写入失败", failure.Message);
            Assert.Contains("恢复文件写入失败", persistence.StorageError);
            Assert.Equal(0, persistence.GetStatus(device.Id).SavedCount);
            Assert.Equal(1, persistence.GetStatus(device.Id).PendingCount);
            Directory.Delete(journalPath);

            await persistence.DisposeAsync();
            Assert.True(File.Exists(journalPath));
            failing.FailWrites = false;
            await using var restarted = NewPersistence(failing, paths);
            await restarted.InitializeAsync();
            Assert.Equal(1, (await failing.QueryAsync(new HistoryQuery())).TotalCount);
        }
        finally { Cleanup(root); }
    }

    private static CapturePersistenceService NewPersistence(IMeasurementRepository repository, ApplicationPaths paths) =>
        new(repository, new FrameParser(), paths, NullLogger<CapturePersistenceService>.Instance);

    private static DeviceDefinition Simulator(string name) => new()
    {
        Id = Guid.NewGuid(), Name = name, Code = name, Kind = DeviceKind.Simulator,
        Simulator = new SimulatorOptions { IntervalMilliseconds = 50 }
    };

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "IndustrialDataAnalyzer.Tests", Guid.NewGuid().ToString("N"));
    private static void Cleanup(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(30);
        }
        throw new TimeoutException("测试等待条件未达成。");
    }

    private sealed class FailWritesRepository(IMeasurementRepository inner) : IMeasurementRepository
    {
        public volatile bool FailWrites;
        public Func<IReadOnlyList<MeasurementRecord>, CancellationToken, Task>? SaveOverride;
        public Task InitializeAsync(CancellationToken token = default) => inner.InitializeAsync(token);
        public Task CreateSessionAsync(CaptureSession session, CancellationToken token = default) => inner.CreateSessionAsync(session, token);
        public Task SetSessionStateAsync(Guid id, CaptureSessionState state, CancellationToken token = default) =>
            inner.SetSessionStateAsync(id, state, token);
        public Task SaveBatchAsync(IReadOnlyList<MeasurementRecord> records, CancellationToken token = default) =>
            SaveOverride is not null ? SaveOverride(records, token) :
            FailWrites ? Task.FromException(new IOException("测试数据库故障")) : inner.SaveBatchAsync(records, token);
        public Task CompleteSessionAsync(Guid id, CaptureSessionState state, long lost, CancellationToken token = default) =>
            inner.CompleteSessionAsync(id, state, lost, token);
        public Task SetLostCountAsync(Guid id, long lost, CancellationToken token = default) => inner.SetLostCountAsync(id, lost, token);
        public Task<HistoryPage> QueryAsync(HistoryQuery query, CancellationToken token = default) => inner.QueryAsync(query, token);
        public Task<IReadOnlyList<HistoricalDevice>> ListDevicesAsync(CancellationToken token = default) => inner.ListDevicesAsync(token);
        public Task<IReadOnlyList<CaptureSession>> ListSessionsAsync(Guid? deviceId = null,
            DateTimeOffset? fromUtc = null, DateTimeOffset? toUtcExclusive = null, int limit = 200,
            CancellationToken token = default) => inner.ListSessionsAsync(deviceId, fromUtc, toUtcExclusive, limit, token);
        public Task<long> CountSinceAsync(DateTimeOffset fromUtc, CancellationToken token = default) => inner.CountSinceAsync(fromUtc, token);
        public Task CheckpointAndBackupAsync(CancellationToken token = default) => inner.CheckpointAndBackupAsync(token);
    }
}
