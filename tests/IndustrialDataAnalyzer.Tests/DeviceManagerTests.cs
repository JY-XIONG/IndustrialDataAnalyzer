using System.IO;
using System.Globalization;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;
using IndustrialDataAnalyzer.Infrastructure.Communication;
using IndustrialDataAnalyzer.Infrastructure.Paths;
using IndustrialDataAnalyzer.Infrastructure.Persistence;
using IndustrialDataAnalyzer.Services.Devices;
using Microsoft.Extensions.Logging.Abstractions;

namespace IndustrialDataAnalyzer.Tests;

public sealed class DeviceManagerTests
{
    [Fact]
    public async Task CrudPersistsAndDuplicateCodesAreRejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "IndustrialDataAnalyzer.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new ApplicationPaths(root);
            var repository = new JsonDeviceRepository(paths, NullLogger<JsonDeviceRepository>.Instance);
            await using var manager = CreateManager(repository);
            await manager.InitializeAsync();
            DeviceDefinition first = Device("A", "01");
            DeviceDefinition second = Device("B", "02");
            await manager.UpsertAsync(first);
            await manager.UpsertAsync(second);
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.UpsertAsync(Device("C", "01")));
            await manager.UpsertAsync(first with { Name = "Edited" });
            Assert.Equal(2, manager.Devices.Count);

            await using var reloaded = CreateManager(repository);
            await reloaded.InitializeAsync();
            Assert.Contains(reloaded.Devices, item => item.Name == "Edited");
            await manager.DeleteAsync(second.Id);
            await using var afterDelete = CreateManager(repository);
            await afterDelete.InitializeAsync();
            Assert.Equal(first.Id, Assert.Single(afterDelete.Devices).Id);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SimulatorFramesAreParsedAndStopPreventsMoreFrames()
    {
        string root = Path.Combine(Path.GetTempPath(), "IndustrialDataAnalyzer.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new JsonDeviceRepository(new ApplicationPaths(root), NullLogger<JsonDeviceRepository>.Instance);
            await using var manager = CreateManager(repository);
            await manager.InitializeAsync();
            DeviceDefinition device = Device("Temperature", "T1") with
            {
                Simulator = new SimulatorOptions { Measurement = MeasurementKind.Temperature, Minimum = 20, Maximum = 25, IntervalMilliseconds = 50 },
                Parser = new ParserOptions { FieldName = "温度", Unit = "℃" }
            };
            await manager.UpsertAsync(device);
            var firstFrame = new TaskCompletionSource<ParsedFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            int count = 0;
            manager.FrameProcessed += frame => { Interlocked.Increment(ref count); firstFrame.TrySetResult(frame); };

            await manager.ConnectAsync(device.Id);
            ParsedFrame received = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(received.IsSuccess);
            Assert.Equal(FrameSource.Simulator, received.Raw.Source);
            Assert.InRange(received.Value!.Value, 20, 25);
            Assert.Equal(ConnectionState.Connected, manager.GetState(device.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConnectAsync(device.Id));
            await manager.DisconnectAsync(device.Id);
            int afterStop = Volatile.Read(ref count);
            await Task.Delay(160);
            Assert.Equal(afterStop, Volatile.Read(ref count));
            Assert.Equal(ConnectionState.Disconnected, manager.GetState(device.Id));

            await manager.ConnectAsync(device.Id); // manual reconnect
            Assert.Equal(ConnectionState.Connected, manager.GetState(device.Id));
            await manager.UpsertAsync(device with { Name = "Updated" });
            Assert.Equal(ConnectionState.Disconnected, manager.GetState(device.Id));
            await manager.ConnectAsync(device.Id);
            await manager.DeleteAsync(device.Id);
            Assert.Empty(manager.Devices);
            Assert.Equal(ConnectionState.Disconnected, manager.GetState(device.Id));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(MeasurementKind.Temperature)]
    [InlineData(MeasurementKind.Weight)]
    [InlineData(MeasurementKind.Voltage)]
    public async Task SimulatorSupportsAllMeasurementKinds(MeasurementKind kind)
    {
        DeviceDefinition device = Device(kind.ToString(), kind.ToString()) with
        {
            Simulator = new SimulatorOptions { Measurement = kind, Minimum = 1, Maximum = 2, IntervalMilliseconds = 50 }
        };
        await using var channel = new SimulatedDeviceChannel(device, NullLogger<SimulatedDeviceChannel>.Instance);
        var first = new TaskCompletionSource<RawFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.FrameReceived += frame => first.TrySetResult(frame);
        await channel.ConnectAsync();
        RawFrame frame = await first.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(FrameSource.Simulator, frame.Source);
        double value = double.Parse(frame.Text, CultureInfo.InvariantCulture);
        Assert.InRange(value, 1, 2);
        await channel.DisconnectAsync();
    }

    [Fact]
    public async Task SimulatorCanStopImmediatelyAfterStart()
    {
        DeviceDefinition device = Device("Rapid stop", "RS") with
        {
            Simulator = new SimulatorOptions { IntervalMilliseconds = 1000 }
        };
        for (int attempt = 0; attempt < 50; attempt++)
        {
            await using var channel = new SimulatedDeviceChannel(device, NullLogger<SimulatedDeviceChannel>.Instance);
            await channel.ConnectAsync();
            await channel.DisconnectAsync();
            Assert.Equal(ConnectionState.Disconnected, channel.State);
        }
    }

    [Fact]
    public async Task FailedConnectionKeepsFaultStateAndManualReconnectRecovers()
    {
        string root = Path.Combine(Path.GetTempPath(), "IndustrialDataAnalyzer.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new JsonDeviceRepository(new ApplicationPaths(root), NullLogger<JsonDeviceRepository>.Instance);
            await using var manager = new DeviceManager(repository, new FailOnceFactory(), new FrameParser(), NullLogger<DeviceManager>.Instance);
            DeviceDefinition device = Device("Recovery", "R1");
            await manager.InitializeAsync();
            await manager.UpsertAsync(device);
            List<ConnectionState> states = [];
            manager.StateChanged += change => states.Add(change.State);

            await Assert.ThrowsAsync<IOException>(() => manager.ConnectAsync(device.Id));
            Assert.Equal(ConnectionState.Faulted, manager.GetState(device.Id));
            await manager.ConnectAsync(device.Id);
            Assert.Equal(ConnectionState.Connected, manager.GetState(device.Id));
            Assert.Contains(ConnectionState.Faulted, states);
            Assert.Contains(ConnectionState.Connected, states);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static DeviceManager CreateManager(JsonDeviceRepository repository) => new(repository,
        new CommunicationChannelFactory(NullLoggerFactory.Instance), new FrameParser(), NullLogger<DeviceManager>.Instance);

    private static DeviceDefinition Device(string name, string code) => new()
    {
        Id = Guid.NewGuid(), Name = name, Code = code, Kind = DeviceKind.Simulator
    };

    private sealed class FailOnceFactory : ICommunicationChannelFactory
    {
        private bool first = true;
        public ICommunicationChannel Create(DeviceDefinition device)
        {
            if (first)
            {
                first = false;
                return new FailingChannel(device.Id);
            }
            return new SimulatedDeviceChannel(device, NullLogger<SimulatedDeviceChannel>.Instance);
        }
    }

    private sealed class FailingChannel(Guid id) : ICommunicationChannel
    {
        public event Action<RawFrame>? FrameReceived { add { } remove { } }
        public event Action<DeviceStateChange>? StateChanged;
        public ConnectionState State { get; private set; }
        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            State = ConnectionState.Faulted;
            StateChanged?.Invoke(new DeviceStateChange(id, State, "故障"));
            throw new IOException("测试连接故障");
        }
        public Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(new DeviceStateChange(id, State, null));
            return Task.CompletedTask;
        }
        public Task SendAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => new(DisconnectAsync());
    }
}
