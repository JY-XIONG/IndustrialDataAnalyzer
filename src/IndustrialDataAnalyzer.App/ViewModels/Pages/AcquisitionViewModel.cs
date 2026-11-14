using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;

namespace IndustrialDataAnalyzer.App.ViewModels.Pages;

/// <summary>Displays bounded live raw frames and parsed results for a selected device.</summary>
public partial class AcquisitionViewModel : PageViewModel
{
    private const int HistoryLimit = 200;
    private readonly IDeviceManager? manager;
    private readonly object historyGate = new();
    private readonly Dictionary<Guid, Queue<ParsedFrame>> history = [];
    private readonly HashSet<Guid> dirtyDevices = [];
    private readonly DispatcherTimer? refreshTimer;
    private ParsedFrame? displayedLatest;

    public AcquisitionViewModel(IDeviceManager? manager = null)
        : base(NavigationPage.Acquisition, "实时数据采集", "查看设备报文、解析结果和采集记录")
    {
        this.manager = manager;
        if (manager is not null)
        {
            manager.DevicesChanged += () => UiThread.Post(RefreshDevices);
            manager.StateChanged += change => UiThread.Post(() => OnState(change));
            manager.FrameProcessed += OnFrame;
            RefreshDevices();
            if (Application.Current?.Dispatcher is Dispatcher dispatcher)
            {
                refreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                refreshTimer.Tick += (_, _) => FlushPendingFrames();
                refreshTimer.Start();
            }
        }
    }

    public ObservableCollection<DeviceDefinition> Devices { get; } = [];
    public ObservableCollection<FrameDisplay> RecentFrames { get; } = [];
    public bool HasSelectedDevice => SelectedDevice is not null;
    public bool IsSerial => SelectedDevice?.Kind == DeviceKind.Serial;
    [ObservableProperty] private DeviceDefinition? selectedDevice;
    [ObservableProperty] private string statusText = "未选择设备";
    [ObservableProperty] private string latestValue = "—";
    [ObservableProperty] private string latestField = "暂无测量值";
    [ObservableProperty] private string latestRaw = "暂无报文";
    [ObservableProperty] private string latestResult = "暂无解析结果";
    [ObservableProperty] private string sendText = string.Empty;
    [ObservableProperty] private string notice = "选择设备后点击启动采集。";
    [ObservableProperty] private bool isBusy;

    partial void OnSelectedDeviceChanged(DeviceDefinition? value)
    {
        OnPropertyChanged(nameof(HasSelectedDevice));
        OnPropertyChanged(nameof(IsSerial));
        UpdateState();
        ShowHistory();
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (manager is null || SelectedDevice is null) return;
        Guid id = SelectedDevice.Id;
        await RunAsync(() => manager.ConnectAsync(id), "采集已启动。", "启动采集失败：");
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (manager is null || SelectedDevice is null) return;
        Guid id = SelectedDevice.Id;
        await RunAsync(() => manager.DisconnectAsync(id), "采集已停止。", "停止采集失败：");
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (manager is null || SelectedDevice?.Kind != DeviceKind.Serial || string.IsNullOrEmpty(SendText)) return;
        Guid id = SelectedDevice.Id;
        string text = SendText;
        await RunAsync(() => manager.SendAsync(id, text), "报文已发送。", "发送失败：");
    }

    private async Task RunAsync(Func<Task> action, string success, string prefix)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(); Notice = success; }
        catch (Exception exception) { Notice = prefix + exception.Message; }
        finally { IsBusy = false; UpdateState(); }
    }

    private void RefreshDevices()
    {
        if (manager is null) return;
        Guid? selectedId = SelectedDevice?.Id;
        Devices.Clear();
        foreach (DeviceDefinition device in manager.Devices) Devices.Add(device);
        lock (historyGate)
        {
            var active = manager.Devices.Select(device => device.Id).ToHashSet();
            foreach (Guid id in history.Keys.Where(id => !active.Contains(id)).ToArray()) history.Remove(id);
            dirtyDevices.RemoveWhere(id => !active.Contains(id));
        }
        SelectedDevice = Devices.FirstOrDefault(device => device.Id == selectedId) ?? Devices.FirstOrDefault();
        UpdateState();
    }

    private void OnState(DeviceStateChange change)
    {
        if (SelectedDevice?.Id != change.DeviceId) return;
        UpdateState();
        if (change.State == ConnectionState.Faulted) Notice = "通信异常：" + change.Detail + "。可手动重连。";
        else if (change.State == ConnectionState.Connected) Notice = "设备已连接，正在接收报文。";
        else if (change.State == ConnectionState.Disconnected) Notice = "设备已断开，可重新启动采集。";
        else Notice = "正在连接设备…";
    }

    private void UpdateState()
    {
        if (manager is null || SelectedDevice is null) { StatusText = "未选择设备"; return; }
        StatusText = manager.GetState(SelectedDevice.Id) switch
        {
            ConnectionState.Connected => "已连接 · 正在接收",
            ConnectionState.Connecting => "正在连接",
            ConnectionState.Faulted => "连接异常 · 可重连",
            _ => "未连接 · 已停止"
        };
    }

    private void OnFrame(ParsedFrame frame)
    {
        lock (historyGate)
        {
            if (!history.TryGetValue(frame.Raw.DeviceId, out Queue<ParsedFrame>? entries))
            {
                entries = new Queue<ParsedFrame>(HistoryLimit);
                history[frame.Raw.DeviceId] = entries;
            }
            if (entries.Count == HistoryLimit) entries.Dequeue();
            entries.Enqueue(frame);
            dirtyDevices.Add(frame.Raw.DeviceId);
        }
    }

    internal int StoredFrameCount(Guid id)
    {
        lock (historyGate) return history.TryGetValue(id, out Queue<ParsedFrame>? entries) ? entries.Count : 0;
    }

    internal void FlushPendingFrames()
    {
        if (SelectedDevice is null) return;
        ParsedFrame[] snapshot;
        lock (historyGate)
        {
            if (!dirtyDevices.Remove(SelectedDevice.Id) || !history.TryGetValue(SelectedDevice.Id, out Queue<ParsedFrame>? entries)) return;
            snapshot = [.. entries];
        }
        RenderHistory(snapshot);
    }

    private void ShowHistory()
    {
        displayedLatest = null;
        RecentFrames.Clear();
        if (SelectedDevice is null)
        {
            LatestValue = "—"; LatestField = "暂无测量值"; LatestRaw = "暂无报文"; LatestResult = "暂无解析结果";
            return;
        }
        ParsedFrame[] snapshot;
        lock (historyGate)
        {
            snapshot = history.TryGetValue(SelectedDevice.Id, out Queue<ParsedFrame>? entries) ? [.. entries] : [];
            dirtyDevices.Remove(SelectedDevice.Id);
        }
        if (snapshot.Length == 0)
        {
            LatestValue = "—"; LatestField = "暂无测量值"; LatestRaw = "暂无报文"; LatestResult = "暂无解析结果";
            return;
        }
        RenderHistory(snapshot);
    }

    private void RenderHistory(ParsedFrame[] entries)
    {
        int previousIndex = displayedLatest is null ? -1 : Array.FindLastIndex(entries, entry => ReferenceEquals(entry, displayedLatest));
        if (displayedLatest is not null && previousIndex < 0)
        {
            RecentFrames.Clear();
        }
        int start = Math.Max(previousIndex + 1, entries.Length - 50);
        for (int index = start; index < entries.Length; index++)
        {
            RecentFrames.Insert(0, new FrameDisplay(entries[index]));
        }
        while (RecentFrames.Count > 50) RecentFrames.RemoveAt(50);
        displayedLatest = entries[^1];
        ParsedFrame latest = entries[^1];
        LatestRaw = $"[{latest.Raw.Timestamp:HH:mm:ss.fff}] [{(latest.Raw.Source == FrameSource.Simulator ? "模拟" : "串口")}] {latest.Raw.Text}";
        LatestResult = latest.IsSuccess
            ? $"{latest.FieldName}: {latest.Value?.ToString("G", CultureInfo.InvariantCulture)} {latest.Unit}"
            : "解析失败：" + latest.Error;
        if (latest.IsSuccess)
        {
            LatestValue = latest.Value!.Value.ToString("G", CultureInfo.InvariantCulture);
            LatestField = $"{latest.FieldName} {latest.Unit}";
        }
        else
        {
            ParsedFrame? previous = entries.LastOrDefault(entry => entry.IsSuccess);
            LatestValue = "无效";
            LatestField = previous is null ? "当前数据无效 · 尚无有效值" :
                $"当前数据无效 · 上次有效 {previous.Value?.ToString("G", CultureInfo.InvariantCulture)} {previous.Unit} · {previous.Raw.Timestamp:HH:mm:ss}";
        }
    }
}

public sealed class FrameDisplay(ParsedFrame frame)
{
    public string Time => frame.Raw.Timestamp.ToString("HH:mm:ss.fff");
    public string Source => frame.Raw.Source == FrameSource.Simulator ? "模拟" : "串口";
    public string Raw => frame.Raw.Text;
    public string Result => frame.IsSuccess
        ? $"{frame.FieldName} = {frame.Value?.ToString("G", CultureInfo.InvariantCulture)} {frame.Unit}"
        : "解析失败：" + frame.Error;
}
