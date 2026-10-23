using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Integrations.Logic.Media;

namespace Jularr.Backend.Data.Common
{
    // The one rule for stored covers: their names, their addresses and the widths they are kept in.
    public static partial class CoverPaths
    {
        public const string Prefix = "/api/v1/covers/";

        // The widths of the cache for titles that are not Works; its address names the provider's image.
        public static readonly IReadOnlyList<int> ExternalWidths = [250, 500, 1280];
        public const string ExternalPath = "/api/v1/covers/external";

        public static string External(string providerUrl, int? width = null) =>
            ExternalPath + "?u=" + Uri.EscapeDataString(providerUrl) + (width is { } wide ? "&w=" + wide.ToString(CultureInfo.InvariantCulture) : "");

        public static IReadOnlyList<int> Widths(CoverKind kind) => kind switch
        {
            CoverKind.Poster => [250, 500],
            CoverKind.Backdrop => [500, 1280],
            _ => [250]
        };

        // The formats the Accept header asks for by name; */* and image/* do not count, and a type with q=0 is refused. JPEG XL is offered only to browsers that ask for it.
        public static CoverFormats Accepts(string? accept)
        {
            var formats = CoverFormats.None;
            foreach (var range in (accept ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = range.Split(';', StringSplitOptions.TrimEntries);
                var refused = parts.Skip(1).Any(part => part.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(part.AsSpan(2), NumberStyles.Float, CultureInfo.InvariantCulture, out var weight) && weight <= 0);
                if (refused)
                {
                    continue;
                }

                if (parts[0].Equals("image/webp", StringComparison.OrdinalIgnoreCase))
                {
                    formats |= CoverFormats.WebP;
                }
                else if (parts[0].Equals("image/jxl", StringComparison.OrdinalIgnoreCase))
                {
                    formats |= CoverFormats.Jxl;
                }
            }

            return formats;
        }

        // The files a width can be kept in, best first: its extension, its content type and what the browser has to name to take it (JPEG always).
        public static readonly IReadOnlyList<(string Extension, string ContentType, CoverFormats Needs)> WidthFormats =
        [
            ("jxl", "image/jxl", CoverFormats.Jxl),
            ("webp", "image/webp", CoverFormats.WebP),
            ("jpg", "image/jpeg", CoverFormats.None)
        ];

        // The extensions a width is tried in: the chosen format, then what the tools can still make; JPEG is always the last resort.
        public static IReadOnlyList<string> Extensions(CoverFormat format) => format == CoverFormat.Jxl ? ["jxl", "webp", "jpg"] : ["webp", "jpg"];

        // The extension of the stored file that answers: the best one the browser takes, none when every stored file is a format it does not name.
        public static string? Serve(IEnumerable<string> present, CoverFormats accepts)
        {
            var kept = present.ToHashSet(StringComparer.Ordinal);
            return WidthFormats.FirstOrDefault(format => kept.Contains(format.Extension) && accepts.HasFlag(format.Needs)).Extension;
        }

        // The extension made on the fly for a browser that takes none of the stored formats.
        public static string Derive(CoverFormats accepts) => accepts.HasFlag(CoverFormats.WebP) ? "webp" : "jpg";

        // The first bytes of a written file name its format; a file that does not is no result.
        public static bool IsFormat(ReadOnlySpan<byte> header, string extension) => extension switch
        {
            "jxl" => header is [0xFF, 0x0A, ..] || header.Length >= 8 && header[4..8].SequenceEqual("JXL "u8),
            "webp" => header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8),
            "jpg" => header is [0xFF, 0xD8, 0xFF, ..],
            _ => false
        };

        // Added to the ETag so each format of one width is its own representation.
        public static string ETagSuffix(string contentType) => contentType switch
        {
            "image/jxl" => "x",
            "image/webp" => "w",
            _ => ""
        };

        // What the file is called inside its Work's folder and as the last part of the address.
        public static string Name(CoverKind kind, long? workUnitId) => kind switch
        {
            CoverKind.Poster => "poster",
            CoverKind.Backdrop => "backdrop",
            _ => "unit-" + (workUnitId ?? throw new ArgumentNullException(nameof(workUnitId))).ToString(CultureInfo.InvariantCulture)
        };

        public static string Url(long workId, CoverKind kind, long? workUnitId, string version) =>
            Prefix + workId.ToString(CultureInfo.InvariantCulture) + "/" + Name(kind, workUnitId) + "?v=" + version;

        public static bool IsLocal(string url) => url.StartsWith(Prefix, StringComparison.Ordinal);

        // poster, backdrop-1280, unit-12-250: the stored image and, after a dash, one of its widths.
        public static bool TryParse(string name, out CoverKind kind, out long? workUnitId, out int? width)
        {
            kind = default;
            workUnitId = null;
            width = null;
            var match = NamePattern().Match(name);
            if (!match.Success)
            {
                return false;
            }

            kind = match.Groups[1].Value switch
            {
                "poster" => CoverKind.Poster,
                "backdrop" => CoverKind.Backdrop,
                _ => CoverKind.UnitImage
            };
            if (kind == CoverKind.UnitImage)
            {
                if (!long.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var unit) || unit < 1)
                {
                    return false;
                }

                workUnitId = unit;
            }

            if (match.Groups[3].Success)
            {
                width = int.Parse(match.Groups[3].ValueSpan, CultureInfo.InvariantCulture);
                if (!Widths(kind).Contains(width.Value))
                {
                    return false;
                }
            }

            return true;
        }

        // The address of one stored width, for the browser to choose from.
        public static IReadOnlyList<(string Url, int Width)> Variants(string url)
        {
            if (CoverClient.IsProviderAddress(url))
            {
                return [.. ExternalWidths.Select(item => (External(url, item), item))];
            }

            var match = LocalPattern().Match(url);
            if (!match.Success || !TryParse(match.Groups[2].Value, out var kind, out _, out var width) || width is not null)
            {
                return [];
            }

            return [.. Widths(kind).Select(item => (match.Groups[1].Value + match.Groups[2].Value + "-" + item.ToString(CultureInfo.InvariantCulture) + match.Groups[3].Value, item))];
        }

        // The smallest stored width that is at least this wide. A Work's image without one is its original; a provider's image is never fetched by the browser itself, so the widest one answers.
        public static string Sized(string url, int width)
        {
            var variants = Variants(url);
            foreach (var variant in variants)
            {
                if (variant.Width >= width)
                {
                    return variant.Url;
                }
            }

            return variants.Count > 0 && !IsLocal(url) ? variants[^1].Url : url;
        }

        [GeneratedRegex("^(poster|backdrop|unit-([0-9]{1,18}))(?:-([0-9]{3,4}))?$", RegexOptions.CultureInvariant)]
        private static partial Regex NamePattern();

        [GeneratedRegex("^(/api/v1/covers/[0-9]+/)((?:poster|backdrop|unit-[0-9]+)(?:-[0-9]+)?)(\\?v=[0-9a-f]+)?$", RegexOptions.CultureInvariant)]
        private static partial Regex LocalPattern();
    }
}
