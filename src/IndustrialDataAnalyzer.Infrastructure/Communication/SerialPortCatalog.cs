using System.IO.Ports;
using IndustrialDataAnalyzer.Core.Abstractions;

namespace IndustrialDataAnalyzer.Infrastructure.Communication;

/// <summary>Lists COM ports reported by Windows.</summary>
public sealed class SerialPortCatalog : IPortCatalog
{
    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailablePorts() => SerialPort.GetPortNames()
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
