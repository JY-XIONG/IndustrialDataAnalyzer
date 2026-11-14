using System.IO;
using IndustrialDataAnalyzer.App.ViewModels.Pages;
using IndustrialDataAnalyzer.Core.Models;
using IndustrialDataAnalyzer.Infrastructure.Paths;
using IndustrialDataAnalyzer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace IndustrialDataAnalyzer.Tests;

public sealed class HistoryViewModelTests
{
    [Fact]
    public async Task PageFiltersByDeviceDateAndExplicitSessionId()
    {
        string root = Path.Combine(Path.GetTempPath(), "IndustrialDataAnalyzer.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SqliteMeasurementRepository(new ApplicationPaths(root));
            await store.InitializeAsync();
            DateTimeOffset now = DateTimeOffset.Now;
            var first = new CaptureSession(Guid.NewGuid(), Guid.NewGuid(), "设备甲", now.ToUniversalTime(),
                null, 0, 0, 0, 0, CaptureSessionState.Active);
            var second = first with { Id = Guid.NewGuid(), DeviceId = Guid.NewGuid(), DeviceName = "设备乙" };
            await store.CreateSessionAsync(first);
            await store.CreateSessionAsync(second);
            var records = Enumerable.Range(0, 61).Select(index => new MeasurementRecord(
                Guid.NewGuid(), first.Id, first.DeviceId, now.AddSeconds(index).ToUniversalTime(),
                FrameSource.Simulator, index.ToString(), "温度", index, "℃", null)).ToList();
            records.Add(new MeasurementRecord(Guid.NewGuid(), second.Id, second.DeviceId, now.ToUniversalTime(),
                FrameSource.Serial, "bad", "重量", null, "g", "无效"));
            await store.SaveBatchAsync(records);
            var viewModel = new HistoryViewModel(store);
            await WaitUntilAsync(() => viewModel.TotalCount == 62 && !viewModel.IsBusy);
            Assert.Equal(50, viewModel.Records.Count);
            Assert.Equal(2, viewModel.PageCount);
            viewModel.SelectedDevice = viewModel.Devices.Single(device => device.Id == first.DeviceId);
            await viewModel.SearchCommand.ExecuteAsync(null);
            Assert.Equal(61, viewModel.TotalCount);
            await viewModel.NextPageCommand.ExecuteAsync(null);
            Assert.Equal(11, viewModel.Records.Count);
            Assert.Equal(2, viewModel.PageNumber);
            viewModel.SelectedDevice = viewModel.Devices[0];
            viewModel.SessionIdText = second.Id.ToString();
            await viewModel.SearchCommand.ExecuteAsync(null);
            Assert.Equal(1, viewModel.TotalCount);
            Assert.Equal("bad", Assert.Single(viewModel.Records).RawText);
            Assert.Equal("无效", viewModel.SelectedRecord?.ErrorText);
            viewModel.SessionIdText = "wrong";
            await viewModel.SearchCommand.ExecuteAsync(null);
            Assert.Contains("格式无效", viewModel.Notice);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("历史页面未完成加载。");
    }
}
