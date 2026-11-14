using System.Globalization;
using System.Text.RegularExpressions;
using IndustrialDataAnalyzer.Core.Abstractions;
using IndustrialDataAnalyzer.Core.Models;

namespace IndustrialDataAnalyzer.Services.Devices;

/// <summary>Extracts one numeric field without allowing malformed frames to escape.</summary>
public sealed class FrameParser : IFrameParser
{
    public ParsedFrame Parse(RawFrame raw, ParserOptions options)
    {
        try
        {
            string candidate = options.Mode switch
            {
                ParserMode.Numeric => raw.Text.Trim(),
                ParserMode.Delimited => ParseDelimited(raw.Text, options),
                ParserMode.Regex => ParseRegex(raw.Text, options),
                _ => throw new FormatException("不支持的解析方式。")
            };
            if (!double.TryParse(candidate.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                !double.IsFinite(value))
            {
                throw new FormatException("报文中的测量值不是有效数字。");
            }
            return new ParsedFrame(raw, options.FieldName, options.Unit, value, null);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or RegexMatchTimeoutException)
        {
            return new ParsedFrame(raw, options.FieldName, options.Unit, null, exception.Message);
        }
    }

    private static string ParseDelimited(string text, ParserOptions options)
    {
        if (string.IsNullOrEmpty(options.Separator) || options.FieldIndex < 0)
        {
            throw new FormatException("分隔符或字段序号无效。");
        }
        string[] parts = text.Split([options.Separator], StringSplitOptions.None);
        return options.FieldIndex < parts.Length ? parts[options.FieldIndex] :
            throw new FormatException("报文字段数量不足。");
    }

    private static string ParseRegex(string text, ParserOptions options)
    {
        Match match = Regex.Match(text, options.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success)
        {
            throw new FormatException("报文不匹配正则表达式。");
        }
        Group group = match.Groups[options.CaptureGroup];
        if (!group.Success)
        {
            throw new FormatException("正则表达式没有指定的捕获组。");
        }
        return group.Value;
    }
}
