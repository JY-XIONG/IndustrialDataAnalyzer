using System.IO;
using IndustrialDataAnalyzer.Core.Models;
using IndustrialDataAnalyzer.Infrastructure.Communication;
using Microsoft.Extensions.Logging.Abstractions;

namespace IndustrialDataAnalyzer.Tests;

public sealed class SerialDeviceChannelTests
{
    [Fact]
    public async Task ConcurrentSendAndDisconnectClosesTransportOnce()
    {
        var transport = new FakeTransport
        {
            Write = async token => await Task.Delay(Timeout.Infinite, token)
        };
        await using var channel = CreateChannel(() => transport);
        await channel.ConnectAsync();
        Task sending = channel.SendAsync("PING");
        await transport.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => channel.DisconnectAsync()))
            .WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.Equal(ConnectionState.Disconnected, channel.State);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task SendTimeoutFaultsAndReleasesTransport()
    {
        var pendingWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport { Write = _ => pendingWrite.Task };
        await using var channel = CreateChannel(() => transport, TimeSpan.FromMilliseconds(100));
        await channel.ConnectAsync();

        await Assert.ThrowsAsync<TimeoutException>(() => channel.SendAsync("PING"));

        Assert.Equal(ConnectionState.Faulted, channel.State);
        Assert.Equal(1, transport.DisposeCount);
        pendingWrite.TrySetCanceled();
        await channel.DisconnectAsync();
    }

    [Fact]
    public async Task RepeatConnectionAndIOExceptionRecoveryUseFreshTransports()
    {
        var first = new FakeTransport { Write = _ => throw new IOException("端口故障") };
        var second = new FakeTransport();
        int created = 0;
        await using var channel = CreateChannel(() => Interlocked.Increment(ref created) == 1 ? first : second);
        await channel.ConnectAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.ConnectAsync());

        await Assert.ThrowsAsync<IOException>(() => channel.SendAsync("PING"));
        Assert.Equal(ConnectionState.Faulted, channel.State);
        await channel.DisconnectAsync();
        await channel.ConnectAsync();
        await channel.SendAsync("PING");

        Assert.Equal(2, created);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(ConnectionState.Connected, channel.State);
    }

    [Fact]
    public async Task ReconnectWaitsUntilPreviousTransportHasClosed()
    {
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeTransport { Dispose = () => releaseClose.Task };
        var second = new FakeTransport();
        int created = 0;
        await using var channel = CreateChannel(() => Interlocked.Increment(ref created) == 1 ? first : second);
        await channel.ConnectAsync();

        Task disconnecting = channel.DisconnectAsync();
        await first.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task reconnecting = channel.ConnectAsync();
        Assert.False(reconnecting.IsCompleted);
        Assert.Equal(1, created);

        releaseClose.SetResult();
        await Task.WhenAll(disconnecting, reconnecting).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, created);
        Assert.Equal(ConnectionState.Connected, channel.State);
    }

    [Fact]
    public async Task ReceiveFailureFaultsChannelAndAllowsManualRecovery()
    {
        var first = new FakeTransport { Read = _ => throw new IOException("接收失败") };
        var second = new FakeTransport();
        int created = 0;
        await using var channel = CreateChannel(() => Interlocked.Increment(ref created) == 1 ? first : second);
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.StateChanged += change =>
        {
            if (change.State == ConnectionState.Faulted) faulted.TrySetResult();
        };

        await channel.ConnectAsync();
        await faulted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await channel.DisconnectAsync();
        await channel.ConnectAsync();

        Assert.Equal(ConnectionState.Connected, channel.State);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(2, created);
    }

    private static SerialDeviceChannel CreateChannel(Func<ISerialPortTransport> create,
        TimeSpan? sendTimeout = null) => new(
            new DeviceDefinition { Id = Guid.NewGuid(), Name = "Serial", Code = "COM-1", Kind = DeviceKind.Serial,
                Serial = new SerialOptions { PortName = "COM1" } },
            NullLogger<SerialDeviceChannel>.Instance,
            _ => create(), _ => true, sendTimeout ?? TimeSpan.FromSeconds(2), TimeSpan.FromHours(1));

    private sealed class FakeTransport : ISerialPortTransport
    {
        public Func<CancellationToken, Task>? Write { get; init; }
        public Func<CancellationToken, Task<int>>? Read { get; init; }
        public Func<Task>? Dispose { get; init; }
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsOpen { get; private set; }
        public int DisposeCount { get; private set; }
        public Task OpenAsync(CancellationToken cancellationToken)
        {
            IsOpen = true;
            return Task.CompletedTask;
        }
        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            Read?.Invoke(cancellationToken) ?? WaitForCancellationAsync(cancellationToken);
        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            WriteStarted.TrySetResult();
            return Write?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposeStarted.TrySetResult();
            if (Dispose is not null) await Dispose();
            IsOpen = false;
        }
    }
}
