using System.IO;
using IndustrialDataAnalyzer.Core.Models;
using IndustrialDataAnalyzer.Infrastructure.Paths;
using IndustrialDataAnalyzer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace IndustrialDataAnalyzer.Tests;

public sealed class SqliteMeasurementRepositoryTests
{
    [Fact]
    public async Task BatchPersistsUtcDataAcrossRestartAndPagesByDeviceAndSession()
    {
        string root = Path.Combine(Path.GetTempPath(), "IndustrialDataAnalyzer.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new ApplicationPaths(root);
            var repository = new SqliteMeasurementRepository(paths);
            await repository.InitializeAsync();
            Guid firstDevice = Guid.NewGuid(), secondDevice = Guid.NewGuid();
            DateTimeOffset start = new(2026, 11, 14, 12, 0, 0, TimeSpan.FromHours(8));
            var first = Session(firstDevice, "旧设备", start);
            var second = Session(secondDevice, "设备二", start.AddMinutes(1));
            await repository.CreateSessionAsync(first);
            await repository.CreateSessionAsync(second);
            MeasurementRecord[] records =
            [
                Record(first, start.AddMinutes(2), "20.5", 20.5),
                Record(first, start.AddMinutes(3), "bad", null, "无效值"),
                Record(second, start.AddMinutes(4), "30", 30)
            ];
            await repository.SaveBatchAsync(records);
            await repository.SaveBatchAsync(records); // idempotent retry after uncertain commit
            await repository.CompleteSessionAsync(first.Id, CaptureSessionState.Completed, 2);
            await repository.CompleteSessionAsync(second.Id, CaptureSessionState.Completed, 0);
            await repository.CheckpointAndBackupAsync();

            var reopened = new SqliteMeasurementRepository(paths);
            await reopened.InitializeAsync();
            HistoryPage page1 = await reopened.QueryAsync(new HistoryQuery(Page: 1, PageSize: 2));
            HistoryPage page2 = await reopened.QueryAsync(new HistoryQuery(Page: 2, PageSize: 2));
            Assert.Equal(3, page1.TotalCount);
            Assert.Equal(2, page1.Records.Count);
            Assert.Single(page2.Records);
            HistoryPage filtered = await reopened.QueryAsync(new HistoryQuery(
                DeviceId: firstDevice, SessionId: first.Id, FromUtc: start.AddMinutes(2).ToUniversalTime(),
                ToUtcExclusive: start.AddMinutes(4).ToUniversalTime()));
            Assert.Equal(2, filtered.TotalCount);
            Assert.Contains(filtered.Records, item => !item.IsSuccess && item.Error == "无效值");
            Assert.All(filtered.Records, item =>
            {
                Assert.Equal(TimeSpan.Zero, item.CapturedUtc.Offset);
                Assert.Equal("旧设备", item.DeviceName);
            });
            CaptureSession loaded = Assert.Single(await reopened.ListSessionsAsync(firstDevice));
            Assert.Equal(4, loaded.TotalCount);
            Assert.Equal(1, loaded.ValidCount);
            Assert.Equal(1, loaded.FailedCount);
            Assert.Equal(2, loaded.LostCount);
            Assert.Equal(3, await reopened.CountSinceAsync(start.ToUniversalTime()));
            Assert.True(File.Exists(Path.Combine(paths.DataDirectory, "measurements.db.bak")));
            using var connection = new SqliteConnection($"Data Source={Path.Combine(paths.DataDirectory, "measurements.db")}");
            connection.Open();
            using var mode = connection.CreateCommand();
            mode.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", mode.ExecuteScalar());
            mode.CommandText = "PRAGMA user_version;";
            Assert.Equal(1L, mode.ExecuteScalar());
            mode.CommandText = "SELECT SUM(parse_status) FROM measurements;";
            Assert.Equal(2L, mode.ExecuteScalar());
            SqliteConnection.ClearAllPools();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task StartupMarksUnfinishedSessionInterruptedWithoutDeletingRecords()
    {
        string root = Path.Combine(Path.GetTempPath(), "IndustrialDataAnalyzer.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new ApplicationPaths(root);
            var repository = new SqliteMeasurementRepository(paths);
            await repository.InitializeAsync();
            CaptureSession session = Session(Guid.NewGuid(), "未正常结束", DateTimeOffset.UtcNow);
            await repository.CreateSessionAsync(session);
            await repository.SaveBatchAsync([Record(session, DateTimeOffset.UtcNow, "12", 12)]);
            await new SqliteMeasurementRepository(paths).InitializeAsync();
            CaptureSession recovered = Assert.Single(await repository.ListSessionsAsync());
            Assert.Equal(CaptureSessionState.Interrupted, recovered.State);
            Assert.NotNull(recovered.EndedUtc);
            Assert.Equal(1, recovered.ValidCount);
            Assert.Single((await repository.QueryAsync(new HistoryQuery())).Records);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task VersionZeroMigrationPreservesExistingTablesAndCreatesBackup()
    {
        string root = Path.Combine(Path.GetTempPath(), "IndustrialDataAnalyzer.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new ApplicationPaths(root);
            Directory.CreateDirectory(paths.DataDirectory);
            string database = Path.Combine(paths.DataDirectory, "measurements.db");
            using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE existing_marker(value TEXT); INSERT INTO existing_marker VALUES('keep');";
                command.ExecuteNonQuery();
            }
            var repository = new SqliteMeasurementRepository(paths);
            await repository.InitializeAsync();
            using var reopened = new SqliteConnection($"Data Source={database}");
            reopened.Open();
            using var check = reopened.CreateCommand();
            check.CommandText = "SELECT value FROM existing_marker;";
            Assert.Equal("keep", check.ExecuteScalar());
            check.CommandText = "PRAGMA user_version;";
            Assert.Equal(1L, check.ExecuteScalar());
            Assert.True(File.Exists(database + ".before-v1.bak"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(CaptureSessionState.Stopping)]
    [InlineData(CaptureSessionState.WaitingForStorage)]
    public async Task RestartMarksIntermediateSessionInterruptedAndPreservesRecords(CaptureSessionState state)
    {
        string root = Path.Combine(Path.GetTempPath(), "IndustrialDataAnalyzer.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new ApplicationPaths(root);
            var repository = new SqliteMeasurementRepository(paths);
            await repository.InitializeAsync();
            CaptureSession session = Session(Guid.NewGuid(), "待恢复设备", DateTimeOffset.UtcNow);
            await repository.CreateSessionAsync(session);
            await repository.SaveBatchAsync([Record(session, DateTimeOffset.UtcNow, "12", 12)]);
            await repository.SetSessionStateAsync(session.Id, state);
            Assert.Equal(state, Assert.Single(await repository.ListSessionsAsync()).State);

            var reopened = new SqliteMeasurementRepository(paths);
            await reopened.InitializeAsync();
            CaptureSession recovered = Assert.Single(await reopened.ListSessionsAsync());
            Assert.Equal(CaptureSessionState.Interrupted, recovered.State);
            Assert.Equal(1, recovered.ValidCount);
            Assert.Single((await reopened.QueryAsync(new HistoryQuery(SessionId: session.Id))).Records);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CompletingSessionAgainAfterCommitIsIdempotent()
    {
        string root = Path.Combine(Path.GetTempPath(), "IndustrialDataAnalyzer.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new ApplicationPaths(root);
            var repository = new SqliteMeasurementRepository(paths);
            await repository.InitializeAsync();
            CaptureSession session = Session(Guid.NewGuid(), "重试设备", DateTimeOffset.UtcNow);
            await repository.CreateSessionAsync(session);
            await repository.SetSessionStateAsync(session.Id, CaptureSessionState.Stopping);
            await repository.CompleteSessionAsync(session.Id, CaptureSessionState.Failed, 2);
            await repository.CompleteSessionAsync(session.Id, CaptureSessionState.Failed, 2);
            CaptureSession completed = Assert.Single(await repository.ListSessionsAsync());
            Assert.Equal(CaptureSessionState.Failed, completed.State);
            Assert.Equal(2, completed.LostCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static CaptureSession Session(Guid deviceId, string name, DateTimeOffset start) =>
        new(Guid.NewGuid(), deviceId, name, start.ToUniversalTime(), null, 0, 0, 0, 0, CaptureSessionState.Active);

    private static MeasurementRecord Record(CaptureSession session, DateTimeOffset captured,
        string raw, double? value, string? error = null) =>
        new(Guid.NewGuid(), session.Id, session.DeviceId, captured, FrameSource.Simulator,
            raw, "测量值", value, "℃", error);
}
