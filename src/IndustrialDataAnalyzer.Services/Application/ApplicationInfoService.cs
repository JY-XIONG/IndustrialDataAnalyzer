using IndustrialDataAnalyzer.Core.Abstractions;

namespace IndustrialDataAnalyzer.Services.Application;

/// <summary>Supplies immutable product and runtime information to the UI.</summary>
public sealed class ApplicationInfoService : IApplicationInfo
{
    /// <summary>Creates product information for the current process.</summary>
    public ApplicationInfoService(IApplicationPaths paths, string environmentName, Version version)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        ArgumentNullException.ThrowIfNull(version);
        EnvironmentName = environmentName;
        Version = $"V{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        DataDirectory = paths.DataDirectory;
        LogsDirectory = paths.LogsDirectory;
    }

    /// <inheritdoc />
    public string Name => "通用工业设备数据采集与分析软件";

    /// <inheritdoc />
    public string Version { get; }

    /// <inheritdoc />
    public string EnvironmentName { get; }

    /// <inheritdoc />
    public string DataDirectory { get; }

    /// <inheritdoc />
    public string LogsDirectory { get; }
}
