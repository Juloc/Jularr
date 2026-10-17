using System.Collections.Frozen;
using System.Globalization;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Logic.Media
{
    internal readonly record struct ArtworkSidecar(ArtworkSlot Slot, int? SeasonNumber = null, int? EpisodeNumber = null);

    // A single, media-independent vocabulary for local artwork discovery and NAS export.
    // Recognition is deliberately conservative: ambiguous names do not assign artwork to a Work.
    internal static class ArtworkFileNames
    {
        private static readonly FrozenSet<string> ImageExtensions =
            new[] { ".jpg", ".jpeg", ".png", ".webp", ".jxl" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static ArtworkSidecar? Recognize(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath[0] == '/' || relativePath.Contains('\\'))
            {
                return null;
            }

            var segments = relativePath.Split('/');
            if (segments.Any(part => part.Length == 0 || part is "." or ".." || part.StartsWith('.')))
            {
                return null;
            }

            var filename = segments[^1];
            var extension = Path.GetExtension(filename);
            if (!ImageExtensions.Contains(extension))
            {
                return null;
            }

            var stem = filename[..^extension.Length].ToLowerInvariant();
            var slot = stem switch
            {
                "poster" or "cover" or "folder" => ArtworkSlot.Poster,
                "fanart" or "backdrop" or "background" => ArtworkSlot.Backdrop,
                "clearlogo" or "logo" => ArtworkSlot.Logo,
                "banner" => ArtworkSlot.Banner,
                "thumb" or "thumbnail" => ArtworkSlot.Thumbnail,
                _ => (ArtworkSlot?)null
            };
            if (slot is not null)
            {
                return new ArtworkSidecar(slot.Value);
            }

            // Plex/Kodi-compatible seasonXX-poster/fanart/banner names.
            var separator = stem.IndexOf('-');
            if (separator > 6 && stem.StartsWith("season", StringComparison.Ordinal) &&
                int.TryParse(stem.AsSpan(6, separator - 6), NumberStyles.None, CultureInfo.InvariantCulture, out var season) &&
                season is >= 0 and <= 999 &&
                stem[(separator + 1)..] is var suffix)
            {
                var seasonSlot = suffix switch
                {
                    "poster" => ArtworkSlot.Poster,
                    "fanart" => ArtworkSlot.Backdrop,
                    "banner" => ArtworkSlot.Banner,
                    _ => (ArtworkSlot?)null
                };
                if (seasonSlot is not null)
                {
                    return new ArtworkSidecar(seasonSlot.Value, season);
                }
            }

            return null;
        }

        // A JXL-only NAS remains an explicit opt-in; the default names work with other media servers.
        public static string ExportName(ArtworkSlot slot, bool jxl)
        {
            var stem = slot switch
            {
                ArtworkSlot.Poster => "poster",
                ArtworkSlot.Backdrop => "fanart",
                ArtworkSlot.Logo => "clearlogo",
                ArtworkSlot.Banner => "banner",
                ArtworkSlot.Thumbnail => "thumb",
                _ => throw new ArgumentOutOfRangeException(nameof(slot))
            };
            return stem + (jxl ? ".jxl" : slot == ArtworkSlot.Logo ? ".png" : ".jpg");
        }

        // Pinned manual choices always win. Unpinned NAS artwork beats remote provider images.
        public static int SourceRank(ArtworkSourceKind kind, bool pinned) =>
            pinned ? 0 : kind switch
            {
                ArtworkSourceKind.Manual => 1,
                ArtworkSourceKind.Sidecar => 2,
                ArtworkSourceKind.Provider => 3,
                ArtworkSourceKind.Embedded => 4,
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
    }
}
