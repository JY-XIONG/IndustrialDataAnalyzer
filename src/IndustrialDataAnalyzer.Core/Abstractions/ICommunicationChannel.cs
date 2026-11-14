using IndustrialDataAnalyzer.Core.Models;

namespace IndustrialDataAnalyzer.Core.Abstractions;

/// <summary>Represents a live serial or simulated device channel.</summary>
public interface ICommunicationChannel : IAsyncDisposable
{
    /// <summary>Occurs when a complete frame arrives.</summary>
    event Action<RawFrame>? FrameReceived;
    /// <summary>Occurs when the channel state changes.</summary>
    event Action<DeviceStateChange>? StateChanged;
    /// <summary>Gets the current connection state.</summary>
    ConnectionState State { get; }
    /// <summary>Opens the channel and starts receiving frames.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);
    /// <summary>Stops receiving and releases the channel.</summary>
    Task DisconnectAsync();
    /// <summary>Sends a text frame to a connected channel.</summary>
    Task SendAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>Creates a communication channel for a configured device.</summary>
public interface ICommunicationChannelFactory
{
    /// <summary>Creates a fresh disconnected channel.</summary>
    ICommunicationChannel Create(DeviceDefinition device);
}
