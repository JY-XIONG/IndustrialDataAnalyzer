using IndustrialDataAnalyzer.Core.Models;

namespace IndustrialDataAnalyzer.Core.Abstractions;

/// <summary>Persists the device catalog as a whole.</summary>
public interface IDeviceRepository
{
    /// <summary>Loads all configured devices.</summary>
    Task<IReadOnlyList<DeviceDefinition>> LoadAsync(CancellationToken cancellationToken = default);
    /// <summary>Atomically replaces all configured devices.</summary>
    Task SaveAsync(IReadOnlyList<DeviceDefinition> devices, CancellationToken cancellationToken = default);
}
