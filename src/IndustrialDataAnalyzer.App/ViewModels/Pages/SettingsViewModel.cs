using IndustrialDataAnalyzer.Core.Abstractions;

namespace IndustrialDataAnalyzer.App.ViewModels.Pages;

/// <summary>Provides application information for the settings page.</summary>
public sealed class SettingsViewModel(IApplicationInfo info) : PageViewModel(NavigationPage.Settings, "系统设置", "查看软件信息与本机存储位置")
{
    /// <summary>Gets the current application information.</summary>
    public IApplicationInfo Info { get; } = info;
}
