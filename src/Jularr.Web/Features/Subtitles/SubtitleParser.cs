using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Jularr.Web.Features.Subtitles;

public static partial class SubtitleParser
{
    [GeneratedRegex(@"(?<h>\d{1,2}):(?<m>\d{2}):(?<s>\d{2})[,.](?<ms>\d{2,3})", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"^(?:(?<h>\d{1,3}):)?(?<m>\d{2}):(?<s>\d{2})\.(?<ms>\d{3})$", RegexOptions.CultureInvariant)]
    private static partial Regex VttTimestampRegex();

    [GeneratedRegex(@"\{[^}]*\}|<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex FormattingRegex();

    [GeneratedRegex(@"<rt[^>]*>.*?</rt>|<rp[^>]*>.*?</rp>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RubyAnnotationRegex();

    public static IReadOnlyList<SubtitleCueData> Parse(string path, string content) =>
        ParseFormat(Path.GetExtension(path), content);

    public static IReadOnlyList<SubtitleCueData> ParseFormat(string format, string content) =>
        format.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "srt" => ParseSrt(content),
            "ass" or "ssa" => ParseAss(content),
            "vtt" => ParseVtt(content),
            _ => throw new NotSupportedException($"Unsupported subtitle format: {format}")
        };

    public static IReadOnlyList<SubtitleCueData> ParseSrt(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var blocks = Regex.Split(normalized.Trim(), @"\n{2,}");
        var result = new List<SubtitleCueData>(blocks.Length);

        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var timingIndex = Array.FindIndex(lines, line => line.Contains("-->", StringComparison.Ordinal));
            if (timingIndex < 0)
            {
                continue;
            }

            var timing = lines[timingIndex].Split("-->", StringSplitOptions.TrimEntries);
            if (timing.Length != 2 ||
                !TryParseTimestamp(timing[0], out var start) ||
                !TryParseTimestamp(timing[1], out var end))
            {
                continue;
            }

            var text = string.Join(" ", lines.Skip(timingIndex + 1));
            text = CleanText(text);
            if (text.Length > 0)
            {
                result.Add(new SubtitleCueData(start, end, text));
            }
        }

        return result;
    }

    public static IReadOnlyList<SubtitleCueData> ParseAss(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var result = new List<SubtitleCueData>();
        var styles = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string[]? styleFormat = null;
        string[]? eventFormat = null;
        var section = "";
        var playResX = 0;
        var playResY = 0;

        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                section = line;
                continue;
            }

            if (section.Equals("[Script Info]", StringComparison.OrdinalIgnoreCase))
            {
                if (line.StartsWith("PlayResX:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line["PlayResX:".Length..].Trim(), CultureInfo.InvariantCulture, out playResX);
                }
                else if (line.StartsWith("PlayResY:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line["PlayResY:".Length..].Trim(), CultureInfo.InvariantCulture, out playResY);
                }
                continue;
            }

            if (section.Equals("[V4+ Styles]", StringComparison.OrdinalIgnoreCase) ||
                section.Equals("[V4 Styles]", StringComparison.OrdinalIgnoreCase))
            {
                if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    styleFormat = line["Format:".Length..].Split(',', StringSplitOptions.TrimEntries);
                }
                else if (styleFormat is not null && line.StartsWith("Style:", StringComparison.OrdinalIgnoreCase))
                {
                    var fields = line["Style:".Length..].Split(',', styleFormat.Length, StringSplitOptions.TrimEntries);
                    var style = ReadFields(styleFormat, fields);
                    if (section.Equals("[V4 Styles]", StringComparison.OrdinalIgnoreCase) &&
                        style.TryGetValue("Alignment", out var legacyValue) &&
                        int.TryParse(legacyValue, CultureInfo.InvariantCulture, out var legacyAlignment))
                    {
                        style["Alignment"] = MapLegacyAlignment(legacyAlignment)?.ToString(CultureInfo.InvariantCulture) ?? "";
                    }
                    if (style.TryGetValue("Name", out var name) && !string.IsNullOrWhiteSpace(name))
                    {
                        styles[name] = style;
                    }
                }
                continue;
            }

            if (!section.Equals("[Events]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                eventFormat = line["Format:".Length..].Split(',', StringSplitOptions.TrimEntries);
                continue;
            }

            if (eventFormat is null || !line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = line["Dialogue:".Length..].Split(',', eventFormat.Length, StringSplitOptions.None);
            var fieldsByName = ReadFields(eventFormat, values);
            if (!fieldsByName.TryGetValue("Start", out var from) ||
                !fieldsByName.TryGetValue("End", out var to) ||
                !fieldsByName.TryGetValue("Text", out var rawText) ||
                !TryParseTimestamp(from, out var start) ||
                !TryParseTimestamp(to, out var end))
            {
                continue;
            }

            var text = CleanText(rawText.Replace("\\N", " ", StringComparison.OrdinalIgnoreCase))
                .Replace("\\h", "\u00A0", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            fieldsByName.TryGetValue("Style", out var styleName);
            styles.TryGetValue(styleName ?? "", out var styleFields);
            var alignment = ReadInt(styleFields, "Alignment");
            var alignmentTags = Regex.Matches(rawText, @"\\an([1-9])|\\a(1[01]|[1-9])(?!\d)", RegexOptions.CultureInvariant);
            if (alignmentTags.Count > 0)
            {
                var lastTag = alignmentTags[^1];
                alignment = lastTag.Groups[1].Success
                    ? int.Parse(lastTag.Groups[1].Value, CultureInfo.InvariantCulture)
                    : MapLegacyAlignment(int.Parse(lastTag.Groups[2].Value, CultureInfo.InvariantCulture));
            }
            if (alignment is < 1 or > 9)
            {
                alignment = null;
            }

            double? xPercent = null;
            double? yPercent = null;
            var position = Regex.Match(
                rawText,
                @"\\pos\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (position.Success && playResX > 0 && playResY > 0 &&
                double.TryParse(position.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(position.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                var px = x * 100d / playResX;
                var py = y * 100d / playResY;
                if (double.IsFinite(px) && double.IsFinite(py) &&
                    px is >= 0 and <= 100 && py is >= 0 and <= 100)
                {
                    xPercent = px;
                    yPercent = py;
                }
            }

            var layer = ReadInt(fieldsByName, "Layer");
            var bold = ReadAssFlag(styleFields, "Bold");
            var italic = ReadAssFlag(styleFields, "Italic");
            var boldOverride = Regex.Match(rawText, @"\\b([01])", RegexOptions.CultureInvariant);
            var italicOverride = Regex.Match(rawText, @"\\i([01])", RegexOptions.CultureInvariant);
            if (boldOverride.Success) bold = boldOverride.Groups[1].Value == "1";
            if (italicOverride.Success) italic = italicOverride.Groups[1].Value == "1";

            string? fontFamily = null;
            double? fontSize = null;
            string? color = null;
            if (styleFields is not null)
            {
                if (styleFields.TryGetValue("Fontname", out var font) && font.Length <= 100)
                {
                    fontFamily = font;
                }
                if (styleFields.TryGetValue("Fontsize", out var size) &&
                    double.TryParse(size, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSize) &&
                    double.IsFinite(parsedSize) && parsedSize is >= 6 and <= 150)
                {
                    fontSize = parsedSize;
                }
                if (styleFields.TryGetValue("PrimaryColour", out var assColor))
                {
                    var hex = assColor.Trim().TrimStart('&').TrimEnd('&').TrimStart('H', 'h');
                    if ((hex.Length == 6 || hex.Length == 8) && hex.All(Uri.IsHexDigit))
                    {
                        hex = hex[^6..];
                        color = $"#{hex[4..6]}{hex[2..4]}{hex[..2]}";
                    }
                }
            }

            var presentation = alignment is not null || xPercent is not null || layer is not null ||
                bold is not null || italic is not null || fontFamily is not null || fontSize is not null || color is not null
                ? new SubtitleCuePresentation(alignment, xPercent, yPercent, layer,
                    fontFamily, fontSize, bold, italic, color)
                : null;
            result.Add(new SubtitleCueData(start, end, text, presentation));
        }

        return result;
    }

    private static int? MapLegacyAlignment(int value) =>
        value switch
        {
            1 or 2 or 3 => value,
            5 or 6 or 7 => value + 2,
            9 or 10 or 11 => value - 5,
            _ => null
        };

    private static Dictionary<string, string> ReadFields(string[] format, string[] values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values.Length != format.Length)
        {
            return result;
        }

        for (var index = 0; index < format.Length; index++)
        {
            result[format[index]] = values[index].Trim();
        }
        return result;
    }

    private static int? ReadInt(IReadOnlyDictionary<string, string>? fields, string key) =>
        fields is not null && fields.TryGetValue(key, out var value) &&
        int.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static bool? ReadAssFlag(IReadOnlyDictionary<string, string>? fields, string key) =>
        ReadInt(fields, key) is { } value ? value != 0 : null;

    public static IReadOnlyList<SubtitleCueData> ParseVtt(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var blocks = Regex.Split(normalized.Trim(), @"\n{2,}");
        var result = new List<SubtitleCueData>(blocks.Length);

        foreach (var block in blocks)
        {
            // Header, NOTE, STYLE and REGION blocks never contain a cue timing line.
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var timingIndex = Array.FindIndex(lines, line => line.Contains("-->", StringComparison.Ordinal));
            if (timingIndex < 0)
            {
                continue;
            }

            var timing = lines[timingIndex].Split("-->", StringSplitOptions.TrimEntries);
            var end = timing.Length == 2
                ? timing[1].Split(' ', '\t')[0]
                : "";
            if (timing.Length != 2 ||
                !TryParseVttTimestamp(timing[0], out var startMs) ||
                !TryParseVttTimestamp(end, out var endMs))
            {
                continue;
            }

            var text = string.Join(" ", lines.Skip(timingIndex + 1));
            text = WebUtility.HtmlDecode(CleanText(RubyAnnotationRegex().Replace(text, "")));
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length > 0)
            {
                result.Add(new SubtitleCueData(startMs, endMs, text));
            }
        }

        return result;
    }

    private static bool TryParseVttTimestamp(string value, out int milliseconds)
    {
        milliseconds = 0;
        var match = VttTimestampRegex().Match(value);
        if (!match.Success)
        {
            return false;
        }

        var hours = match.Groups["h"].Success
            ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture)
            : 0;
        var minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        var ms = int.Parse(match.Groups["ms"].Value, CultureInfo.InvariantCulture);

        milliseconds = (((hours * 60) + minutes) * 60 + seconds) * 1000 + ms;
        return true;
    }

    private static bool TryParseTimestamp(string value, out int milliseconds)
    {
        milliseconds = 0;
        var match = TimestampRegex().Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        var hours = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        var fraction = match.Groups["ms"].Value;
        var ms = fraction.Length == 2
            ? int.Parse(fraction, CultureInfo.InvariantCulture) * 10
            : int.Parse(fraction, CultureInfo.InvariantCulture);

        milliseconds = (int)TimeSpan.FromHours(hours).TotalMilliseconds
            + (int)TimeSpan.FromMinutes(minutes).TotalMilliseconds
            + (seconds * 1000)
            + ms;

        return true;
    }

    private static string CleanText(string value) =>
        Regex.Replace(FormattingRegex().Replace(value, ""), @"\s+", " ").Trim();
}
