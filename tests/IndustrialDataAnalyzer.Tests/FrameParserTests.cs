using IndustrialDataAnalyzer.Core.Models;
using IndustrialDataAnalyzer.Services.Devices;

namespace IndustrialDataAnalyzer.Tests;

public sealed class FrameParserTests
{
    private readonly FrameParser parser = new();
    private static RawFrame Frame(string text) => new(Guid.NewGuid(), DateTimeOffset.UtcNow, FrameSource.Serial, text);

    [Theory]
    [InlineData("23.75", 23.75)]
    [InlineData(" -0.25 ", -0.25)]
    public void ParsesPlainNumericText(string text, double expected)
    {
        ParsedFrame result = parser.Parse(Frame(text), new ParserOptions { FieldName = "温度", Unit = "℃" });
        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value);
        Assert.Equal(text, result.Raw.Text);
        Assert.Equal("温度", result.FieldName);
    }

    [Fact]
    public void ParsesSelectedDelimitedField()
    {
        var options = new ParserOptions { Mode = ParserMode.Delimited, Separator = ";", FieldIndex = 1 };
        ParsedFrame result = parser.Parse(Frame("T;18.5;OK"), options);
        Assert.Equal(18.5, result.Value);
    }

    [Fact]
    public void ParsesRegexCaptureGroup()
    {
        var options = new ParserOptions { Mode = ParserMode.Regex, Pattern = @"W=(?<value>[-+]?\d+(?:\.\d+)?)", CaptureGroup = "value" };
        ParsedFrame result = parser.Parse(Frame("ID1,W=42.8,OK"), options);
        Assert.Equal(42.8, result.Value);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void InvalidNumericFrameReturnsErrorAndKeepsRawText(string text)
    {
        ParsedFrame result = parser.Parse(Frame(text), new ParserOptions());
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(text, result.Raw.Text);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void MissingFieldAndInvalidRegexReturnErrors()
    {
        Assert.False(parser.Parse(Frame("1,2"), new ParserOptions { Mode = ParserMode.Delimited, FieldIndex = 3 }).IsSuccess);
        Assert.False(parser.Parse(Frame("123"), new ParserOptions { Mode = ParserMode.Regex, Pattern = "[" }).IsSuccess);
    }
}
