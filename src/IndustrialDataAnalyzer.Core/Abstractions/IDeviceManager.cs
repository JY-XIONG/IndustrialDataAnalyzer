using IndustrialDataAnalyzer.Core.Models;

namespace IndustrialDataAnalyzer.Core.Abstractions;

/// <summary>Coordinates device catalog changes and live channels.</summary>
public interface IDeviceManager : IAsyncDisposable
{
    /// <summary>Occurs after the catalog changes.</summary>
    event Action? DevicesChanged;
    /// <summary>Occurs after a device state changes.</summary>
    event Action<DeviceStateChange>? StateChanged;
    /// <summary>Occurs when a frame has been parsed.</summary>
    event Action<ParsedFrame>? FrameProcessed;
    /// <summary>Gets a snapshot of configured devices.</summary>
    IReadOnlyList<DeviceDefinition> Devices { get; }
    /// <summary>Loads saved devices at application startup.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
    /// <summary>Adds or updates one device.</summary>
    Task UpsertAsync(DeviceDefinition device, CancellationToken cancellationToken = default);
    /// <summary>Deletes a device and disconnects it first if needed.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>Gets a device's live connection state.</summary>
    ConnectionState GetState(Guid id);
    /// <summary>Connects a configured device.</summary>
    Task ConnectAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>Disconnects a configured device.</summary>
    Task DisconnectAsync(Guid id);
    /// <summary>Sends a text frame to a connected device.</summary>
    Task SendAsync(Guid id, string text, CancellationToken cancellationToken = default);
    /// <summary>Safely closes every open channel.</summary>
    Task DisconnectAllAsync();
}
