using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IndustrialDataAnalyzer.App.ViewModels.Pages;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;

namespace IndustrialDataAnalyzer.App.ViewModels;

/// <summary>Owns navigation state for the single application window.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<NavigationPage, PageViewModel> pages;
    private readonly NavigationItem[] navigationItems;

    /// <summary>Creates the window state from registered page view models.</summary>
    public MainWindowViewModel(IEnumerable<PageViewModel> pageViewModels, IApplicationInfo applicationInfo, IDeviceManager? deviceManager = null)
    {
        pages = pageViewModels.ToDictionary(page => page.Page);
        ApplicationInfo = applicationInfo;
        WorkspaceItems = [new(NavigationPage.Overview, "系统总览", "\uE80F")];
        DeviceItems = [
            new(NavigationPage.Devices, "设备管理", "\uE964"),
            new(NavigationPage.Acquisition, "实时数据采集", "\uE9D9"),
            new(NavigationPage.Monitoring, "实时监测", "\uE9D2")
        ];
        DataItems = [
            new(NavigationPage.History, "历史数据分析", "\uE9D2"),
            new(NavigationPage.Alarms, "报警中心", "\uE7BA"),
            new(NavigationPage.Reports, "报表管理", "\uE8A5")
        ];
        SystemItems = [new(NavigationPage.Settings, "系统设置", "\uE713")];
        navigationItems = [.. WorkspaceItems, .. DeviceItems, .. DataItems, .. SystemItems];
        CurrentPage = pages[NavigationPage.Overview];
        navigationItems[0].IsSelected = true;
        if (deviceManager is not null)
        {
            void RefreshStatus() => UiThread.Post(() => OnlineStatus = $"在线设备：{deviceManager.Devices.Count(device => deviceManager.GetState(device.Id) == ConnectionState.Connected)}");
            deviceManager.DevicesChanged += RefreshStatus;
            deviceManager.StateChanged += _ => RefreshStatus();
            RefreshStatus();
        }
    }

    /// <summary>Gets immutable application information.</summary>
    public IApplicationInfo ApplicationInfo { get; }
    /// <summary>Gets workspace navigation items.</summary>
    public IReadOnlyList<NavigationItem> WorkspaceItems { get; }
    /// <summary>Gets device navigation items.</summary>
    public IReadOnlyList<NavigationItem> DeviceItems { get; }
    /// <summary>Gets data center navigation items.</summary>
    public IReadOnlyList<NavigationItem> DataItems { get; }
    /// <summary>Gets system navigation items.</summary>
    public IReadOnlyList<NavigationItem> SystemItems { get; }

    [ObservableProperty]
    private string onlineStatus = "在线设备：0";

    [ObservableProperty]
    private PageViewModel currentPage;

    [RelayCommand]
    private void Navigate(NavigationItem? item)
    {
        if (item is null || !pages.TryGetValue(item.Page, out var page))
        {
            return;
        }

        CurrentPage = page;
        if (page is OverviewViewModel overview) _ = overview.RefreshTodayAsync();
        foreach (var navigationItem in navigationItems)
        {
            navigationItem.IsSelected = navigationItem.Page == item.Page;
        }
    }
}
