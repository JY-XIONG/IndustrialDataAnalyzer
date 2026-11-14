using System.Text;

namespace IndustrialDataAnalyzer.Infrastructure.Communication;

/// <summary>Assembles complete text frames across arbitrary byte chunks.</summary>
public sealed class LineFrameBuffer
{
    private const int MaximumFrameBytes = 64 * 1024;
    private readonly List<byte> pending = [];
    private readonly byte[] ending;
    private readonly Encoding encoding;

    /// <summary>Creates a buffer with a configured text encoding and frame ending.</summary>
    public LineFrameBuffer(string encodedEnding, string encodingName)
    {
        string actualEnding = DecodeEnding(encodedEnding);
        if (actualEnding.Length == 0)
        {
            throw new ArgumentException("报文结束符不能为空。", nameof(encodedEnding));
        }

        encoding = Encoding.GetEncoding(encodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        ending = encoding.GetBytes(actualEnding);
    }

    /// <summary>Gets the bytes of the configured frame ending.</summary>
    public ReadOnlyMemory<byte> Ending => ending;

    /// <summary>Gets the strict text encoding used for frames.</summary>
    public Encoding Encoding => encoding;

    /// <summary>Adds bytes and returns each complete frame without its ending.</summary>
    public IReadOnlyList<string> Append(ReadOnlySpan<byte> bytes)
    {
        List<string> frames = [];
        foreach (byte value in bytes)
        {
            pending.Add(value);
            if (HasEnding())
            {
                int frameLength = pending.Count - ending.Length;
                if (frameLength > 0)
                {
                    frames.Add(encoding.GetString(pending.GetRange(0, frameLength).ToArray()));
                }
                pending.Clear();
            }
            else if (pending.Count > MaximumFrameBytes + ending.Length)
            {
                pending.Clear();
                throw new InvalidDataException("报文超过 64 KiB 限制，已丢弃未完成报文。");
            }
        }
        return frames;
    }

    /// <summary>Converts escaped CR, LF, and TAB text to actual delimiters.</summary>
    public static string DecodeEnding(string? value) => (value ?? string.Empty)
        .Replace("\\r", "\r", StringComparison.Ordinal)
        .Replace("\\n", "\n", StringComparison.Ordinal)
        .Replace("\\t", "\t", StringComparison.Ordinal);

    private bool HasEnding()
    {
        if (pending.Count < ending.Length)
        {
            return false;
        }
        for (int index = 0; index < ending.Length; index++)
        {
            if (pending[pending.Count - ending.Length + index] != ending[index])
            {
                return false;
            }
        }
        return true;
    }
}
