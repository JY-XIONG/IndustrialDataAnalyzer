using System.Globalization;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;
using Microsoft.Extensions.Logging;

namespace IndustrialDataAnalyzer.Infrastructure.Communication;

/// <summary>Emits bounded measurement values without physical hardware.</summary>
public sealed class SimulatedDeviceChannel(DeviceDefinition device, ILogger<SimulatedDeviceChannel> logger) : ICommunicationChannel
{
    private CancellationTokenSource? cancellation;
    private Task? producer;
    private volatile ConnectionState state;
    private bool disposed;

    /// <inheritdoc />
    public event Action<RawFrame>? FrameReceived;
    /// <inheritdoc />
    public event Action<DeviceStateChange>? StateChanged;
    /// <inheritdoc />
    public ConnectionState State => state;

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (state is ConnectionState.Connected or ConnectionState.Connecting)
        {
            throw new InvalidOperationException("模拟设备已经启动。");
        }

        var source = new CancellationTokenSource();
        cancellation = source;
        SetState(ConnectionState.Connected, null);
        logger.LogInformation("Simulator started for device {DeviceId}", device.Id);
        // The field is cleared by DisconnectAsync; the producer must capture its own source.
        producer = Task.Run(() => ProduceAsync(source.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        var source = cancellation;
        var task = producer;
        cancellation = null;
        producer = null;
        if (state == ConnectionState.Disconnected && source is null)
        {
            return;
        }

        SetState(ConnectionState.Disconnected, null);
        if (source is not null)
        {
            await source.CancelAsync().ConfigureAwait(false);
        }
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the expected stop path.
            }
        }
        source?.Dispose();
        logger.LogInformation("Simulator stopped for device {DeviceId}", device.Id);
    }

    /// <inheritdoc />
    public Task SendAsync(string text, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("模拟设备不支持发送报文。");

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
    }

    private async Task ProduceAsync(CancellationToken token)
    {
        try
        {
            TimeSpan interval = TimeSpan.FromMilliseconds(device.Simulator.IntervalMilliseconds);
            while (true)
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
                if (state != ConnectionState.Connected)
                {
                    break;
                }
                double value = device.Simulator.Minimum +
                    Random.Shared.NextDouble() * (device.Simulator.Maximum - device.Simulator.Minimum);
                string text = value.ToString("F3", CultureInfo.InvariantCulture);
                FrameReceived?.Invoke(new RawFrame(device.Id, DateTimeOffset.Now, FrameSource.Simulator, text));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The caller requested a clean stop.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Simulator failed for device {DeviceId}", device.Id);
            SetState(ConnectionState.Faulted, exception.Message);
        }
    }

    private void SetState(ConnectionState next, string? detail)
    {
        state = next;
        StateChanged?.Invoke(new DeviceStateChange(device.Id, next, detail));
    }
}
