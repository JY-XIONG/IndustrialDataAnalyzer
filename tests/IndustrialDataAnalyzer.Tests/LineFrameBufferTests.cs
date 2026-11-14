using System.IO;
using System.Text;
using IndustrialDataAnalyzer.Infrastructure.Communication;

namespace IndustrialDataAnalyzer.Tests;

public sealed class LineFrameBufferTests
{
    [Fact]
    public void ReassemblesSplitAndCombinedFrames()
    {
        var buffer = new LineFrameBuffer("\\r\\n", "UTF-8");
        Assert.Empty(buffer.Append(Encoding.UTF8.GetBytes("12\r")));
        Assert.Equal(["12", "34"], buffer.Append(Encoding.UTF8.GetBytes("\n34\r\n56\r")));
        Assert.Equal(["56"], buffer.Append(Encoding.UTF8.GetBytes("\n")));
    }

    [Fact]
    public void ReassemblesSplitUtf8CodePoint()
    {
        var buffer = new LineFrameBuffer("\\n", "UTF-8");
        byte[] frame = Encoding.UTF8.GetBytes("温度=23\n");
        Assert.Empty(buffer.Append(frame.AsSpan(0, 2)));
        Assert.Equal(["温度=23"], buffer.Append(frame.AsSpan(2)));
    }

    [Fact]
    public void OversizedFrameIsRejectedAndBufferCanRecover()
    {
        var buffer = new LineFrameBuffer("\\n", "ASCII");
        Assert.Throws<InvalidDataException>(() => buffer.Append(new byte[70 * 1024]));
        Assert.Equal(["3"], buffer.Append(Encoding.ASCII.GetBytes("3\n")));
    }
}
