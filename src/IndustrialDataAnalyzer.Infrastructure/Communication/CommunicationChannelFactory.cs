using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;
using Microsoft.Extensions.Logging;

namespace IndustrialDataAnalyzer.Infrastructure.Communication;

/// <summary>Creates interchangeable serial and simulated channels.</summary>
public sealed class CommunicationChannelFactory(ILoggerFactory loggerFactory) : ICommunicationChannelFactory
{
    /// <inheritdoc />
    public ICommunicationChannel Create(DeviceDefinition device) => device.Kind switch
    {
        DeviceKind.Serial => new SerialDeviceChannel(device, loggerFactory.CreateLogger<SerialDeviceChannel>()),
        DeviceKind.Simulator => new SimulatedDeviceChannel(device, loggerFactory.CreateLogger<SimulatedDeviceChannel>()),
        _ => throw new ArgumentOutOfRangeException(nameof(device), "未知设备类型。")
    };
}
