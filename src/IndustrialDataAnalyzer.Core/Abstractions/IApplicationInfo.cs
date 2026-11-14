namespace IndustrialDataAnalyzer.Core.Abstractions;

/// <summary>Exposes information displayed by the application shell.</summary>
public interface IApplicationInfo
{
    /// <summary>Gets the Chinese product name.</summary>
    string Name { get; }

    /// <summary>Gets the product version.</summary>
    string Version { get; }

    /// <summary>Gets the current operating system, process architecture, and .NET version.</summary>
    string EnvironmentName { get; }

    /// <summary>Gets the writable data directory.</summary>
    string DataDirectory { get; }

    /// <summary>Gets the log directory.</summary>
    string LogsDirectory { get; }
}
