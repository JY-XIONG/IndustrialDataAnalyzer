namespace IndustrialDataAnalyzer.Core.Models;

/// <summary>One complete text frame emitted by a communication channel.</summary>
public sealed record RawFrame(Guid DeviceId, DateTimeOffset Timestamp, FrameSource Source, string Text);

/// <summary>One frame after parsing with the original text retained.</summary>
public sealed record ParsedFrame(RawFrame Raw, string FieldName, string Unit, double? Value, string? Error)
{
    /// <summary>Gets whether a measurement value was extracted.</summary>
    public bool IsSuccess => Value.HasValue && Error is null;
}

/// <summary>Reports a device connection state change.</summary>
public sealed record DeviceStateChange(Guid DeviceId, ConnectionState State, string? Detail);
