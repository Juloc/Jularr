using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Integrations.Data.Storage;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class ImportPlacementLogic
    {
        private static readonly HashSet<char> InvalidNameCharacters = [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '/', '\\', '|', '?', '*'];

        public static string? MapRemotePath(string storagePath, string remotePath)
        {
            // Only paths below the client's mapped folder are imported.
            var normalized = storagePath.Replace('\\', '/').TrimEnd('/');
            var prefix = remotePath.Replace('\\', '/').TrimEnd('/');
            if (!normalized.Equals(prefix, StringComparison.Ordinal) && !normalized.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                return null;
            }

            var rest = normalized[prefix.Length..].TrimStart('/');
            return rest.Split('/').Any(segment => segment is "." or "..") ? null : rest;
        }

        public static IReadOnlyList<ImportPlacementData> Plan(MediaType mediaType, string title, int? year, string? qualityKey, IReadOnlyList<StorageFile> files)
        {
            var videos = files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) == MediaAssetType.Video && !MediaPathParser.IsSample(file.RelativePath)).ToArray();
            var name = SafeName(title);
            var quality = qualityKey is { Length: > 0 } ? " [" + SafeName(qualityKey) + "]" : "";
            var placements = new List<ImportPlacementData>();
            if (mediaType == MediaType.Movie)
            {
                // The largest video is the feature.
                if (videos.MaxBy(video => video.SizeBytes) is { } feature)
                {
                    var folder = year is { } value ? $"{name} ({value.ToString(CultureInfo.InvariantCulture)})" : name;
                    AddGroup(placements, files, feature, $"{folder}/{folder}{quality}", null, null, null);
                }

                return placements;
            }

            // Episodes keep their numbering.
            foreach (var video in videos)
            {
                if (!ReleaseParser.TryParse(Path.GetFileNameWithoutExtension(video.RelativePath), out var parsed) || parsed.SeasonNumber is not { } season || parsed.EpisodeStart is not { } episode)
                {
                    continue;
                }

                var seasonText = season.ToString("00", CultureInfo.InvariantCulture);
                AddGroup(placements, files, video, $"{name}/Season {seasonText}/{name} - S{seasonText}E{episode.ToString("00", CultureInfo.InvariantCulture)}{quality}", WorkUnitType.Episode,
                    episode, season);
            }

            return placements;
        }

        // Taken names are never reused; a counter keeps both files.
        public static string Alternative(string stem, int attempt) => attempt <= 1 ? stem : $"{stem} ({attempt.ToString(CultureInfo.InvariantCulture)})";

        public static bool IsStorable(string relativePath)
        {
            try
            {
                StoredFileFields.RelativePath.Validate(relativePath, nameof(StoredFileFields.RelativePath));
                return true;
            }
            catch (DataValidationException)
            {
                return false;
            }
        }

        private static void AddGroup(List<ImportPlacementData> placements, IReadOnlyList<StorageFile> files, StorageFile video, string stem, WorkUnitType? unitType, decimal? unitNumber,
            decimal? parentUnitNumber)
        {
            var group = placements.Count;
            var extension = Path.GetExtension(video.RelativePath).ToLowerInvariant();
            if (!IsStorable(stem + extension))
            {
                return;
            }

            placements.Add(new ImportPlacementData(group, video.RelativePath, stem, extension, true, unitType, unitNumber, parentUnitNumber));

            // Subtitles named like the video follow it.
            var videoStem = video.RelativePath[..^Path.GetExtension(video.RelativePath).Length];
            foreach (var subtitle in files.Where(file => file.RelativePath.StartsWith(videoStem + ".", StringComparison.Ordinal) && LibraryFileTypes.IsSubtitle(file.RelativePath)))
            {
                var suffix = subtitle.RelativePath[videoStem.Length..].ToLowerInvariant();
                if (!suffix.Contains('/') && IsStorable(stem + suffix))
                {
                    placements.Add(new ImportPlacementData(group, subtitle.RelativePath, stem, suffix, false, null, null, null));
                }
            }
        }

        private static string SafeName(string value)
        {
            var cleaned = new string(value.Select(character => InvalidNameCharacters.Contains(character) || char.IsControl(character) ? ' ' : character).ToArray());
            return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ') is { Length: > 0 } name ? name : "Untitled";
        }
    }
}
