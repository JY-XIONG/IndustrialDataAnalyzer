using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;

namespace IndustrialDataAnalyzer.App.ViewModels.Pages;

/// <summary>Pages through persisted measurements; filters are applied by SQLite.</summary>
public partial class HistoryViewModel : PageViewModel
{
    private readonly IMeasurementRepository? repository;

    public HistoryViewModel(IMeasurementRepository? repository = null)
        : base(NavigationPage.History, "历史数据分析", "按设备、时间和采集会话查询已保存记录")
    {
        this.repository = repository;
        Devices.Add(new HistoricalDevice(Guid.Empty, "全部设备"));
        Sessions.Add(new SessionChoice(null, "全部会话"));
        SelectedDevice = Devices[0];
        SelectedSession = Sessions[0];
        if (repository is not null) _ = SearchAsync();
    }

    public ObservableCollection<HistoricalDevice> Devices { get; } = [];
    public ObservableCollection<SessionChoice> Sessions { get; } = [];
    public ObservableCollection<HistoryRow> Records { get; } = [];
    [ObservableProperty] private HistoricalDevice? selectedDevice;
    [ObservableProperty] private SessionChoice? selectedSession;
    [ObservableProperty] private string sessionIdText = string.Empty;
    [ObservableProperty] private HistoryRow? selectedRecord;
    [ObservableProperty] private DateTime? fromDate = DateTime.Today.AddDays(-7);
    [ObservableProperty] private DateTime? toDate = DateTime.Today;
    [ObservableProperty] private int pageNumber = 1;
    [ObservableProperty] private int pageCount = 1;
    [ObservableProperty] private long totalCount;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string notice = "选择条件后查询已保存记录。";

    private async Task LoadDevicesAsync()
    {
        if (repository is null) return;
        IReadOnlyList<HistoricalDevice> historical = await repository.ListDevicesAsync();
        Guid selected = SelectedDevice?.Id ?? Guid.Empty;
        Devices.Clear();
        Devices.Add(new HistoricalDevice(Guid.Empty, "全部设备"));
        foreach (HistoricalDevice device in historical) Devices.Add(device);
        SelectedDevice = Devices.FirstOrDefault(device => device.Id == selected) ?? Devices[0];
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        try
        {
            await LoadDevicesAsync();
            PageNumber = 1;
            await QueryPageAsync(refreshSessions: true);
        }
        catch (Exception exception) { Notice = "历史记录加载失败：" + exception.Message; }
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (PageNumber <= 1) return;
        PageNumber--;
        await QueryPageAsync(refreshSessions: false);
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (PageNumber >= PageCount) return;
        PageNumber++;
        await QueryPageAsync(refreshSessions: false);
    }

    private async Task QueryPageAsync(bool refreshSessions)
    {
        if (repository is null) { Notice = "数据库尚未接入。"; return; }
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            DateTimeOffset? from = FromDate.HasValue ? LocalMidnightUtc(FromDate.Value) : null;
            DateTimeOffset? to = ToDate.HasValue ? LocalMidnightUtc(ToDate.Value.Date.AddDays(1)) : null;
            if (from.HasValue && to.HasValue && from >= to)
            {
                Notice = "结束日期必须不早于开始日期。";
                return;
            }
            Guid? deviceId = SelectedDevice?.Id == Guid.Empty ? null : SelectedDevice?.Id;
            if (refreshSessions)
            {
                Guid? previous = SelectedSession?.Id;
                IReadOnlyList<CaptureSession> available = await repository.ListSessionsAsync(
                    deviceId, from, to, limit: 200);
                Sessions.Clear();
                Sessions.Add(new SessionChoice(null, "全部会话"));
                foreach (CaptureSession session in available)
                    Sessions.Add(new SessionChoice(session.Id,
                        $"{session.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {session.DeviceName} · {session.State}"));
                SelectedSession = Sessions.FirstOrDefault(item => item.Id == previous) ?? Sessions[0];
            }
            Guid? sessionId = SelectedSession?.Id;
            if (!string.IsNullOrWhiteSpace(SessionIdText))
            {
                if (!Guid.TryParse(SessionIdText, out Guid typedSession))
                {
                    Notice = "采集会话 ID 格式无效。";
                    return;
                }
                sessionId = typedSession;
            }
            HistoryPage page = await repository.QueryAsync(new HistoryQuery(deviceId, sessionId,
                from, to, PageNumber, 50));
            Records.Clear();
            foreach (MeasurementRecord record in page.Records) Records.Add(new HistoryRow(record));
            SelectedRecord = Records.FirstOrDefault();
            TotalCount = page.TotalCount;
            PageCount = (int)Math.Max(1, (page.TotalCount + 49) / 50);
            Notice = page.TotalCount == 0 ? "该条件下没有已保存记录。" :
                $"共 {page.TotalCount} 条已保存记录；第 {PageNumber}/{PageCount} 页。";
        }
        catch (Exception exception) { Notice = "历史查询失败：" + exception.Message; }
        finally { IsBusy = false; }
    }

    private static DateTimeOffset LocalMidnightUtc(DateTime date) =>
        new DateTimeOffset(date.Date, TimeZoneInfo.Local.GetUtcOffset(date.Date)).ToUniversalTime();
}

public sealed record SessionChoice(Guid? Id, string Label);

public sealed class HistoryRow(MeasurementRecord record)
{
    public string TimeLocal => record.CapturedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
    public string DeviceName => record.DeviceName;
    public string FieldName => record.FieldName;
    public string ValueText => record.IsSuccess ? record.Value!.Value.ToString("G", CultureInfo.InvariantCulture) : "无效";
    public string Unit => record.Unit;
    public string Source => record.Source == FrameSource.Simulator ? "模拟" : "串口";
    public string Status => record.IsSuccess ? "有效" : "解析失败";
    public string RawText => record.RawText;
    public string ErrorText => record.Error ?? "无解析错误";
    public string SessionId => record.SessionId.ToString();
}
