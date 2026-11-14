using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;
using Microsoft.Data.Sqlite;

namespace IndustrialDataAnalyzer.Infrastructure.Persistence;

/// <summary>SQLite storage. SQLite calls are synchronous internally, so every public operation runs off the UI thread.</summary>
public sealed class SqliteMeasurementRepository : IMeasurementRepository
{
    private const int SchemaVersion = 1;
    private readonly string filePath;
    private readonly string connectionString;

    public SqliteMeasurementRepository(IApplicationPaths paths)
    {
        filePath = Path.Combine(paths.DataDirectory, "measurements.db");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        bool existed = File.Exists(filePath) && new FileInfo(filePath).Length > 0;
        using var connection = Open();
        using (var mode = Command(connection, "PRAGMA journal_mode=WAL;"))
        {
            if (!string.Equals((string?)mode.ExecuteScalar(), "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SQLite 未能启用 WAL 模式。");
        }
        using var versionCommand = Command(connection, "PRAGMA user_version;");
        long version = (long)(versionCommand.ExecuteScalar() ?? 0L);
        if (version > SchemaVersion) throw new InvalidDataException($"数据库版本 {version} 高于软件支持的 {SchemaVersion}。");
        if (version < SchemaVersion)
        {
            if (existed) Backup(connection, filePath + ".before-v1.bak");
            using var migration = connection.BeginTransaction();
            using var create = Command(connection, """
                CREATE TABLE IF NOT EXISTS capture_sessions (
                    id TEXT PRIMARY KEY,
                    device_id TEXT NOT NULL,
                    device_name TEXT NOT NULL,
                    started_utc_ms INTEGER NOT NULL,
                    ended_utc_ms INTEGER,
                    total_count INTEGER NOT NULL DEFAULT 0,
                    valid_count INTEGER NOT NULL DEFAULT 0,
                    failed_count INTEGER NOT NULL DEFAULT 0,
                    lost_count INTEGER NOT NULL DEFAULT 0,
                    state INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS measurements (
                    id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL REFERENCES capture_sessions(id),
                    device_id TEXT NOT NULL,
                    captured_utc_ms INTEGER NOT NULL,
                    source INTEGER NOT NULL,
                    raw_text TEXT NOT NULL,
                    field_name TEXT NOT NULL,
                    value REAL,
                    parse_status INTEGER NOT NULL,
                    unit TEXT NOT NULL,
                    error TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_sessions_device_time ON capture_sessions(device_id, started_utc_ms DESC);
                CREATE INDEX IF NOT EXISTS ix_measurements_device_time ON measurements(device_id, captured_utc_ms DESC, id DESC);
                CREATE INDEX IF NOT EXISTS ix_measurements_session_time ON measurements(session_id, captured_utc_ms DESC, id DESC);
                CREATE INDEX IF NOT EXISTS ix_measurements_time ON measurements(captured_utc_ms DESC);
                PRAGMA user_version=1;
                """, migration);
            create.ExecuteNonQuery();
            migration.Commit();
        }
        using var recover = Command(connection, """
            UPDATE capture_sessions SET state=@interrupted, ended_utc_ms=@now
            WHERE state=@active;
            """);
        recover.Parameters.AddWithValue("@interrupted", (int)CaptureSessionState.Interrupted);
        recover.Parameters.AddWithValue("@active", (int)CaptureSessionState.Active);
        recover.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        recover.ExecuteNonQuery();
    }, cancellationToken);

    public Task CreateSessionAsync(CaptureSession session, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = Command(connection, """
            INSERT INTO capture_sessions(id,device_id,device_name,started_utc_ms,state)
            VALUES(@id,@device,@name,@started,@state);
            """);
        command.Parameters.AddWithValue("@id", session.Id.ToString("N"));
        command.Parameters.AddWithValue("@device", session.DeviceId.ToString("N"));
        command.Parameters.AddWithValue("@name", session.DeviceName);
        command.Parameters.AddWithValue("@started", session.StartedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@state", (int)session.State);
        command.ExecuteNonQuery();
    }, cancellationToken);

    public Task SaveBatchAsync(IReadOnlyList<MeasurementRecord> records, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (records.Count == 0) return;
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var counts = new Dictionary<Guid, (long Valid, long Failed)>();
        using var insert = Command(connection, """
            INSERT OR IGNORE INTO measurements
            (id,session_id,device_id,captured_utc_ms,source,raw_text,field_name,value,parse_status,unit,error)
            VALUES(@id,@session,@device,@time,@source,@raw,@field,@value,@status,@unit,@error);
            """, transaction);
        foreach (MeasurementRecord record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("@id", record.Id.ToString("N"));
            insert.Parameters.AddWithValue("@session", record.SessionId.ToString("N"));
            insert.Parameters.AddWithValue("@device", record.DeviceId.ToString("N"));
            insert.Parameters.AddWithValue("@time", record.CapturedUtc.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("@source", (int)record.Source);
            insert.Parameters.AddWithValue("@raw", record.RawText);
            insert.Parameters.AddWithValue("@field", record.FieldName);
            insert.Parameters.AddWithValue("@value", (object?)record.Value ?? DBNull.Value);
            insert.Parameters.AddWithValue("@status", record.IsSuccess ? 1 : 0);
            insert.Parameters.AddWithValue("@unit", record.Unit);
            insert.Parameters.AddWithValue("@error", (object?)record.Error ?? DBNull.Value);
            if (insert.ExecuteNonQuery() == 0) continue;
            counts.TryGetValue(record.SessionId, out var count);
            counts[record.SessionId] = record.IsSuccess ? (count.Valid + 1, count.Failed) : (count.Valid, count.Failed + 1);
        }
        using var update = Command(connection, """
            UPDATE capture_sessions SET total_count=total_count+@total,
                valid_count=valid_count+@valid, failed_count=failed_count+@failed
            WHERE id=@session;
            """, transaction);
        foreach (var (sessionId, count) in counts)
        {
            update.Parameters.Clear();
            update.Parameters.AddWithValue("@session", sessionId.ToString("N"));
            update.Parameters.AddWithValue("@total", count.Valid + count.Failed);
            update.Parameters.AddWithValue("@valid", count.Valid);
            update.Parameters.AddWithValue("@failed", count.Failed);
            if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("采集会话不存在，批量写入已回滚。");
        }
        transaction.Commit();
    }, cancellationToken);

    public Task CompleteSessionAsync(Guid sessionId, CaptureSessionState state, long lostCount,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = Command(connection, """
            UPDATE capture_sessions SET state=@state, ended_utc_ms=@ended,
                total_count=valid_count+failed_count+@lost, lost_count=@lost
            WHERE id=@id AND state=@active;
            """);
        command.Parameters.AddWithValue("@state", (int)state);
        command.Parameters.AddWithValue("@ended", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@lost", lostCount);
        command.Parameters.AddWithValue("@id", sessionId.ToString("N"));
        command.Parameters.AddWithValue("@active", (int)CaptureSessionState.Active);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("采集会话不存在或已结束。");
    }, cancellationToken);

    public Task SetLostCountAsync(Guid sessionId, long lostCount, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = Command(connection, """
            UPDATE capture_sessions SET lost_count=MAX(lost_count,@lost),
                total_count=valid_count+failed_count+MAX(lost_count,@lost)
            WHERE id=@id;
            """);
        command.Parameters.AddWithValue("@id", sessionId.ToString("N"));
        command.Parameters.AddWithValue("@lost", lostCount);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("采集会话不存在，无法记录丢失数。");
    }, cancellationToken);

    public Task<HistoryPage> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        int page = Math.Max(1, query.Page);
        int size = Math.Clamp(query.PageSize, 1, 200);
        using var connection = Open();
        string where = BuildWhere(query);
        using var count = Command(connection, "SELECT COUNT(*) FROM measurements m WHERE " + where);
        BindFilter(count, query);
        long total = (long)(count.ExecuteScalar() ?? 0L);
        using var select = Command(connection, """
            SELECT m.id,m.session_id,m.device_id,m.captured_utc_ms,m.source,m.raw_text,
                m.field_name,m.value,m.unit,m.error,s.device_name
            FROM measurements m JOIN capture_sessions s ON s.id=m.session_id
            WHERE
            """ + where + " ORDER BY m.captured_utc_ms DESC,m.id DESC LIMIT @limit OFFSET @offset;");
        BindFilter(select, query);
        select.Parameters.AddWithValue("@limit", size);
        select.Parameters.AddWithValue("@offset", (long)(page - 1) * size);
        var records = new List<MeasurementRecord>(size);
        using var reader = select.ExecuteReader();
        while (reader.Read()) records.Add(new MeasurementRecord(
            Guid.ParseExact(reader.GetString(0), "N"), Guid.ParseExact(reader.GetString(1), "N"),
            Guid.ParseExact(reader.GetString(2), "N"), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
            (FrameSource)reader.GetInt32(4), reader.GetString(5), reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetDouble(7), reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10)));
        return new HistoryPage(records, total);
    }, cancellationToken);

    public Task<IReadOnlyList<CaptureSession>> ListSessionsAsync(Guid? deviceId = null,
        DateTimeOffset? fromUtc = null, DateTimeOffset? toUtcExclusive = null,
        int limit = 200, CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<CaptureSession>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = Command(connection, """
            SELECT id,device_id,device_name,started_utc_ms,ended_utc_ms,total_count,
                valid_count,failed_count,lost_count,state
            FROM capture_sessions WHERE (@device IS NULL OR device_id=@device)
                AND (@from IS NULL OR ended_utc_ms IS NULL OR ended_utc_ms>=@from)
                AND (@to IS NULL OR started_utc_ms<@to)
            ORDER BY started_utc_ms DESC LIMIT @limit;
            """);
        command.Parameters.AddWithValue("@device", deviceId.HasValue ? deviceId.Value.ToString("N") : DBNull.Value);
        command.Parameters.AddWithValue("@from", fromUtc.HasValue ? fromUtc.Value.ToUnixTimeMilliseconds() : DBNull.Value);
        command.Parameters.AddWithValue("@to", toUtcExclusive.HasValue ? toUtcExclusive.Value.ToUnixTimeMilliseconds() : DBNull.Value);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000));
        var sessions = new List<CaptureSession>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) sessions.Add(new CaptureSession(
            Guid.ParseExact(reader.GetString(0), "N"), Guid.ParseExact(reader.GetString(1), "N"),
            reader.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
            reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
            reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8),
            (CaptureSessionState)reader.GetInt32(9)));
        return sessions;
    }, cancellationToken);

    public Task<IReadOnlyList<HistoricalDevice>> ListDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<HistoricalDevice>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = Open();
            using var command = Command(connection, """
                SELECT device_id,device_name FROM (
                    SELECT device_id,device_name,ROW_NUMBER() OVER (
                        PARTITION BY device_id ORDER BY started_utc_ms DESC,id DESC) AS rank
                    FROM capture_sessions
                ) WHERE rank=1 ORDER BY device_name;
                """);
            var devices = new List<HistoricalDevice>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) devices.Add(new HistoricalDevice(Guid.ParseExact(reader.GetString(0), "N"),
                reader.GetString(1)));
            return devices;
        }, cancellationToken);

    public Task<long> CountSinceAsync(DateTimeOffset fromUtc, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = Command(connection, "SELECT COUNT(*) FROM measurements WHERE captured_utc_ms>=@from;");
        command.Parameters.AddWithValue("@from", fromUtc.ToUnixTimeMilliseconds());
        return (long)(command.ExecuteScalar() ?? 0L);
    }, cancellationToken);

    public Task CheckpointAndBackupAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using (var checkpoint = Command(connection, "PRAGMA wal_checkpoint(TRUNCATE);"))
        using (var result = checkpoint.ExecuteReader())
        {
            if (!result.Read() || result.GetInt32(0) != 0)
                throw new IOException("SQLite WAL 检查点未完成，请关闭其他数据库连接后重试备份。");
        }
        Backup(connection, filePath + ".bak");
    }, cancellationToken);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            using var foreignKeys = Command(connection, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
            foreignKeys.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private static void Backup(SqliteConnection source, string backupPath)
    {
        string temporary = backupPath + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = temporary, Pooling = false
        }.ToString()))
        {
            destination.Open();
            source.BackupDatabase(destination);
        }
        File.Move(temporary, backupPath, true);
    }

    private static string BuildWhere(HistoryQuery query) =>
        "(@device IS NULL OR m.device_id=@device) AND (@session IS NULL OR m.session_id=@session) " +
        "AND (@from IS NULL OR m.captured_utc_ms>=@from) AND (@to IS NULL OR m.captured_utc_ms<@to)";

    private static void BindFilter(SqliteCommand command, HistoryQuery query)
    {
        command.Parameters.AddWithValue("@device", query.DeviceId.HasValue ? query.DeviceId.Value.ToString("N") : DBNull.Value);
        command.Parameters.AddWithValue("@session", query.SessionId.HasValue ? query.SessionId.Value.ToString("N") : DBNull.Value);
        command.Parameters.AddWithValue("@from", query.FromUtc.HasValue ? query.FromUtc.Value.ToUnixTimeMilliseconds() : DBNull.Value);
        command.Parameters.AddWithValue("@to", query.ToUtcExclusive.HasValue ? query.ToUtcExclusive.Value.ToUnixTimeMilliseconds() : DBNull.Value);
    }
}
