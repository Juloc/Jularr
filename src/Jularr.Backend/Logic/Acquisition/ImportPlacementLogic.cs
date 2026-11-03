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
    // A file of a download that was not placed, and why; the reason names its text (acquisition.file.<reason>).
    internal sealed record UnplacedFile(string Path, string Reason)
    {
        public const string Extension = "extension";
        public const string NoNumber = "no_volume";
        public const string UnknownUnit = "unknown_unit";
        public const string Ambiguous = "ambiguous";
        public const string NotStorable = "not_storable";
        public const string Present = "present";

        // The file names no unit of the Work: a person says where it belongs.
        public bool NamesNoUnit => Reason is NoNumber or UnknownUnit or Ambiguous;
    }

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

        // Episodes are a series' units, volumes a book's; a manga's chapters come apart. A book's files that are not placed are added to unplaced with their reason.
        public static IReadOnlyList<ImportPlacementData> Plan(MediaType mediaType, MediaSubtype? subtype, string title, int? year, string? qualityKey, IReadOnlyList<StorageFile> files,
            NamingSet patterns, ReleaseFact? facts, string? author = null, int trackCount = 0, string? platform = null, IReadOnlyList<TargetUnit>? episodes = null,
            IReadOnlyDictionary<string, AudioTags>? tags = null, NamingValues? known = null, ICollection<UnplacedFile>? unplaced = null, IReadOnlyList<TargetUnit>? chapters = null)
        {
            if (mediaType == MediaType.Book)
            {
                unplaced ??= [];
                return subtype == MediaSubtype.Manga ? PlanManga(title, files, patterns, known, episodes ?? [], chapters ?? [], unplaced)
                    : PlanBook(subtype, title, year, author, files, patterns, known, episodes ?? [], unplaced);
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

        // The files a person chose, each as the film, the episode, or the book's volume or chapter they say; every file is named by its own release facts, with the quality the person gave instead of its name's,
        // and subtitles named like a video follow it. A book's Season is its volume and Episode a manga's chapter, whose volume is the Season or else the one the Work gives the chapter.
        public static IReadOnlyList<ImportPlacementData> PlanChosen(MediaType mediaType, MediaSubtype? subtype, string title, int? year, IReadOnlyList<StorageFile> files,
            IReadOnlyList<(string Path, int? Season, int? Episode)> choices, NamingSet patterns, IReadOnlyList<TargetUnit> episodes, IReadOnlyDictionary<string, string>? qualities = null, NamingValues? known = null,
            IReadOnlyList<TargetUnit>? chapters = null)
        {
            var placements = new List<ImportPlacementData>();
            var isMovie = mediaType == MediaType.Movie;
            foreach (var (path, season, episode) in choices)
            {
                if (mediaType == MediaType.Book)
                {
                    PlaceChosenBook(placements, subtype, title, year, files, path, season, episode, patterns, qualities?.GetValueOrDefault(path), known, chapters ?? []);
                    continue;
                }

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

        // One document as the volume, the manga chapter or the whole Work a person says it is, named like the import names it; a chapter of a book that is no manga is no placement.
        private static void PlaceChosenBook(List<ImportPlacementData> placements, MediaSubtype? subtype, string title, int? year, IReadOnlyList<StorageFile> files, string path, int? volume, int? chapter,
            NamingSet patterns, string? quality, NamingValues? known, IReadOnlyList<TargetUnit> chapters)
        {
            if (files.FirstOrDefault(file => file.RelativePath == path && LibraryFileTypes.GetAssetType(file.RelativePath) is MediaAssetType.Ebook or MediaAssetType.Comic) is not { } document
                || chapter is not null && subtype != MediaSubtype.Manga)
            {
                return;
            }

            var pattern = patterns.Pattern(MediaType.Book, subtype);
            var values = Values(MediaType.Book, subtype, title, year, document, patterns, known) with { Quality = quality };
            if (chapter is { } number)
            {
                var parent = volume ?? chapters.FirstOrDefault(unit => unit.Number == number)?.ParentNumber;
                AddGroup(placements, files, document, NamingPattern.Apply(pattern, values with { Volume = parent }) + " c" + number.ToString("000", CultureInfo.InvariantCulture), WorkUnitType.Chapter, number, parent);
            }
            else if (volume is { } volumeNumber)
            {
                // A pattern without a volume still keeps the volumes apart.
                if (!pattern.Contains("{Volume", StringComparison.OrdinalIgnoreCase))
                {
                    pattern += " {Volume Label:00}";
                }

                AddGroup(placements, files, document, NamingPattern.Apply(pattern, values with { Volume = volumeNumber }), WorkUnitType.Volume, volumeNumber, null);
            }
            else
            {
                AddGroup(placements, files, document, NamingPattern.Apply(pattern, values), null, null, null);
            }
        }

        // One file per ebook format, the largest; the author names the folder. A light novel's pack of numbered volumes places one file per volume and format on its unit instead;
        // a volume a present file already holds stays as it is, and a pattern without a volume still keeps the volumes apart.
        private static IReadOnlyList<ImportPlacementData> PlanBook(MediaSubtype? subtype, string title, int? year, string? author, IReadOnlyList<StorageFile> files, NamingSet patterns, NamingValues? known,
            IReadOnlyList<TargetUnit> volumes, ICollection<UnplacedFile> unplaced)
        {
            var placements = new List<ImportPlacementData>();
            foreach (var other in files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) != MediaAssetType.Ebook))
            {
                unplaced.Add(new UnplacedFile(other.RelativePath, UnplacedFile.Extension));
            }

            if (PackVolumes(subtype, files, volumes) is { } pack)
            {
                var pattern = patterns.Pattern(MediaType.Book, subtype);
                if (!pattern.Contains("{Volume", StringComparison.OrdinalIgnoreCase))
                {
                    pattern += " {Volume Label:00}";
                }

                foreach (var item in pack.Where(item => item.Unit is not { HasFile: false }))
                {
                    unplaced.Add(new UnplacedFile(item.File.RelativePath, item.Reason ?? UnplacedFile.Present));
                }

                foreach (var group in pack.Where(item => item.Unit is { HasFile: false }).GroupBy(item => (item.Unit!.Number, Path.GetExtension(item.File.RelativePath).ToLowerInvariant())).OrderBy(group => group.Key.Number))
                {
                    var volume = group.MaxBy(item => item.File.SizeBytes);
                    var stem = NamingPattern.Apply(pattern, Values(MediaType.Book, subtype, title, year, volume.File, patterns, known) with { Author = BookReleaseJudge.Authors(author).FirstOrDefault(), Volume = volume.Unit!.Number });
                    if (!AddGroup(placements, files, volume.File, stem, WorkUnitType.Volume, volume.Unit.Number, null))
                    {
                        NotStorable(unplaced, group.Select(item => item.File));
                    }
                }

                return placements;
            }

            foreach (var format in files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) == MediaAssetType.Ebook).GroupBy(file => Path.GetExtension(file.RelativePath).ToLowerInvariant()))
            {
                var book = format.MaxBy(file => file.SizeBytes)!;
                var stem = NamingPattern.Apply(patterns.Pattern(MediaType.Book, subtype), Values(MediaType.Book, subtype, title, year, book, patterns, known) with { Author = BookReleaseJudge.Authors(author).FirstOrDefault() });
                if (LibraryNaming.IsStorable(stem + format.Key))
                {
                    placements.Add(new ImportPlacementData(placements.Count, book.RelativePath, stem, format.Key, true, null, null, null));
                }
                else
                {
                    NotStorable(unplaced, format);
                }
            }

            return placements;
        }

        private static void NotStorable(ICollection<UnplacedFile> unplaced, IEnumerable<StorageFile> files)
        {
            foreach (var file in files)
            {
                unplaced.Add(new UnplacedFile(file.RelativePath, UnplacedFile.NotStorable));
            }
        }

        // A pack names at least two different volumes and its Work has volumes; a range, an unnumbered file or a number the Work does not have is no unit, and says so.
        private static (StorageFile File, TargetUnit? Unit, string? Reason)[]? PackVolumes(MediaSubtype? subtype, IReadOnlyList<StorageFile> files, IReadOnlyList<TargetUnit> volumes)
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

            return [.. named.Select(item => NumberOf(item) is { } number
                ? volumes.FirstOrDefault(unit => unit.ParentNumber is null && unit.Number == number) is { } unit ? (item.File, unit, null) : (item.File, (TargetUnit?)null, UnplacedFile.UnknownUnit)
                : (item.File, null, item.Name.VolumeStart is null ? UnplacedFile.NoNumber : UnplacedFile.Ambiguous))];
        }

        // Volume numbers of a pack whose names have no "v01": the Japanese "第01巻", or "<title> 01" (also "<title> - 01" and "<title> #01", bracketed tags aside) where most files share the title
        // and differ in the number (a spin-off or special with another title stays unnumbered, so it never lands on a volume of the main work); given the Work's title, a file of that title alone also counts.
        private static Dictionary<string, int> NumberedFiles(IReadOnlyList<StorageFile> documents, string? title = null)
        {
            var kanji = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var file in documents)
            {
                if (KanjiVolume().Match(Path.GetFileNameWithoutExtension(file.RelativePath)) is { Success: true } match) kanji[file.RelativePath] = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
            }

            if (kanji.Values.Distinct().Count() >= 2) return kanji;
            var trailing = documents.Select(file => (File: file, Match: TrailingNumber().Match(Tags().Replace(Path.GetFileNameWithoutExtension(file.RelativePath), "").Trim()))).Where(item => item.Match.Success)
                .Select(item => (item.File, Title: Comparable(item.Match.Groups["title"].Value), Number: int.Parse(item.Match.Groups["number"].Value, CultureInfo.InvariantCulture))).ToArray();
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            var group = trailing.GroupBy(item => item.Title, StringComparer.Ordinal).OrderByDescending(item => item.Count()).FirstOrDefault();
            if (group is not null && group.Select(item => item.Number).Distinct().Count() >= 2)
            {
                foreach (var item in group) result[item.File.RelativePath] = item.Number;
            }

            if (title is not null && Comparable(title) is { Length: > 0 } wanted)
            {
                foreach (var item in trailing.Where(item => item.Title == wanted)) result[item.File.RelativePath] = item.Number;
            }

            return result;
        }

        // Letters and digits only, for comparing titles across punctuation and case.
        private static string Comparable(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();

        // One file per volume or chapter, the largest; a single file without a marker is the whole series. "v01" names a volume, "Chapter 12" a chapter the Work has,
        // "<title> 01" a volume the Work has; a range, a part chapter or a number that is also one of the Work's chapters is never guessed.
        private static IReadOnlyList<ImportPlacementData> PlanManga(string title, IReadOnlyList<StorageFile> files, NamingSet patterns, NamingValues? known, IReadOnlyList<TargetUnit> volumes,
            IReadOnlyList<TargetUnit> chapters, ICollection<UnplacedFile> unplaced)
        {
            var documents = new List<StorageFile>();
            foreach (var file in files)
            {
                if (LibraryFileTypes.GetAssetType(file.RelativePath) is MediaAssetType.Comic or MediaAssetType.Ebook) documents.Add(file);
                else unplaced.Add(new UnplacedFile(file.RelativePath, UnplacedFile.Extension));
            }

            // What each file names.
            var volumeNumbers = volumes.Where(unit => unit.ParentNumber is null).Select(unit => unit.Number).ToHashSet();
            var chapterUnits = chapters.GroupBy(unit => unit.Number).ToDictionary(group => group.Key, group => group.First());
            var numbered = NumberedFiles(documents, title);
            var units = new List<(StorageFile File, WorkUnitType Type, int Number, int? Parent)>();
            var reasons = new List<UnplacedFile>();
            var unmarked = false;
            foreach (var document in documents)
            {
                var name = Path.GetFileNameWithoutExtension(document.RelativePath);
                var parsed = MangaReleaseJudge.Parse(name);
                var (marked, chapter) = MangaReleaseJudge.ReadChapter(name);
                if (chapter is { } chapterNumber && chapterUnits.TryGetValue(chapterNumber, out var chapterUnit))
                {
                    units.Add((document, WorkUnitType.Chapter, chapterNumber, chapterUnit.ParentNumber));
                }
                else if (parsed.VolumeStart is { } start)
                {
                    if (parsed.VolumeEnd is null || parsed.VolumeEnd == start) units.Add((document, WorkUnitType.Volume, start, null));
                    else reasons.Add(new UnplacedFile(document.RelativePath, UnplacedFile.Ambiguous));
                }
                else if (marked)
                {
                    reasons.Add(new UnplacedFile(document.RelativePath, chapter is null ? UnplacedFile.Ambiguous : UnplacedFile.UnknownUnit));
                }
                else if (numbered.TryGetValue(document.RelativePath, out var number))
                {
                    unmarked = true;
                    if (chapterUnits.ContainsKey(number)) reasons.Add(new UnplacedFile(document.RelativePath, UnplacedFile.Ambiguous));
                    else if (volumeNumbers.Contains(number)) units.Add((document, WorkUnitType.Volume, number, null));
                    else reasons.Add(new UnplacedFile(document.RelativePath, UnplacedFile.UnknownUnit));
                }
                else
                {
                    unmarked = true;
                    reasons.Add(new UnplacedFile(document.RelativePath, UnplacedFile.NoNumber));
                }
            }

            // A chapter is named like its volume, then its number.
            var placements = new List<ImportPlacementData>();
            var pattern = patterns.Pattern(MediaType.Book, MediaSubtype.Manga);
            foreach (var group in units.GroupBy(unit => (unit.Type, unit.Number)))
            {
                var unit = group.MaxBy(item => item.File.SizeBytes);
                var values = Values(MediaType.Book, MediaSubtype.Manga, title, known?.Year, unit.File, patterns, known);
                var stem = unit.Type == WorkUnitType.Volume ? NamingPattern.Apply(pattern, values with { Volume = unit.Number })
                    : NamingPattern.Apply(pattern, values with { Volume = unit.Parent }) + " c" + unit.Number.ToString("000", CultureInfo.InvariantCulture);
                if (!AddGroup(placements, files, unit.File, stem, unit.Type, unit.Number, unit.Parent))
                {
                    NotStorable(reasons, group.Select(item => item.File));
                }
            }

            // Its one document without a volume or chapter marker is the whole series.
            if (placements.Count == 0 && units.Count == 0 && documents.Count == 1 && unmarked)
            {
                if (AddGroup(placements, files, documents[0], NamingPattern.Apply(pattern, Values(MediaType.Book, MediaSubtype.Manga, title, known?.Year, documents[0], patterns, known)), null, null, null))
                {
                    return placements;
                }

                reasons = [new UnplacedFile(documents[0].RelativePath, UnplacedFile.NotStorable)];
            }

            foreach (var reason in reasons) unplaced.Add(reason);
            return placements;
        }

        // Every file of a download a person must look at, with why it was not placed: the reason the import gave, else that its format was not taken; download bookkeeping stays out.
        public static IReadOnlyList<ImportFileProposal> Unplaced(IReadOnlyList<StorageFile> download, IReadOnlyList<StorageFile> taken, IReadOnlyList<UnplacedFile> reasons)
        {
            var reasonOf = reasons.GroupBy(reason => reason.Path, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Reason, StringComparer.Ordinal);
            var takenPaths = taken.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
            return [.. download.Where(file => !ReleaseNoise.Contains(Path.GetExtension(file.RelativePath).ToLowerInvariant())).Select(file => file.RelativePath).Order(StringComparer.Ordinal)
                .Select(path => new ImportFileProposal(path, null, null, null, null, reasonOf.GetValueOrDefault(path) ?? (takenPaths.Contains(path) ? null : UnplacedFile.Extension)))];
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

        // False when the name made for the file cannot be stored.
        private static bool AddGroup(List<ImportPlacementData> placements, IReadOnlyList<StorageFile> files, StorageFile media, string stem, WorkUnitType? unitType, decimal? unitNumber,
            decimal? parentUnitNumber, IReadOnlyList<decimal>? furtherEpisodes = null)
        {
            var group = placements.Count;
            var extension = Path.GetExtension(media.RelativePath).ToLowerInvariant();
            if (!LibraryNaming.IsStorable(stem + extension))
            {
                return false;
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

            return true;
        }

        [GeneratedRegex(@"^\s*(?:\d{1,2}[-.])?\d{1,3}(?=[\s._-]|$)")]
        private static partial Regex TrackNumber();

        [GeneratedRegex(@"第\s?(?<number>\d{1,4})\s?巻")]
        private static partial Regex KanjiVolume();

        [GeneratedRegex(@"^(?<title>.*\D)(?:[\s._-]+#?|#)(?<number>\d{1,3})$")]
        private static partial Regex TrailingNumber();

        // Bracketed group, source and year tags in front of a name or after it.
        [GeneratedRegex(@"^\s*(?:[\[(][^\])]*[\])]\s*)+|(?:\s*[\[(][^\])]*[\])])+\s*$")]
        private static partial Regex Tags();

        [GeneratedRegex(@"\d+")]
        private static partial Regex Digits();
    }
}
