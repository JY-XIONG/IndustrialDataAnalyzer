using IndustrialDataAnalyzer.Core.Models;

namespace IndustrialDataAnalyzer.Core.Abstractions;

/// <summary>Extracts a measurement from a raw text frame.</summary>
public interface IFrameParser
{
    /// <summary>Returns a parsed value or a non-throwing error result.</summary>
    ParsedFrame Parse(RawFrame raw, ParserOptions options);
}

/// <summary>Lists currently available serial ports.</summary>
public interface IPortCatalog
{
    /// <summary>Returns a sorted snapshot of COM port names.</summary>
    IReadOnlyList<string> GetAvailablePorts();
}
