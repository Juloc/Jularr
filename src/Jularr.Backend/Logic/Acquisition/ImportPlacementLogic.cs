using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Integrations.Data.Media;
using Jularr.Integrations.Data.Storage;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static partial class ImportPlacementLogic
    {
        // Deleting a finished client's source is safe only if the import accounted for every file.
        public static bool AllSourceFilesSelected(IEnumerable<string> sourceFiles, IEnumerable<string> selectedFiles)
        {
            var sources = sourceFiles.ToArray();
            var selected = selectedFiles.ToHashSet(StringComparer.Ordinal);
            // An empty or incomplete inventory never authorizes deleting a client's source.
            return sources.Length > 0 && selected.Count > 0
                && sources.All(path => !string.IsNullOrWhiteSpace(path) && selected.Contains(path));
        }

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

        // Episodes are a series' units, volumes a light novel's.
        public static IReadOnlyList<ImportPlacementData> Plan(MediaType mediaType, MediaSubtype? subtype, string title, int? year, string? qualityKey, IReadOnlyList<StorageFile> files,
            NamingSet patterns, ReleaseFact? facts, string? author = null, int trackCount = 0, string? platform = null, IReadOnlyList<TargetUnit>? episodes = null,
            IReadOnlyDictionary<string, AudioTags>? tags = null, NamingValues? known = null)
        {
            if (mediaType == MediaType.Book)
            {
                return subtype == MediaSubtype.Manga ? PlanManga(title, files, patterns, known) : PlanBook(subtype, title, year, author, files, patterns, known, episodes ?? []);
            }

            if (mediaType == MediaType.Music)
            {
                return PlanMusic(title, year, author, files, trackCount, tags ?? new Dictionary<string, AudioTags>(), patterns, known);
            }

            if (mediaType == MediaType.Game)
            {
                return PlanGame(title, year, platform, files, patterns, known);
            }

            var videos = files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) == MediaAssetType.Video && !MediaPathParser.IsSample(file.RelativePath)).ToArray();
            var placements = new List<ImportPlacementData>();
            if (mediaType == MediaType.Movie)
            {
                // The largest video is the feature.
                if (videos.MaxBy(video => video.SizeBytes) is { } feature)
                {
                    AddGroup(placements, files, feature, NamingPattern.Apply(patterns.Pattern(MediaType.Movie, subtype),
                        Values(MediaType.Movie, subtype, title, year, feature, patterns, known) with { Quality = qualityKey, ReleaseGroup = facts?.ReleaseGroup, Edition = facts?.Edition, Revision = facts?.Revision ?? 1 }), null, null, null);
                }

                return placements;
            }

            // Episodes keep their numbering; absolute and dated names take the episode they map to.
            foreach (var video in videos)
            {
                if (!ReleaseParser.TryParse(Path.GetFileNameWithoutExtension(video.RelativePath), out var parsed) || EpisodeNumbering.Resolve(parsed, episodes ?? []) is not var (season, episode, end))
                {
                    continue;
                }

                if (end < episode || end - episode > 9999) continue;

                // The placed episode brings its absolute number and air date to the name.
                var unit = episodes?.FirstOrDefault(candidate => candidate.ParentNumber == season && candidate.Number == episode);
                AddGroup(placements, files, video, NamingPattern.Apply(patterns.Pattern(MediaType.Series, subtype, parsed.AirDate is not null),
                    Values(MediaType.Series, subtype, title, year, video, patterns, known) with
                    {
                        Season = season, Episode = episode, EpisodeNumbers = Enumerable.Range(episode, end - episode + 1).Select(number => (decimal)number).ToArray(), Quality = qualityKey, ReleaseGroup = facts?.ReleaseGroup ?? parsed.ReleaseGroup, Edition = facts?.Edition, Revision = facts?.Revision ?? 1,
                        AbsoluteNumbers = AbsoluteNumbers(episodes ?? [], season, episode, end),
                        EpisodeTitle = unit?.Title,
                        Absolute = unit?.Absolute,
                        AirDate = unit?.ReleasedOn ?? parsed.AirDate
                    }), WorkUnitType.Episode, episode, season, Enumerable.Range(episode + 1, end - episode).Select(number => (decimal)number).ToArray());
            }

            return placements;
        }

        // The videos a person chose, each as the film or as the episode they say; every file is named by its own release facts, with the quality the person gave instead of its name's, and subtitles named like it follow.
        public static IReadOnlyList<ImportPlacementData> PlanChosen(MediaType mediaType, MediaSubtype? subtype, string title, int? year, IReadOnlyList<StorageFile> files,
            IReadOnlyList<(string Path, int? Season, int? Episode)> choices, NamingSet patterns, IReadOnlyList<TargetUnit> episodes, IReadOnlyDictionary<string, string>? qualities = null, NamingValues? known = null)
        {
            var placements = new List<ImportPlacementData>();
            var isMovie = mediaType == MediaType.Movie;
            foreach (var (path, season, episode) in choices)
            {
                if (files.FirstOrDefault(file => file.RelativePath == path && LibraryFileTypes.GetAssetType(file.RelativePath) == MediaAssetType.Video) is not { } video)
                {
                    continue;
                }

                var facts = ReleaseFacts.WithQuality(ReleaseFacts.OfPath(path, isMovie), path, qualities?.GetValueOrDefault(path));
                if (isMovie)
                {
                    AddGroup(placements, files, video, NamingPattern.Apply(patterns.Pattern(MediaType.Movie, subtype),
                        Values(MediaType.Movie, subtype, title, year, video, patterns, known) with { Quality = facts?.QualityKey, ReleaseGroup = facts?.ReleaseGroup, Edition = facts?.Edition, Revision = facts?.Revision ?? 1 }), null, null, null);
                }
                else if (season is { } seasonNumber && episode is { } episodeNumber)
                {
                    var parsedSource = ReleaseParser.TryParse(Path.GetFileNameWithoutExtension(path), out var parsed);
                    var daily = parsedSource && parsed.AirDate is not null;
                    var range = parsedSource ? EpisodeNumbering.Resolve(parsed, episodes) : null;
                    var end = range is { } matched && matched.Season == seasonNumber && matched.Start == episodeNumber && matched.End >= episodeNumber && matched.End - episodeNumber <= 9999 ? matched.End : episodeNumber;
                    var numbers = Enumerable.Range(episodeNumber, end - episodeNumber + 1).Select(number => (decimal)number).ToArray();
                    var unit = episodes.FirstOrDefault(candidate => candidate.ParentNumber == seasonNumber && candidate.Number == episodeNumber);
                    AddGroup(placements, files, video, NamingPattern.Apply(patterns.Pattern(MediaType.Series, subtype, daily),
                        Values(MediaType.Series, subtype, title, year, video, patterns, known) with
                        {
                            Season = seasonNumber, Episode = episodeNumber, EpisodeNumbers = numbers, AbsoluteNumbers = AbsoluteNumbers(episodes, seasonNumber, episodeNumber, end), Quality = facts?.QualityKey, ReleaseGroup = facts?.ReleaseGroup, Edition = facts?.Edition, Revision = facts?.Revision ?? 1,
                            EpisodeTitle = unit?.Title,
                            Absolute = unit?.Absolute,
                            AirDate = unit?.ReleasedOn
                        }), WorkUnitType.Episode, episodeNumber, seasonNumber, numbers.Skip(1).ToArray());
                }
            }

            return placements;
        }

        // One file per ebook format, the largest; the author names the folder. A light novel's pack of numbered volumes places one file per volume and format on its unit instead;
        // a volume a present file already holds stays as it is, and a pattern without a volume still keeps the volumes apart.
        private static IReadOnlyList<ImportPlacementData> PlanBook(MediaSubtype? subtype, string title, int? year, string? author, IReadOnlyList<StorageFile> files, NamingSet patterns, NamingValues? known,
            IReadOnlyList<TargetUnit> volumes)
        {
            var placements = new List<ImportPlacementData>();
            if (PackVolumes(subtype, files, volumes) is { } pack)
            {
                var pattern = patterns.Pattern(MediaType.Book, subtype);
                if (!pattern.Contains("{Volume", StringComparison.OrdinalIgnoreCase))
                {
                    pattern += " {Volume Label:00}";
                }

                foreach (var volume in pack.Where(item => item.Unit is { HasFile: false }).GroupBy(item => (item.Unit!.Number, Path.GetExtension(item.File.RelativePath).ToLowerInvariant()))
                    .Select(group => group.MaxBy(item => item.File.SizeBytes)).OrderBy(item => item.Unit!.Number))
                {
                    var stem = NamingPattern.Apply(pattern, Values(MediaType.Book, subtype, title, year, volume.File, patterns, known) with { Author = BookReleaseJudge.Authors(author).FirstOrDefault(), Volume = volume.Unit!.Number });
                    AddGroup(placements, files, volume.File, stem, WorkUnitType.Volume, volume.Unit.Number, null);
                }

                return placements;
            }

            foreach (var book in files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) == MediaAssetType.Ebook)
                .GroupBy(file => Path.GetExtension(file.RelativePath).ToLowerInvariant())
                .Select(format => format.MaxBy(file => file.SizeBytes)!))
            {
                var stem = NamingPattern.Apply(patterns.Pattern(MediaType.Book, subtype), Values(MediaType.Book, subtype, title, year, book, patterns, known) with { Author = BookReleaseJudge.Authors(author).FirstOrDefault() });
                var extension = Path.GetExtension(book.RelativePath).ToLowerInvariant();
                if (LibraryNaming.IsStorable(stem + extension))
                {
                    placements.Add(new ImportPlacementData(placements.Count, book.RelativePath, stem, extension, true, null, null, null));
                }
            }

            return placements;
        }

        // The ebooks of a light novel's pack that name no volume of the Work; null when the download is no such pack.
        public static IReadOnlyList<string>? UnmatchedVolumes(MediaSubtype? subtype, IReadOnlyList<StorageFile> files, IReadOnlyList<TargetUnit> volumes) =>
            PackVolumes(subtype, files, volumes) is { } pack ? [.. pack.Where(item => item.Unit is null).Select(item => item.File.RelativePath).Order(StringComparer.Ordinal)] : null;

        // A pack names at least two different volumes and its Work has volumes; a range, an unnumbered file or a number the Work does not have is no unit.
        private static (StorageFile File, TargetUnit? Unit)[]? PackVolumes(MediaSubtype? subtype, IReadOnlyList<StorageFile> files, IReadOnlyList<TargetUnit> volumes)
        {
            if (subtype != MediaSubtype.LightNovel || volumes.Count == 0)
            {
                return null;
            }

            var ebooks = files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) == MediaAssetType.Ebook).ToArray();
            var named = ebooks.Select(file => (File: file, Name: BookReleaseJudge.ReadName(Path.GetFileNameWithoutExtension(file.RelativePath)))).ToArray();
            var numbered = NumberedFiles(ebooks);

            // The volume a file names: "v01" style names first, else the number the pack's own file names carry; a range is no single volume.
            int? NumberOf((StorageFile File, BookReleaseName Name) item) =>
                item.Name.VolumeStart is { } start ? item.Name.VolumeEnd is null ? start : null : numbered.TryGetValue(item.File.RelativePath, out var number) ? number : null;

            if (named.Select(NumberOf).OfType<int>().Distinct().Count() < 2)
            {
                return null;
            }

            return [.. named.Select(item => (item.File, NumberOf(item) is { } number ? volumes.FirstOrDefault(unit => unit.ParentNumber is null && unit.Number == number) : null))];
        }

        // Volume numbers of a pack whose names have no "v01": the Japanese "第01巻", or "<title> 01" where most files share the title and differ in the number
        // (a spin-off or special with another title stays unnumbered, so it never lands on a volume of the main work).
        private static Dictionary<string, int> NumberedFiles(IReadOnlyList<StorageFile> ebooks)
        {
            var kanji = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var file in ebooks)
            {
                if (KanjiVolume().Match(Path.GetFileNameWithoutExtension(file.RelativePath)) is { Success: true } match) kanji[file.RelativePath] = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
            }

            if (kanji.Values.Distinct().Count() >= 2) return kanji;
            var group = ebooks.Select(file => (File: file, Match: TrailingNumber().Match(Path.GetFileNameWithoutExtension(file.RelativePath)))).Where(item => item.Match.Success)
                .GroupBy(item => item.Match.Groups["title"].Value.Trim(), StringComparer.Ordinal).OrderByDescending(item => item.Count()).FirstOrDefault();
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            if (group is null || group.Select(item => item.Match.Groups["number"].Value).Distinct().Count() < 2) return result;
            foreach (var item in group) result[item.File.RelativePath] = int.Parse(item.Match.Groups["number"].Value, CultureInfo.InvariantCulture);
            return result;
        }

        // One file per volume, the largest; a single unnumbered file is the whole series. A file holding a range is no single volume.
        private static IReadOnlyList<ImportPlacementData> PlanManga(string title, IReadOnlyList<StorageFile> files, NamingSet patterns, NamingValues? known)
        {
            var documents = files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) is MediaAssetType.Comic or MediaAssetType.Ebook)
                .Select(file => (File: file, Parsed: MangaReleaseJudge.Parse(Path.GetFileNameWithoutExtension(file.RelativePath))))
                .Where(document => document.Parsed.VolumeEnd is null || document.Parsed.VolumeEnd == document.Parsed.VolumeStart)
                .Select(document => (document.File, Volume: document.Parsed.VolumeStart))
                .ToArray();
            var placements = new List<ImportPlacementData>();
            foreach (var volume in documents.Where(document => document.Volume is not null).GroupBy(document => document.Volume!.Value).Select(group => group.MaxBy(document => document.File.SizeBytes)))
            {
                AddGroup(placements, files, volume.File, NamingPattern.Apply(patterns.Pattern(MediaType.Book, MediaSubtype.Manga), Values(MediaType.Book, MediaSubtype.Manga, title, known?.Year, volume.File, patterns, known) with { Volume = volume.Volume }), WorkUnitType.Volume, volume.Volume, null);
            }

            if (placements.Count == 0 && documents.Length == 1)
            {
                AddGroup(placements, files, documents[0].File, NamingPattern.Apply(patterns.Pattern(MediaType.Book, MediaSubtype.Manga), Values(MediaType.Book, MediaSubtype.Manga, title, known?.Year, documents[0].File, patterns, known)), null, null, null);
            }

            return placements;
        }

        // Tracks keep their tagged disc and track order, else the release's natural order, and land on their units only when the album's track count matches.
        // A tagged title names the file; the artist names the folder.
        private static IReadOnlyList<ImportPlacementData> PlanMusic(string title, int? year, string? artist, IReadOnlyList<StorageFile> files, int trackCount,
            IReadOnlyDictionary<string, AudioTags> tags, NamingSet patterns, NamingValues? known)
        {
            var tracks = files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) == MediaAssetType.Audio)
                .OrderBy(file => NaturalKey(file.RelativePath), StringComparer.OrdinalIgnoreCase).ToArray();
            if (tracks.All(file => tags.GetValueOrDefault(file.RelativePath)?.Track is not null))
            {
                tracks = [.. tracks.OrderBy(file => tags[file.RelativePath].Disc ?? 1).ThenBy(file => tags[file.RelativePath].Track)];
            }

            var numbered = tracks.All(file => Path.GetFileName(file.RelativePath) is { Length: > 0 } name && char.IsAsciiDigit(name[0]));
            var units = tracks.Length == trackCount;
            var placements = new List<ImportPlacementData>();
            for (var index = 0; index < tracks.Length; index++)
            {
                var stem = Path.GetFileNameWithoutExtension(tracks[index].RelativePath);
                var name = tags.GetValueOrDefault(tracks[index].RelativePath)?.Title is { } tagged ? LibraryNaming.SafeName(tagged)
                    : (numbered ? TrackNumber().Replace(stem, "") : stem).Trim(' ', '-', '.', '_');
                AddGroup(placements, files, tracks[index], NamingPattern.Apply(patterns.Pattern(MediaType.Music, null), Values(MediaType.Music, null, title, year, tracks[index], patterns, known) with { Artist = artist, Track = tags.GetValueOrDefault(tracks[index].RelativePath)?.Track ?? index + 1, TrackTitle = name.Length > 0 ? name : "Track " + (index + 1), Disc = tags.GetValueOrDefault(tracks[index].RelativePath)?.Disc ?? 1, DiscCount = tracks.Select(track => tags.GetValueOrDefault(track.RelativePath)?.Disc ?? 1).Max() }), units ? WorkUnitType.Track : null, units ? index + 1 : null, null);
            }

            return placements;
        }

        // "2 - Two" sorts before "10 - Ten".
        public static string NaturalKey(string path) => Digits().Replace(path, match => match.Value.PadLeft(10, '0'));

        // A game keeps its release's files below its platform; the largest game file is the media.
        private static IReadOnlyList<ImportPlacementData> PlanGame(string title, int? year, string? platform, IReadOnlyList<StorageFile> files, NamingSet patterns, NamingValues? known)
        {
            if (files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) == MediaAssetType.Game).MaxBy(file => file.SizeBytes) is not { } main)
            {
                return [];
            }

            // Relative paths inside a multi-file release may be referenced by cue sheets or playlists.
            // Only a single-file release can safely change its file name.
            var release = files.Where(file => !ReleaseNoise.Contains(Path.GetExtension(file.RelativePath).ToLowerInvariant())).ToArray();
            if (release.Length > 1 && release.Any(file => file.RelativePath.Split('/').Any(part => LibraryNaming.SafeName(part) != part))) return [];
            var (region, version) = GameFileName.Facts(main.RelativePath);
            var values = Values(MediaType.Game, null, title, year, main, patterns, known) with { Platform = platform ?? "Unknown", Region = region, GameVersion = version };
            var named = NamingPattern.Apply(patterns.Pattern(MediaType.Game, null), values);
            var folder = NamingPattern.Split(named).Folder;
            var placements = new List<ImportPlacementData>();
            foreach (var file in release)
            {
                var extension = release.Length > 1 ? Path.GetExtension(file.RelativePath) : Path.GetExtension(file.RelativePath).ToLowerInvariant();
                var stem = release.Length == 1 ? named : NamingPattern.Join(folder, string.Join('/', file.RelativePath[..^extension.Length].Split('/').Select(LibraryNaming.SafeName)));
                if (!LibraryNaming.IsStorable(stem + extension))
                {
                    return [];
                }

                placements.Add(new ImportPlacementData(placements.Count, file.RelativePath, stem, extension, ReferenceEquals(file, main), null, null, null));
            }

            return placements;
        }

        private static IReadOnlyList<decimal> AbsoluteNumbers(IReadOnlyList<TargetUnit> episodes, int season, int first, int last)
        {
            var numbered = episodes.Where(unit => unit.ParentNumber == season && unit.Number >= first && unit.Number <= last).OrderBy(unit => unit.Number)
                .Select(unit => unit.Absolute).ToArray();
            return numbered.Length == last - first + 1 && numbered.All(number => number is not null) ? numbered.Select(number => (decimal)number!.Value).ToArray() : [];
        }

        // Catalog identities are already loaded with the wanted target; naming does no provider calls.
        public static NamingValues KnownValues(WantedTargetData target) => new(target.CanonicalTitle, target.ReleaseYear, null, null, null, null, null, null, 1)
        {
            MediaTypeId = target.MediaTypeId, MediaSubtypeId = target.MediaSubtypeId,
            Author = BookReleaseJudge.Authors(target.Author).FirstOrDefault(), Artist = target.Author,
            Platform = target.PlatformAbbreviation, Language = target.OriginalLanguage,
            ProviderIds = target.ExternalIdentities.Select(identity => identity.Split(':', 2)).Where(parts => parts.Length == 2)
                .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First()[1], StringComparer.OrdinalIgnoreCase)
        };

        private static NamingValues Values(MediaType type, MediaSubtype? subtype, string title, int? year, StorageFile file, NamingSet patterns, NamingValues? known) =>
            (known ?? new NamingValues(title, year, null, null, null, null, null, null, 1)) with
            {
                Title = title, Year = year, MediaTypeId = type, MediaSubtypeId = subtype, Rules = patterns.Rules(type),
                OriginalFilename = Path.GetFileNameWithoutExtension(file.RelativePath), Format = Path.GetExtension(file.RelativePath).TrimStart('.').ToUpperInvariant()
            };

        private static void AddGroup(List<ImportPlacementData> placements, IReadOnlyList<StorageFile> files, StorageFile media, string stem, WorkUnitType? unitType, decimal? unitNumber,
            decimal? parentUnitNumber, IReadOnlyList<decimal>? furtherEpisodes = null)
        {
            var group = placements.Count;
            var extension = Path.GetExtension(media.RelativePath).ToLowerInvariant();
            if (!LibraryNaming.IsStorable(stem + extension))
            {
                return;
            }

            placements.Add(new ImportPlacementData(group, media.RelativePath, stem, extension, true, unitType, unitNumber, parentUnitNumber, furtherEpisodes));

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

        [GeneratedRegex(@"第\s?(?<number>\d{1,4})\s?巻")]
        private static partial Regex KanjiVolume();

        [GeneratedRegex(@"^(?<title>.*\D)[\s._-]+(?<number>\d{1,3})$")]
        private static partial Regex TrailingNumber();

        [GeneratedRegex(@"\d+")]
        private static partial Regex Digits();
    }
}
