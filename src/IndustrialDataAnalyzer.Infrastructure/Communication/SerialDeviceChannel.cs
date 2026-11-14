using System.IO.Ports;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;
using Microsoft.Extensions.Logging;

namespace IndustrialDataAnalyzer.Infrastructure.Communication;

/// <summary>Reads and writes terminated text frames through one serial session.</summary>
public sealed class SerialDeviceChannel : ICommunicationChannel
{
    private static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultOpenTimeout = TimeSpan.FromSeconds(5);
    private readonly DeviceDefinition device;
    private readonly ILogger<SerialDeviceChannel> logger;
    private readonly Func<DeviceDefinition, ISerialPortTransport> createTransport;
    private readonly Func<string, bool> isPortAvailable;
    private readonly TimeSpan sendTimeout;
    private readonly TimeSpan monitorInterval;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private Session? session;
    private Task? closingTask;
    private volatile ConnectionState state;
    private bool disposed;

    public SerialDeviceChannel(DeviceDefinition device, ILogger<SerialDeviceChannel> logger)
        : this(device, logger, definition => new SerialPortTransport(definition.Serial),
            port => SerialPort.GetPortNames().Contains(port, StringComparer.OrdinalIgnoreCase),
            DefaultSendTimeout, TimeSpan.FromSeconds(2)) { }

    internal SerialDeviceChannel(DeviceDefinition device, ILogger<SerialDeviceChannel> logger,
        Func<DeviceDefinition, ISerialPortTransport> createTransport, Func<string, bool> isPortAvailable,
        TimeSpan sendTimeout, TimeSpan monitorInterval)
    {
        this.device = device;
        this.logger = logger;
        this.createTransport = createTransport;
        this.isPortAvailable = isPortAvailable;
        this.sendTimeout = sendTimeout;
        this.monitorInterval = monitorInterval;
    }

    public event Action<RawFrame>? FrameReceived;
    public event Action<DeviceStateChange>? StateChanged;
    public ConnectionState State => state;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (closingTask is null || closingTask.IsCompleted) break;
            Task pendingClose = closingTask;
            lifecycle.Release();
            await pendingClose.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (closingTask is not null) await closingTask.ConfigureAwait(false);
            if (state != ConnectionState.Disconnected || session is not null)
                throw new InvalidOperationException("串口已连接或正在连接；异常后请先断开再重连。");

            var frameBuffer = new LineFrameBuffer(device.Serial.Terminator, device.Serial.EncodingName);
            ISerialPortTransport opened = createTransport(device);
            SetState(ConnectionState.Connecting, null);
            try
            {
                Task openTask = opened.OpenAsync(cancellationToken);
                ObserveFault(openTask);
                await openTask.WaitAsync(DefaultOpenTimeout, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var current = new Session(opened);
                session = current;
                SetState(ConnectionState.Connected, null);
                current.Reader = ReceiveAsync(current, frameBuffer);
                current.Monitor = MonitorAsync(current);
                logger.LogInformation("Serial port {Port} opened for {DeviceId}", device.Serial.PortName, device.Id);
            }
            catch (Exception exception)
            {
                await opened.DisposeAsync().ConfigureAwait(false);
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
        Task? pendingClose;
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            Session? current = session;
            session = null;
            if (state != ConnectionState.Disconnected) SetState(ConnectionState.Disconnected, null);
            pendingClose = current is null ? closingTask : closingTask = CloseSessionAsync(current);
        }
        finally
        {
            lifecycle.Release();
        }
        if (pendingClose is not null) await pendingClose.ConfigureAwait(false);
    }

    private async Task CloseSessionAsync(Session current)
    {
        await current.Cancellation.CancelAsync().ConfigureAwait(false);
        Exception? closeError = null;
        try { await current.CloseAsync().ConfigureAwait(false); }
        catch (Exception exception) { closeError = exception; logger.LogError(exception, "Serial close failed for {DeviceId}", device.Id); }

        // A writer may have started before the state changed. Closing the port and
        // waiting for the send gate ensures it cannot outlive this disconnect.
        await sendGate.WaitAsync().ConfigureAwait(false);
        sendGate.Release();
        await AwaitLoopAsync(current.Reader).ConfigureAwait(false);
        await AwaitLoopAsync(current.Monitor).ConfigureAwait(false);
        current.Cancellation.Dispose();
        logger.LogInformation("Serial port {Port} closed for {DeviceId}", device.Serial.PortName, device.Id);
        if (closeError is not null) throw new IOException("串口关闭失败。", closeError);
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Session current = session ?? throw new InvalidOperationException("设备尚未连接。");
            if (state != ConnectionState.Connected || current.Cancellation.IsCancellationRequested)
                throw new InvalidOperationException("设备尚未连接。");

            var buffer = new LineFrameBuffer(device.Serial.Terminator, device.Serial.EncodingName);
            byte[] bytes = [.. buffer.Encoding.GetBytes(text), .. buffer.Ending.ToArray()];
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, current.Cancellation.Token);
            linked.CancelAfter(sendTimeout);
            try
            {
                Task writeTask = current.Transport.WriteAsync(bytes, linked.Token);
                ObserveFault(writeTask);
                await writeTask.WaitAsync(sendTimeout, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Sent {Count} bytes to serial device {DeviceId}", bytes.Length, device.Id);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
                !current.Cancellation.IsCancellationRequested)
            {
                await FaultAndCloseAsync(current, "串口发送超时。", null).ConfigureAwait(false);
                throw new TimeoutException("串口发送超时。");
            }
            catch (TimeoutException exception)
            {
                await FaultAndCloseAsync(current, "串口发送超时。", exception).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                if (!current.Cancellation.IsCancellationRequested)
                    await FaultAndCloseAsync(current, exception.Message, exception).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            sendGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await DisconnectAsync().ConfigureAwait(false);
        disposed = true;
    }

    private async Task ReceiveAsync(Session current, LineFrameBuffer frameBuffer)
    {
        byte[] bytes = new byte[4096];
        try
        {
            while (!current.Cancellation.IsCancellationRequested)
            {
                int count = await current.Transport.ReadAsync(bytes, current.Cancellation.Token).ConfigureAwait(false);
                if (count == 0) throw new IOException("串口数据流已关闭。");
                foreach (string frame in frameBuffer.Append(bytes.AsSpan(0, count)))
                {
                    if (ReferenceEquals(session, current) && state == ConnectionState.Connected &&
                        !current.Cancellation.IsCancellationRequested)
                        FrameReceived?.Invoke(new RawFrame(device.Id, DateTimeOffset.Now, FrameSource.Serial, frame));
                }
            }
        }
        catch (Exception exception) when (current.Cancellation.IsCancellationRequested &&
            exception is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Manual disconnect or fault cleanup closed the stream.
        }
        catch (Exception exception)
        {
            await FaultAndCloseAsync(current, exception.Message, exception).ConfigureAwait(false);
        }
    }

    private async Task MonitorAsync(Session current)
    {
        using var timer = new PeriodicTimer(monitorInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(current.Cancellation.Token).ConfigureAwait(false))
            {
                if (!ReferenceEquals(session, current) || state != ConnectionState.Connected) return;
                try
                {
                    if (!current.Transport.IsOpen || !isPortAvailable(device.Serial.PortName))
                    {
                        await FaultAndCloseAsync(current, $"串口 {device.Serial.PortName} 已断开或不可用。", null).ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Could not check serial port {Port}", device.Serial.PortName);
                }
            }
        }
        catch (OperationCanceledException) when (current.Cancellation.IsCancellationRequested) { }
    }

    private async Task FaultAndCloseAsync(Session current, string detail, Exception? exception)
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(session, current) && state == ConnectionState.Connected)
            {
                SetState(ConnectionState.Faulted, detail);
                logger.LogError(exception, "Serial communication failed for {DeviceId}: {Detail}", device.Id, detail);
            }
        }
        finally
        {
            lifecycle.Release();
        }
        await current.Cancellation.CancelAsync().ConfigureAwait(false);
        await current.CloseAsync().ConfigureAwait(false);
    }

    private async Task AwaitLoopAsync(Task? task)
    {
        if (task is null) return;
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (TimeoutException exception) { logger.LogWarning(exception, "Serial receive loop did not stop promptly for {DeviceId}", device.Id); }
    }

    private static void ObserveFault(Task task) => _ = task.ContinueWith(
        completed => _ = completed.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private void SetState(ConnectionState next, string? detail)
    {
        state = next;
        StateChanged?.Invoke(new DeviceStateChange(device.Id, next, detail));
    }

    private sealed class Session(ISerialPortTransport transport)
    {
        private readonly object closeGate = new();
        private Task? closeTask;
        public ISerialPortTransport Transport { get; } = transport;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Reader { get; set; }
        public Task? Monitor { get; set; }
        public Task CloseAsync()
        {
            lock (closeGate) return closeTask ??= Transport.DisposeAsync().AsTask();
        }
    }
}
