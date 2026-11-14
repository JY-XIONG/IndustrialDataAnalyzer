using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;

namespace IndustrialDataAnalyzer.App.ViewModels.Pages;

/// <summary>Shows live device counts in the existing overview layout.</summary>
public partial class OverviewViewModel : PageViewModel
{
    private readonly IDeviceManager? manager;

    public OverviewViewModel(IDeviceManager? manager = null)
        : base(NavigationPage.Overview, "系统总览", "查看设备、采集和报警的整体情况")
    {
        this.manager = manager;
        if (manager is not null)
        {
            manager.DevicesChanged += () => UiThread.Post(Refresh);
            manager.StateChanged += _ => UiThread.Post(Refresh);
            manager.FrameProcessed += frame => UiThread.Post(() => OnFrame(frame));
            Refresh();
        }
    }

    [ObservableProperty] private int totalDevices;
    [ObservableProperty] private int onlineDevices;
    [ObservableProperty] private int disconnectedDevices;
    [ObservableProperty] private int faultedDevices;
    [ObservableProperty] private string statusSummary = "暂无设备状态";
    [ObservableProperty] private int sessionFrames;
    [ObservableProperty] private string latestFrameSummary = "暂无采集记录";
    public ObservableCollection<string> DeviceStatusLines { get; } = [];

    private void Refresh()
    {
        if (manager is null) return;
        DeviceDefinition[] devices = [.. manager.Devices];
        TotalDevices = devices.Length;
        OnlineDevices = devices.Count(device => manager.GetState(device.Id) == ConnectionState.Connected);
        FaultedDevices = devices.Count(device => manager.GetState(device.Id) == ConnectionState.Faulted);
        DisconnectedDevices = TotalDevices - OnlineDevices - FaultedDevices;
        StatusSummary = TotalDevices == 0 ? "暂无设备状态" :
            $"已配置 {TotalDevices} 台设备，在线 {OnlineDevices} 台。";
        DeviceStatusLines.Clear();
        foreach (DeviceDefinition device in devices.Take(4))
        {
            ConnectionState state = manager.GetState(device.Id);
            DeviceStatusLines.Add($"{device.Name} · {state switch
            {
                ConnectionState.Connected => "已连接",
                ConnectionState.Connecting => "连接中",
                ConnectionState.Faulted => "连接异常",
                _ => "未连接"
            }}");
        }
        if (devices.Length > 4) DeviceStatusLines.Add($"另有 {devices.Length - 4} 台设备…");
    }

    private void OnFrame(ParsedFrame frame)
    {
        SessionFrames++;
        string name = manager?.Devices.FirstOrDefault(device => device.Id == frame.Raw.DeviceId)?.Name ?? "设备";
        LatestFrameSummary = frame.IsSuccess
            ? $"{frame.Raw.Timestamp:HH:mm:ss} · {name} · {frame.FieldName} {frame.Value:G} {frame.Unit}"
            : $"{frame.Raw.Timestamp:HH:mm:ss} · {name} · 解析失败：{frame.Error}";
    }
}
