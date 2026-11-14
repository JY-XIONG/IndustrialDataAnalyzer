using IndustrialDataAnalyzer.App.ViewModels.Pages;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;

namespace IndustrialDataAnalyzer.Tests;

public sealed class AcquisitionViewModelTests
{
    [Fact]
    public async Task SwitchingDevicesKeepsEachRecentRawFrameAndStopUpdatesStatus()
    {
        DeviceDefinition first = new() { Id = Guid.NewGuid(), Name = "温度", Code = "T", Kind = DeviceKind.Simulator };
        DeviceDefinition second = new() { Id = Guid.NewGuid(), Name = "重量", Code = "W", Kind = DeviceKind.Simulator };
        var manager = new FakeManager(first, second);
        var viewModel = new AcquisitionViewModel(manager);

        manager.Emit(first.Id, "21.5");
        viewModel.FlushPendingFrames();
        Assert.Contains("21.5", viewModel.LatestRaw);
        viewModel.SelectedDevice = second;
        Assert.Equal("暂无报文", viewModel.LatestRaw);
        manager.Emit(second.Id, "3.2");
        viewModel.FlushPendingFrames();
        Assert.Contains("3.2", viewModel.LatestRaw);
        viewModel.SelectedDevice = first;
        Assert.Contains("21.5", viewModel.LatestRaw);
        Assert.Single(viewModel.RecentFrames);

        await viewModel.StopCommand.ExecuteAsync(null);
        Assert.Contains("未连接", viewModel.StatusText);
    }

    [Fact]
    public void LatestFailedFrameIsShownAsInvalidWithPreviousValueAndTime()
    {
        DeviceDefinition device = new() { Id = Guid.NewGuid(), Name = "温度", Code = "T", Kind = DeviceKind.Simulator };
        var manager = new FakeManager(device);
        var viewModel = new AcquisitionViewModel(manager);
        DateTimeOffset validTime = new(2026, 11, 14, 8, 9, 10, TimeSpan.Zero);
        manager.Emit(device.Id, "21.5", 21.5, null, validTime);
        manager.Emit(device.Id, "bad", null, "不是数字", validTime.AddSeconds(1));

        viewModel.FlushPendingFrames();

        Assert.Equal("无效", viewModel.LatestValue);
        Assert.Contains("21.5", viewModel.LatestField);
        Assert.Contains("08:09:10", viewModel.LatestField);
        Assert.Contains("解析失败", viewModel.LatestResult);
        Assert.Contains("bad", viewModel.LatestRaw);
        Assert.Equal(2, viewModel.RecentFrames.Count);
    }

    [Fact]
    public void HighFrequencyFramesStayBoundedUntilBatchRefresh()
    {
        DeviceDefinition device = new() { Id = Guid.NewGuid(), Name = "重量", Code = "W", Kind = DeviceKind.Simulator };
        var manager = new FakeManager(device);
        var viewModel = new AcquisitionViewModel(manager);
        for (int index = 0; index < 10_000; index++) manager.Emit(device.Id, index.ToString());

        Assert.Equal(200, viewModel.StoredFrameCount(device.Id));
        Assert.Empty(viewModel.RecentFrames);
        viewModel.FlushPendingFrames();
        Assert.Equal("9999", viewModel.LatestValue);
        Assert.Equal(50, viewModel.RecentFrames.Count);
    }

    private sealed class FakeManager(params DeviceDefinition[] devices) : IDeviceManager
    {
        private readonly Dictionary<Guid, ConnectionState> states = [];
        public event Action? DevicesChanged;
        public event Action<DeviceStateChange>? StateChanged;
        public event Action<ParsedFrame>? FrameProcessed;
        public IReadOnlyList<DeviceDefinition> Devices => devices;
        public Task InitializeAsync(CancellationToken cancellationToken = default) { DevicesChanged?.Invoke(); return Task.CompletedTask; }
        public Task UpsertAsync(DeviceDefinition device, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ConnectionState GetState(Guid id) => states.GetValueOrDefault(id);
        public Task ConnectAsync(Guid id, CancellationToken cancellationToken = default)
        {
            SetState(id, ConnectionState.Connected);
            return Task.CompletedTask;
        }
        public Task DisconnectAsync(Guid id)
        {
            SetState(id, ConnectionState.Disconnected);
            return Task.CompletedTask;
        }
        public Task SendAsync(Guid id, string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAllAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Emit(Guid id, string text) => Emit(id, text,
            double.Parse(text, System.Globalization.CultureInfo.InvariantCulture), null, DateTimeOffset.UtcNow);
        public void Emit(Guid id, string text, double? value, string? error, DateTimeOffset timestamp)
        {
            RawFrame raw = new(id, timestamp, FrameSource.Simulator, text);
            FrameProcessed?.Invoke(new ParsedFrame(raw, "测量值", string.Empty, value, error));
        }
        private void SetState(Guid id, ConnectionState state)
        {
            states[id] = state;
            StateChanged?.Invoke(new DeviceStateChange(id, state, null));
        }
    }
}
