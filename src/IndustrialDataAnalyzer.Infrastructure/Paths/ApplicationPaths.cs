using IndustrialDataAnalyzer.Core.Abstractions;

namespace IndustrialDataAnalyzer.Infrastructure.Paths;

/// <summary>Locates per-user writable application folders.</summary>
public sealed class ApplicationPaths : IApplicationPaths
{
    /// <summary>Creates paths below a supplied local application data folder.</summary>
    public ApplicationPaths(string localApplicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataDirectory);
        DataDirectory = Path.Combine(localApplicationDataDirectory, "IndustrialDataAnalyzer");
        LogsDirectory = Path.Combine(DataDirectory, "Logs");
        DeviceConfigurationFile = Path.Combine(DataDirectory, "devices.json");
    }

    /// <inheritdoc />
    public string DataDirectory { get; }

    /// <inheritdoc />
    public string LogsDirectory { get; }

    /// <inheritdoc />
    public string DeviceConfigurationFile { get; }

    /// <inheritdoc />
    public void Initialize()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
