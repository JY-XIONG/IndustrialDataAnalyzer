namespace IndustrialDataAnalyzer.Core.Models;

/// <summary>Describes one configured serial or simulated device.</summary>
public sealed record DeviceDefinition
{
    /// <summary>Gets the stable device identifier.</summary>
    public Guid Id { get; init; }
    /// <summary>Gets the user-facing name.</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Gets the unique device code.</summary>
    public string Code { get; init; } = string.Empty;
    /// <summary>Gets the communication device kind.</summary>
    public DeviceKind Kind { get; init; }
    /// <summary>Gets serial communication settings.</summary>
    public SerialOptions Serial { get; init; } = new();
    /// <summary>Gets simulator output settings.</summary>
    public SimulatorOptions Simulator { get; init; } = new();
    /// <summary>Gets frame parsing settings.</summary>
    public ParserOptions Parser { get; init; } = new();
    /// <summary>Gets optional operator notes.</summary>
    public string Notes { get; init; } = string.Empty;
}

/// <summary>Identifies the physical or simulated communication channel.</summary>
public enum DeviceKind { Serial, Simulator }

/// <summary>Identifies supported simulator measurement types.</summary>
public enum MeasurementKind { Temperature, Weight, Voltage }

/// <summary>Identifies the frame parsing strategy.</summary>
public enum ParserMode { Numeric, Delimited, Regex }

/// <summary>Identifies the live connection state.</summary>
public enum ConnectionState { Disconnected, Connecting, Connected, Faulted }

/// <summary>Identifies the source of a received frame.</summary>
public enum FrameSource { Serial, Simulator }
