using System.IO.Ports;
using IndustrialDataAnalyzer.Core.Models;

namespace IndustrialDataAnalyzer.Infrastructure.Communication;

/// <summary>Small transport seam for serial lifecycle and timeout tests.</summary>
internal interface ISerialPortTransport : IAsyncDisposable
{
    bool IsOpen { get; }
    Task OpenAsync(CancellationToken cancellationToken);
    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
}

internal sealed class SerialPortTransport : ISerialPortTransport
{
    private readonly SerialPort port;
    private int disposed;

    public SerialPortTransport(SerialOptions options)
    {
        port = new SerialPort(options.PortName, options.BaudRate, (Parity)options.Parity,
            options.DataBits, options.StopBits switch
            {
                SerialStopBits.One => StopBits.One,
                SerialStopBits.OnePointFive => StopBits.OnePointFive,
                SerialStopBits.Two => StopBits.Two,
                _ => throw new ArgumentOutOfRangeException(nameof(options), "无效的停止位配置。")
            })
        {
            Handshake = (Handshake)options.FlowControl,
            ReadTimeout = 1000,
            WriteTimeout = 1000
        };
    }

    public bool IsOpen => Volatile.Read(ref disposed) == 0 && port.IsOpen;
    public Task OpenAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(SerialPortTransport));
        port.Open();
        if (Volatile.Read(ref disposed) != 0)
        {
            port.Dispose();
            throw new ObjectDisposedException(nameof(SerialPortTransport));
        }
    }, cancellationToken);
    public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        port.BaseStream.ReadAsync(buffer, cancellationToken).AsTask();

    public async Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        await port.BaseStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return ValueTask.CompletedTask;
        return new ValueTask(Task.Run(port.Dispose));
    }
}
