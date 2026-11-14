namespace IndustrialDataAnalyzer.Core.Abstractions;

/// <summary>Provides writable application data locations.</summary>
public interface IApplicationPaths
{
    /// <summary>Gets the root directory for user data.</summary>
    string DataDirectory { get; }

    /// <summary>Gets the directory for rolling log files.</summary>
    string LogsDirectory { get; }

    /// <summary>Creates the directories required during startup.</summary>
    void Initialize();
}
