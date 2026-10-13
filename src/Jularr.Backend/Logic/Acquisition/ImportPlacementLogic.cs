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

        public static IReadOnlyList<ImportPlacementData> Plan(MediaType mediaType, string title, int? year, string? qualityKey, IReadOnlyList<StorageFile> files, string? author = null)
        {
            if (mediaType is MediaType.Book or MediaType.LightNovel)
            {
                return PlanBook(title, year, author, files);
            }

            if (mediaType == MediaType.Manga)
            {
                return PlanManga(title, files);
            }

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

        // One file per ebook format, the largest; the author names the folder.
        private static IReadOnlyList<ImportPlacementData> PlanBook(string title, int? year, string? author, IReadOnlyList<StorageFile> files)
        {
            var name = SafeName(title);
            var folder = year is { } value ? $"{name} ({value.ToString(CultureInfo.InvariantCulture)})" : name;
            var writer = BookReleaseJudge.Authors(author).FirstOrDefault() is { } first ? SafeName(first) + "/" : "";
            var placements = new List<ImportPlacementData>();
            foreach (var book in files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) == MediaAssetType.Ebook)
                .GroupBy(file => Path.GetExtension(file.RelativePath).ToLowerInvariant())
                .Select(format => format.MaxBy(file => file.SizeBytes)!))
            {
                var extension = Path.GetExtension(book.RelativePath).ToLowerInvariant();
                var stem = $"{writer}{folder}/{name}";
                if (IsStorable(stem + extension))
                {
                    placements.Add(new ImportPlacementData(placements.Count, book.RelativePath, stem, extension, true, null, null, null));
                }
            }

            return placements;
        }

        // One file per volume, the largest; a single unnumbered file is the whole series. A file holding a range is no single volume.
        private static IReadOnlyList<ImportPlacementData> PlanManga(string title, IReadOnlyList<StorageFile> files)
        {
            var name = SafeName(title);
            var documents = files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) is MediaAssetType.Comic or MediaAssetType.Ebook)
                .Select(file => (File: file, Parsed: MangaReleaseJudge.Parse(Path.GetFileNameWithoutExtension(file.RelativePath))))
                .Where(document => document.Parsed.VolumeEnd is null || document.Parsed.VolumeEnd == document.Parsed.VolumeStart)
                .Select(document => (document.File, Volume: document.Parsed.VolumeStart))
                .ToArray();
            var placements = new List<ImportPlacementData>();
            foreach (var volume in documents.Where(document => document.Volume is not null).GroupBy(document => document.Volume!.Value).Select(group => group.MaxBy(document => document.File.SizeBytes)))
            {
                AddGroup(placements, files, volume.File, $"{name}/{name} v{volume.Volume!.Value.ToString("00", CultureInfo.InvariantCulture)}", WorkUnitType.Volume, volume.Volume, null);
            }

            if (placements.Count == 0 && documents.Length == 1)
            {
                AddGroup(placements, files, documents[0].File, $"{name}/{name}", null, null, null);
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

        private static void AddGroup(List<ImportPlacementData> placements, IReadOnlyList<StorageFile> files, StorageFile media, string stem, WorkUnitType? unitType, decimal? unitNumber,
            decimal? parentUnitNumber)
        {
            var group = placements.Count;
            var extension = Path.GetExtension(media.RelativePath).ToLowerInvariant();
            if (!IsStorable(stem + extension))
            {
                return;
            }

            placements.Add(new ImportPlacementData(group, media.RelativePath, stem, extension, true, unitType, unitNumber, parentUnitNumber));

            // Subtitles named like the video follow it.
            var videoStem = media.RelativePath[..^Path.GetExtension(media.RelativePath).Length];
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
