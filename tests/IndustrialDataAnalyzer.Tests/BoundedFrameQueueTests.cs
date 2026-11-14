using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;
using IndustrialDataAnalyzer.Services.Devices;
using Microsoft.Extensions.Logging.Abstractions;

namespace IndustrialDataAnalyzer.Tests;

public sealed class BoundedFrameQueueTests
{
    [Fact]
    public async Task OverflowDropsOldFramesAndEventuallyProcessesLatest()
    {
        DeviceDefinition device = new() { Id = Guid.NewGuid(), Name = "Burst", Code = "B", Kind = DeviceKind.Simulator };
        var factory = new BurstFactory();
        var parser = new BlockingParser();
        await using var manager = new DeviceManager(new MemoryRepository(), factory, parser,
            NullLogger<DeviceManager>.Instance);
        await manager.InitializeAsync();
        await manager.UpsertAsync(device);
        var latest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.FrameProcessed += frame => { if (frame.Raw.Text == "5999") latest.TrySetResult(); };
        await manager.ConnectAsync(device.Id);
        try
        {
            factory.Channel!.Emit("first");
            await parser.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            for (int index = 0; index < 6000; index++) factory.Channel.Emit(index.ToString());

            Assert.InRange(manager.PendingFrameCount, 1, 4096);
            Assert.True(manager.DroppedFrameCount > 0);
        }
        finally
        {
            parser.Release();
        }
        await latest.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class MemoryRepository : IDeviceRepository
    {
        private IReadOnlyList<DeviceDefinition> devices = [];
        public Task<IReadOnlyList<DeviceDefinition>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(devices);
        public Task SaveAsync(IReadOnlyList<DeviceDefinition> updated, CancellationToken cancellationToken = default)
        {
            devices = [.. updated];
            return Task.CompletedTask;
        }
    }

    private sealed class BurstFactory : ICommunicationChannelFactory
    {
        public BurstChannel? Channel { get; private set; }
        public ICommunicationChannel Create(DeviceDefinition device) => Channel = new BurstChannel(device.Id);
    }

    private sealed class BurstChannel(Guid id) : ICommunicationChannel
    {
        public event Action<RawFrame>? FrameReceived;
        public event Action<DeviceStateChange>? StateChanged;
        public ConnectionState State { get; private set; }
        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(new DeviceStateChange(id, State, null));
            return Task.CompletedTask;
        }
        public Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(new DeviceStateChange(id, State, null));
            return Task.CompletedTask;
        }
        public Task SendAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => new(DisconnectAsync());
        public void Emit(string text) => FrameReceived?.Invoke(new RawFrame(id, DateTimeOffset.UtcNow, FrameSource.Simulator, text));
    }

    private sealed class BlockingParser : IFrameParser
    {
        private readonly ManualResetEventSlim gate = new(false);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ParsedFrame Parse(RawFrame raw, ParserOptions options)
        {
            if (raw.Text == "first")
            {
                Started.TrySetResult();
                gate.Wait(TimeSpan.FromSeconds(5));
            }
            return new ParsedFrame(raw, "value", string.Empty, 1, null);
        }
        public void Release() => gate.Set();
    }
}
