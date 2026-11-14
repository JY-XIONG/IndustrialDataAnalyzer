namespace IndustrialDataAnalyzer.Core.Models;

/// <summary>Stores the settings needed to open a serial port.</summary>
public sealed record SerialOptions
{
    /// <summary>Gets the COM port name.</summary>
    public string PortName { get; init; } = string.Empty;
    /// <summary>Gets the baud rate.</summary>
    public int BaudRate { get; init; } = 9600;
    /// <summary>Gets the number of data bits.</summary>
    public int DataBits { get; init; } = 8;
    /// <summary>Gets the stop-bit choice.</summary>
    public SerialStopBits StopBits { get; init; } = SerialStopBits.One;
    /// <summary>Gets the parity choice.</summary>
    public SerialParity Parity { get; init; } = SerialParity.None;
    /// <summary>Gets the hardware or software flow control choice.</summary>
    public SerialFlowControl FlowControl { get; init; } = SerialFlowControl.None;
    /// <summary>Gets the encoded frame ending; escaped CR and LF are accepted.</summary>
    public string Terminator { get; init; } = "\\n";
    /// <summary>Gets the text encoding name.</summary>
    public string EncodingName { get; init; } = "UTF-8";
}

/// <summary>Defines how a simulator emits measurement values.</summary>
public sealed record SimulatorOptions
{
    /// <summary>Gets the type of measurement.</summary>
    public MeasurementKind Measurement { get; init; } = MeasurementKind.Temperature;
    /// <summary>Gets the minimum generated value.</summary>
    public double Minimum { get; init; } = 18;
    /// <summary>Gets the maximum generated value.</summary>
    public double Maximum { get; init; } = 30;
    /// <summary>Gets the output interval in milliseconds.</summary>
    public int IntervalMilliseconds { get; init; } = 1000;
}

/// <summary>Defines how to extract one measurement from a text frame.</summary>
public sealed record ParserOptions
{
    /// <summary>Gets the parsing mode.</summary>
    public ParserMode Mode { get; init; } = ParserMode.Numeric;
    /// <summary>Gets the displayed field name.</summary>
    public string FieldName { get; init; } = "测量值";
    /// <summary>Gets the displayed measurement unit.</summary>
    public string Unit { get; init; } = string.Empty;
    /// <summary>Gets the delimiter used in delimited mode.</summary>
    public string Separator { get; init; } = ",";
    /// <summary>Gets the zero-based field index in delimited mode.</summary>
    public int FieldIndex { get; init; }
    /// <summary>Gets the regular expression in regex mode.</summary>
    public string Pattern { get; init; } = @"([-+]?\d+(?:\.\d+)?)";
    /// <summary>Gets the capture group name or number in regex mode.</summary>
    public string CaptureGroup { get; init; } = "1";
}

/// <summary>Identifies supported serial stop bits.</summary>
public enum SerialStopBits { One, OnePointFive, Two }
/// <summary>Identifies supported parity settings.</summary>
public enum SerialParity { None, Odd, Even, Mark, Space }
/// <summary>Identifies supported serial flow control settings.</summary>
public enum SerialFlowControl { None, XOnXOff, RequestToSend, RequestToSendXOnXOff }
