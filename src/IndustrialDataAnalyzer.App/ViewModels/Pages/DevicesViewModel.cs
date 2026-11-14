using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IndustrialDataAnalyzer.App.Services;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;

namespace IndustrialDataAnalyzer.App.ViewModels.Pages;

/// <summary>Manages the searchable device catalog and connection commands.</summary>
public partial class DevicesViewModel : PageViewModel
{
    private readonly IDeviceManager? manager;
    private readonly IDeviceDialogService? dialogs;

    public DevicesViewModel(IDeviceManager? manager = null, IDeviceDialogService? dialogs = null)
        : base(NavigationPage.Devices, "设备管理", "管理设备档案和通信配置")
    {
        this.manager = manager;
        this.dialogs = dialogs;
        if (manager is not null)
        {
            manager.DevicesChanged += () => UiThread.Post(Refresh);
            manager.StateChanged += change => UiThread.Post(() => ApplyState(change));
            Refresh();
        }
    }

    public ObservableCollection<DeviceRowViewModel> Devices { get; } = [];
    public bool HasDevices => Devices.Count > 0;
    public bool HasSelection => SelectedDevice is not null;
    [ObservableProperty] private DeviceRowViewModel? selectedDevice;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private string notice = "可添加真实串口或模拟设备。";
    [ObservableProperty] private bool isBusy;

    partial void OnSelectedDeviceChanged(DeviceRowViewModel? value) => OnPropertyChanged(nameof(HasSelection));
    partial void OnSearchTextChanged(string value) => Refresh();

    [RelayCommand]
    private async Task AddAsync()
    {
        if (manager is null || dialogs is null) return;
        DeviceDefinition? device = dialogs.Edit(null);
        if (device is not null) await RunAsync(() => manager.UpsertAsync(device), "设备已保存。");
    }

    [RelayCommand]
    private async Task EditAsync()
    {
        if (manager is null || dialogs is null || SelectedDevice is null) return;
        DeviceDefinition? device = dialogs.Edit(SelectedDevice.Device);
        if (device is not null) await RunAsync(() => manager.UpsertAsync(device), "设备已更新。");
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (manager is null || dialogs is null || SelectedDevice is null) return;
        DeviceDefinition selected = SelectedDevice.Device;
        if (dialogs.ConfirmDelete(selected)) await RunAsync(() => manager.DeleteAsync(selected.Id), "设备已删除。");
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (manager is null || SelectedDevice is null) return;
        Guid id = SelectedDevice.Id;
        await RunAsync(() => manager.ConnectAsync(id), "设备已连接。若此前连接异常，此操作会重新连接。");
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (manager is null || SelectedDevice is null) return;
        Guid id = SelectedDevice.Id;
        await RunAsync(() => manager.DisconnectAsync(id), "设备已断开。");
    }

    private async Task RunAsync(Func<Task> action, string success)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(); Notice = success; }
        catch (Exception exception) { Notice = "操作失败：" + exception.Message; }
        finally { IsBusy = false; }
    }

    private void Refresh()
    {
        if (manager is null) return;
        Guid? selectedId = SelectedDevice?.Id;
        Devices.Clear();
        foreach (DeviceDefinition device in manager.Devices.Where(Matches))
            Devices.Add(new DeviceRowViewModel(device, manager.GetState(device.Id)));
        SelectedDevice = Devices.FirstOrDefault(item => item.Id == selectedId);
        OnPropertyChanged(nameof(HasDevices));
    }

    private bool Matches(DeviceDefinition device) => string.IsNullOrWhiteSpace(SearchText) ||
        new[] { device.Name, device.Code, device.Notes, device.Serial.PortName, device.Kind.ToString() }
            .Any(value => value.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));

    private void ApplyState(DeviceStateChange change)
    {
        DeviceRowViewModel? row = Devices.FirstOrDefault(item => item.Id == change.DeviceId);
        if (row is not null) row.State = change.State;
        if (change.State == ConnectionState.Faulted) Notice = "连接异常：" + change.Detail;
    }
}
