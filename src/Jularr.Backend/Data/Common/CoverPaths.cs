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
