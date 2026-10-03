using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jularr.Web.Features.Library;

// Parses `ffprobe -show_format -show_streams -of json` output into the canonical inventory model.
public static partial class MediaProbeParser
{
    public static MediaTechnicalInfo Parse(string probeJson)
    {
        using var document = JsonDocument.Parse(probeJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("ffprobe output is not a JSON object.");
        }

        string? container = null;
        double? durationSeconds = null;
        if (root.TryGetProperty("format", out var format) &&
            format.ValueKind == JsonValueKind.Object)
        {
            container = ReadString(format, "format_name");
            durationSeconds = ReadPositiveDouble(format, "duration");
        }

        MediaVideoInfo? video = null;
        var streams = new List<MediaStreamInfo>();
        if (root.TryGetProperty("streams", out var streamArray) &&
            streamArray.ValueKind == JsonValueKind.Array)
        {
            var position = 0;
            foreach (var stream in streamArray.EnumerateArray())
            {
                var index = ReadInt(stream, "index") ?? position;
                position++;

                var type = ReadString(stream, "codec_type");
                if (string.Equals(type, "video", StringComparison.OrdinalIgnoreCase))
                {
                    // Cover art is exposed as an attached-picture video stream; it is not the programme video.
                    if (video is null && !ReadDisposition(stream, "attached_pic"))
                    {
                        video = ParseVideo(stream, index);
                    }
                }
                else if (string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase))
                {
                    streams.Add(ParseStream(stream, index, MediaStreamKind.Audio));
                }
                else if (string.Equals(type, "subtitle", StringComparison.OrdinalIgnoreCase))
                {
                    streams.Add(ParseStream(stream, index, MediaStreamKind.Subtitle));
                }
            }
        }

        return new MediaTechnicalInfo(
            container,
            durationSeconds,
            video,
            [.. streams.OrderBy(x => x.Index)]);
    }

    private static MediaVideoInfo ParseVideo(JsonElement stream, int streamIndex)
    {
        var pixelFormat = ReadString(stream, "pix_fmt");
        return new MediaVideoInfo(
            ReadString(stream, "codec_name"),
            ReadString(stream, "profile"),
            ReadInt(stream, "width"),
            ReadInt(stream, "height"),
            pixelFormat,
            ReadBitDepth(stream, pixelFormat),
            ReadDynamicRange(stream),
            streamIndex);
    }

    private static MediaStreamInfo ParseStream(
        JsonElement stream,
        int index,
        MediaStreamKind kind)
    {
        string? language = null;
        string? title = null;
        if (stream.TryGetProperty("tags", out var tags) &&
            tags.ValueKind == JsonValueKind.Object)
        {
            language = ReadTag(tags, "language");
            title = ReadTag(tags, "title");
        }

        return new MediaStreamInfo(
            index,
            kind,
            ReadString(stream, "codec_name"),
            language,
            title,
            kind == MediaStreamKind.Audio ? ReadInt(stream, "channels") : null,
            kind == MediaStreamKind.Audio ? ReadString(stream, "channel_layout") : null,
            ReadDisposition(stream, "default"),
            ReadDisposition(stream, "forced"));
    }

    private static int? ReadBitDepth(JsonElement stream, string? pixelFormat)
    {
        if (ReadInt(stream, "bits_per_raw_sample") is { } rawBits and >= 8 and <= 16)
        {
            return rawBits;
        }

        if (pixelFormat is null)
        {
            return null;
        }

        var match = PixelFormatBitDepth().Match(pixelFormat);
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var bits) &&
            bits is >= 9 and <= 16)
        {
            return bits;
        }

        return pixelFormat.StartsWith("yuv", StringComparison.OrdinalIgnoreCase) ||
               pixelFormat.StartsWith("nv12", StringComparison.OrdinalIgnoreCase)
            ? 8
            : null;
    }

    private static string ReadDynamicRange(JsonElement stream)
    {
        var codecTag = ReadString(stream, "codec_tag_string");
        if (codecTag is "dvh1" or "dvhe" or "dav1" or "dva1" ||
            HasDolbyVisionSideData(stream))
        {
            return "Dolby Vision";
        }

        return ReadString(stream, "color_transfer")?.ToLowerInvariant() switch
        {
            "smpte2084" => "HDR10",
            "arib-std-b67" => "HLG",
            _ => "SDR"
        };
    }

    private static bool HasDolbyVisionSideData(JsonElement stream) =>
        stream.TryGetProperty("side_data_list", out var sideData) &&
        sideData.ValueKind == JsonValueKind.Array &&
        sideData.EnumerateArray().Any(item =>
            ReadString(item, "side_data_type")?.Contains(
                "DOVI",
                StringComparison.OrdinalIgnoreCase) == true);

    private static bool ReadDisposition(JsonElement stream, string flag) =>
        stream.TryGetProperty("disposition", out var disposition) &&
        disposition.ValueKind == JsonValueKind.Object &&
        disposition.TryGetProperty(flag, out var value) &&
        ((value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number != 0) ||
         value.ValueKind == JsonValueKind.True);

    // Container muxers differ in tag casing (Matroska "language" vs. "LANGUAGE").
    private static string? ReadTag(JsonElement tags, string name)
    {
        foreach (var property in tags.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString()?.Trim();
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    // ffprobe prints some integers (bits_per_raw_sample) as JSON strings.
    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? ReadPositiveDouble(JsonElement element, string propertyName) =>
        ReadString(element, propertyName) is { } text &&
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
        double.IsFinite(value) &&
        value > 0
            ? value
            : null;

    [GeneratedRegex(@"(\d{2})(?:le|be)$", RegexOptions.CultureInvariant)]
    private static partial Regex PixelFormatBitDepth();
}
