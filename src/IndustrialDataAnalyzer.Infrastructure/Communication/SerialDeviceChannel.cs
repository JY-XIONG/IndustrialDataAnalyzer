using System.IO.Ports;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;
using Microsoft.Extensions.Logging;

namespace IndustrialDataAnalyzer.Infrastructure.Communication;

/// <summary>Reads and writes terminated text frames through one serial port.</summary>
public sealed class SerialDeviceChannel(DeviceDefinition device, ILogger<SerialDeviceChannel> logger) : ICommunicationChannel
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private SerialPort? port;
    private CancellationTokenSource? receiveCancellation;
    private Task? receiveTask;
    private Task? monitorTask;
    private volatile ConnectionState state;
    private bool disposed;

    public event Action<RawFrame>? FrameReceived;
    public event Action<DeviceStateChange>? StateChanged;
    public ConnectionState State => state;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (state != ConnectionState.Disconnected)
            {
                throw new InvalidOperationException("串口已连接或正在连接。");
            }

            var frameBuffer = new LineFrameBuffer(device.Serial.Terminator, device.Serial.EncodingName);
            SerialPort opened = new(device.Serial.PortName, device.Serial.BaudRate,
                (Parity)device.Serial.Parity, device.Serial.DataBits, device.Serial.StopBits switch
                {
                    SerialStopBits.One => StopBits.One,
                    SerialStopBits.OnePointFive => StopBits.OnePointFive,
                    SerialStopBits.Two => StopBits.Two,
                    _ => throw new ArgumentOutOfRangeException(nameof(device), "无效的停止位配置。")
                })
            {
                Handshake = (Handshake)device.Serial.FlowControl,
                ReadTimeout = 1000,
                WriteTimeout = 1000
            };
            SetState(ConnectionState.Connecting, null);
            try
            {
                await Task.Run(opened.Open, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                port = opened;
                receiveCancellation = new CancellationTokenSource();
                SetState(ConnectionState.Connected, null);
                logger.LogInformation("Serial port {Port} opened for {DeviceId}", device.Serial.PortName, device.Id);
                receiveTask = ReceiveAsync(opened, frameBuffer, receiveCancellation.Token);
                monitorTask = MonitorAsync(opened, receiveCancellation.Token);
            }
            catch (Exception exception)
            {
                opened.Dispose();
                SetState(ConnectionState.Faulted, exception.Message);
                logger.LogError(exception, "Could not open serial port {Port} for {DeviceId}", device.Serial.PortName, device.Id);
                throw;
            }
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        if (disposed) return;
        Task? reader;
        Task? monitor;
        CancellationTokenSource? cancellation;
        SerialPort? closing;
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            reader = receiveTask;
            monitor = monitorTask;
            cancellation = receiveCancellation;
            closing = port;
            receiveTask = null;
            monitorTask = null;
            receiveCancellation = null;
            port = null;
            if (state != ConnectionState.Disconnected)
            {
                SetState(ConnectionState.Disconnected, null);
            }
            if (cancellation is not null) await cancellation.CancelAsync().ConfigureAwait(false);
            if (closing is not null)
                await Task.Run(closing.Dispose).ConfigureAwait(false); // Also releases a pending read.
        }
        finally
        {
            lifecycle.Release();
        }

        if (reader is not null)
        {
            try
            {
                await reader.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during manual disconnect.
            }
        }
        if (monitor is not null)
        {
            try { await monitor.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        cancellation?.Dispose();
        logger.LogInformation("Serial port {Port} closed for {DeviceId}", device.Serial.PortName, device.Id);
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            SerialPort current = port ?? throw new InvalidOperationException("设备尚未连接。");
            if (state != ConnectionState.Connected)
            {
                throw new InvalidOperationException("设备尚未连接。");
            }
            var buffer = new LineFrameBuffer(device.Serial.Terminator, device.Serial.EncodingName);
            byte[] bytes = [.. buffer.Encoding.GetBytes(text), .. buffer.Ending.ToArray()];
            await current.BaseStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await current.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Sent {Count} bytes to serial device {DeviceId}", bytes.Length, device.Id);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogError(exception, "Serial send failed for {DeviceId}", device.Id);
            if (state == ConnectionState.Connected && exception is IOException or UnauthorizedAccessException)
            {
                SetState(ConnectionState.Faulted, exception.Message);
                SerialPort? failed = port;
                if (failed is not null) await Task.Run(failed.Dispose).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            sendGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        await DisconnectAsync().ConfigureAwait(false);
        disposed = true;
    }

    private async Task ReceiveAsync(SerialPort opened, LineFrameBuffer frameBuffer, CancellationToken token)
    {
        byte[] bytes = new byte[4096];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int count = await opened.BaseStream.ReadAsync(bytes, token).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new IOException("串口数据流已关闭。");
                }
                foreach (string frame in frameBuffer.Append(bytes.AsSpan(0, count)))
                {
                    if (state == ConnectionState.Connected && !token.IsCancellationRequested)
                    {
                        FrameReceived?.Invoke(new RawFrame(device.Id, DateTimeOffset.Now, FrameSource.Serial, frame));
                    }
                }
            }
        }
        catch (Exception exception) when (token.IsCancellationRequested &&
            exception is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The caller closed the stream.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Serial receive failed for {DeviceId}", device.Id);
            if (state == ConnectionState.Connected)
            {
                SetState(ConnectionState.Faulted, exception.Message);
                opened.Dispose();
            }
        }
    }

    private async Task MonitorAsync(SerialPort opened, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            if (state != ConnectionState.Connected) return;
            try
            {
                if (!opened.IsOpen || !SerialPort.GetPortNames().Contains(device.Serial.PortName, StringComparer.OrdinalIgnoreCase))
                {
                    SetState(ConnectionState.Faulted, $"串口 {device.Serial.PortName} 已断开或不可用。");
                    logger.LogWarning("Serial port {Port} disappeared for {DeviceId}", device.Serial.PortName, device.Id);
                    opened.Dispose();
                    return;
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not check serial port {Port}", device.Serial.PortName);
            }
        }
    }

    private void SetState(ConnectionState next, string? detail)
    {
        state = next;
        StateChanged?.Invoke(new DeviceStateChange(device.Id, next, detail));
    }
}
