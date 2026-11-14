using IndustrialDataAnalyzer.Core.Models;

namespace IndustrialDataAnalyzer.Core.Abstractions;

public interface IMeasurementRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task CreateSessionAsync(CaptureSession session, CancellationToken cancellationToken = default);
    Task SaveBatchAsync(IReadOnlyList<MeasurementRecord> records, CancellationToken cancellationToken = default);
    Task CompleteSessionAsync(Guid sessionId, CaptureSessionState state, long lostCount,
        CancellationToken cancellationToken = default);
    Task SetLostCountAsync(Guid sessionId, long lostCount, CancellationToken cancellationToken = default);
    Task<HistoryPage> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HistoricalDevice>> ListDevicesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CaptureSession>> ListSessionsAsync(Guid? deviceId = null,
        DateTimeOffset? fromUtc = null, DateTimeOffset? toUtcExclusive = null,
        int limit = 200, CancellationToken cancellationToken = default);
    Task<long> CountSinceAsync(DateTimeOffset fromUtc, CancellationToken cancellationToken = default);
    Task CheckpointAndBackupAsync(CancellationToken cancellationToken = default);
}
