namespace IndustrialDataAnalyzer.Core.Models;

// Values 0-3 are already stored in SQLite; append new states without renumbering them.
public enum CaptureSessionState { Active, Completed, Failed, Interrupted, Stopping, WaitingForStorage }

public sealed record CaptureSession(
    Guid Id, Guid DeviceId, string DeviceName, DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc, long TotalCount, long ValidCount, long FailedCount,
    long LostCount, CaptureSessionState State);

public sealed record MeasurementRecord(
    Guid Id, Guid SessionId, Guid DeviceId, DateTimeOffset CapturedUtc,
    FrameSource Source, string RawText, string FieldName, double? Value,
    string Unit, string? Error, string DeviceName = "")
{
    public bool IsSuccess => Value.HasValue && Error is null;
}

public sealed record HistoryQuery(
    Guid? DeviceId = null, Guid? SessionId = null,
    DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtcExclusive = null,
    int Page = 1, int PageSize = 50);

public sealed record HistoryPage(IReadOnlyList<MeasurementRecord> Records, long TotalCount);

public sealed record HistoricalDevice(Guid Id, string Name);

public sealed record CaptureStatus(
    Guid DeviceId, Guid? SessionId, CaptureSessionState? State,
    long SavedCount, long PendingCount, long LostCount, string? Error);
