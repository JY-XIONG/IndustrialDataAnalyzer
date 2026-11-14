using IndustrialDataAnalyzer.Core.Models;

namespace IndustrialDataAnalyzer.Core.Abstractions;

public interface ICapturePersistence : IAsyncDisposable
{
    string? StorageError { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<Guid> BeginSessionAsync(DeviceDefinition device, CancellationToken cancellationToken = default);
    bool TryAccept(Guid sessionId, RawFrame frame, ParserOptions options);
    Task EndSessionAsync(Guid sessionId, CaptureSessionState state = CaptureSessionState.Completed,
        CancellationToken cancellationToken = default);
    CaptureStatus GetStatus(Guid deviceId);
}
