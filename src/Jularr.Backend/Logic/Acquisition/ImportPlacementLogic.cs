using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Integrations.Data.Storage;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static partial class ImportPlacementLogic
    {
        // Download bookkeeping that never belongs in a library.
        private static readonly HashSet<string> ReleaseNoise = [".par2", ".sfv", ".nfo", ".srr", ".url", ".diz", ".nzb"];

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

        public static IReadOnlyList<ImportPlacementData> Plan(MediaType mediaType, MediaSubtype? subtype, string title, int? year, string? qualityKey, IReadOnlyList<StorageFile> files,
            IReadOnlyDictionary<MediaType, string> patterns, ReleaseFact? facts, string? author = null, int trackCount = 0, string? platform = null)
        {
            if (mediaType == MediaType.Book)
            {
                return subtype == MediaSubtype.Manga ? PlanManga(title, files) : PlanBook(title, year, author, files);
            }

            if (mediaType == MediaType.Music)
            {
                return PlanMusic(title, year, author, files, trackCount);
            }

            if (mediaType == MediaType.Game)
            {
                return PlanGame(title, year, platform, files);
            }

            var videos = files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) == MediaAssetType.Video && !MediaPathParser.IsSample(file.RelativePath)).ToArray();
            var placements = new List<ImportPlacementData>();
            if (mediaType == MediaType.Movie)
            {
                // The largest video is the feature.
                if (videos.MaxBy(video => video.SizeBytes) is { } feature)
                {
                    AddGroup(placements, files, feature, NamingPattern.Apply(patterns[MediaType.Movie],
                        new NamingValues(title, year, null, null, null, qualityKey, facts?.ReleaseGroup, facts?.Edition, facts?.Revision ?? 1)), null, null, null);
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

                AddGroup(placements, files, video, NamingPattern.Apply(patterns[MediaType.Series],
                    new NamingValues(title, year, season, episode, null, qualityKey, facts?.ReleaseGroup ?? parsed.ReleaseGroup, facts?.Edition, facts?.Revision ?? 1)), WorkUnitType.Episode, episode, season);
            }

            return placements;
        }

        // One file per ebook format, the largest; the author names the folder.
        private static IReadOnlyList<ImportPlacementData> PlanBook(string title, int? year, string? author, IReadOnlyList<StorageFile> files)
        {
            var stem = LibraryNaming.Book(title, year, author);
            var placements = new List<ImportPlacementData>();
            foreach (var book in files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) == MediaAssetType.Ebook)
                .GroupBy(file => Path.GetExtension(file.RelativePath).ToLowerInvariant())
                .Select(format => format.MaxBy(file => file.SizeBytes)!))
            {
                var extension = Path.GetExtension(book.RelativePath).ToLowerInvariant();
                if (LibraryNaming.IsStorable(stem + extension))
                {
                    placements.Add(new ImportPlacementData(placements.Count, book.RelativePath, stem, extension, true, null, null, null));
                }
            }

            return placements;
        }

        // One file per volume, the largest; a single unnumbered file is the whole series. A file holding a range is no single volume.
        private static IReadOnlyList<ImportPlacementData> PlanManga(string title, IReadOnlyList<StorageFile> files)
        {
            var documents = files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) is MediaAssetType.Comic or MediaAssetType.Ebook)
                .Select(file => (File: file, Parsed: MangaReleaseJudge.Parse(Path.GetFileNameWithoutExtension(file.RelativePath))))
                .Where(document => document.Parsed.VolumeEnd is null || document.Parsed.VolumeEnd == document.Parsed.VolumeStart)
                .Select(document => (document.File, Volume: document.Parsed.VolumeStart))
                .ToArray();
            var placements = new List<ImportPlacementData>();
            foreach (var volume in documents.Where(document => document.Volume is not null).GroupBy(document => document.Volume!.Value).Select(group => group.MaxBy(document => document.File.SizeBytes)))
            {
                AddGroup(placements, files, volume.File, LibraryNaming.Volume(title, volume.Volume), WorkUnitType.Volume, volume.Volume, null);
            }

            if (placements.Count == 0 && documents.Length == 1)
            {
                AddGroup(placements, files, documents[0].File, LibraryNaming.Volume(title, null), null, null, null);
            }

            return placements;
        }

        // Tracks keep the release's natural order and land on their units only when the album's track count matches; the artist names the folder.
        private static IReadOnlyList<ImportPlacementData> PlanMusic(string title, int? year, string? artist, IReadOnlyList<StorageFile> files, int trackCount)
        {
            var tracks = files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) == MediaAssetType.Audio)
                .OrderBy(file => Digits().Replace(file.RelativePath, match => match.Value.PadLeft(10, '0')), StringComparer.OrdinalIgnoreCase).ToArray();
            var numbered = tracks.All(file => Path.GetFileName(file.RelativePath) is { Length: > 0 } name && char.IsAsciiDigit(name[0]));
            var units = tracks.Length == trackCount;
            var placements = new List<ImportPlacementData>();
            for (var index = 0; index < tracks.Length; index++)
            {
                var stem = Path.GetFileNameWithoutExtension(tracks[index].RelativePath);
                var name = (numbered ? TrackNumber().Replace(stem, "") : stem).Trim(' ', '-', '.', '_');
                AddGroup(placements, files, tracks[index], LibraryNaming.Track(artist, title, year, index + 1, name), units ? WorkUnitType.Track : null, units ? index + 1 : null, null);
            }

            return placements;
        }

        // A game keeps its release's files below its platform; the largest game file is the media.
        private static IReadOnlyList<ImportPlacementData> PlanGame(string title, int? year, string? platform, IReadOnlyList<StorageFile> files)
        {
            var folder = LibraryNaming.Game(platform, title, year);
            if (files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) == MediaAssetType.Game).MaxBy(file => file.SizeBytes) is not { } main)
            {
                return [];
            }

            var placements = new List<ImportPlacementData>();
            foreach (var file in files.Where(file => !ReleaseNoise.Contains(Path.GetExtension(file.RelativePath).ToLowerInvariant())))
            {
                var extension = Path.GetExtension(file.RelativePath).ToLowerInvariant();
                var stem = folder + "/" + string.Join('/', file.RelativePath[..^extension.Length].Split('/').Select(LibraryNaming.SafeName));
                if (!LibraryNaming.IsStorable(stem + extension))
                {
                    return [];
                }

                placements.Add(new ImportPlacementData(placements.Count, file.RelativePath, stem, extension, ReferenceEquals(file, main), null, null, null));
            }

            return placements;
        }

        private static void AddGroup(List<ImportPlacementData> placements, IReadOnlyList<StorageFile> files, StorageFile media, string stem, WorkUnitType? unitType, decimal? unitNumber,
            decimal? parentUnitNumber)
        {
            var group = placements.Count;
            var extension = Path.GetExtension(media.RelativePath).ToLowerInvariant();
            if (!LibraryNaming.IsStorable(stem + extension))
            {
                return;
            }

            placements.Add(new ImportPlacementData(group, media.RelativePath, stem, extension, true, unitType, unitNumber, parentUnitNumber));

            // Subtitles named like the video follow it.
            var videoStem = media.RelativePath[..^Path.GetExtension(media.RelativePath).Length];
            foreach (var subtitle in files.Where(file => file.RelativePath.StartsWith(videoStem + ".", StringComparison.Ordinal) && LibraryFileTypes.IsSubtitle(file.RelativePath)))
            {
                var suffix = subtitle.RelativePath[videoStem.Length..].ToLowerInvariant();
                if (!suffix.Contains('/') && LibraryNaming.IsStorable(stem + suffix))
                {
                    placements.Add(new ImportPlacementData(group, subtitle.RelativePath, stem, suffix, false, null, null, null));
                }
            }
        }

        [GeneratedRegex(@"^\s*(?:\d{1,2}[-.])?\d{1,3}(?=[\s._-]|$)")]
        private static partial Regex TrackNumber();

        [GeneratedRegex(@"\d+")]
        private static partial Regex Digits();
    }
}
