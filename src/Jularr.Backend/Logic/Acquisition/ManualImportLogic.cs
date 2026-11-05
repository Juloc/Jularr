using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Integrations.Data.Storage;

namespace Jularr.Backend.Logic.Acquisition
{
    // A file of a folder with what its name says it is and the name the library would give it. Of a series Season and Episode are the episode's numbers; of a book Season is the
    // volume and Episode a manga's chapter (Season then its volume), and neither is the whole Work.
    internal sealed record ManualImportProposal(string Path, long SizeBytes, int? Season, int? Episode, string? QualityKey, string? TargetName, string? Languages);

    // A film, series or book of the library a file of a folder may be: its wanted entry, its kind and every title it is known by.
    internal sealed record ManualImportCandidate(long WantedItemId, MediaType MediaTypeId, MediaSubtype? MediaSubtypeId, int? Year, IReadOnlyList<string> Titles);

    // What a file was recognised as: one title of the library, or none with why (no title fits, several do).
    internal sealed record ManualImportRecognition(ManualImportCandidate? Candidate, string? ProblemKey);

    // What a person sees and decides when a folder is imported by hand.
    internal static partial class ManualImportLogic
    {
        public const string WorkUnknown = "work_unknown";
        public const string WorkAmbiguous = "work_ambiguous";
        public const string EpisodeUnknown = "episode_unknown";
        public const string VolumeUnknown = "volume_unknown";
        public const string AlreadyInLibrary = "already_in_library";
        public const string LargerFileChosen = "larger_file_chosen";

        // The videos (no samples or extras), ebooks and comics of a folder of any kind, in the order of their names.
        public static StorageFile[] Importable(IEnumerable<StorageFile> files)
        {
            var all = files.ToArray();
            return [.. Importable(MediaType.Movie, all).Concat(Importable(MediaType.Book, all)).OrderBy(file => ImportPlacementLogic.NaturalKey(file.RelativePath), StringComparer.OrdinalIgnoreCase)];
        }

        public static bool IsDocument(string path) => LibraryFileTypes.GetAssetType(path) is MediaAssetType.Ebook or MediaAssetType.Comic;

        // What a file looks like by its name alone: a book's document, an episode (a name that counts seasons or episodes) or a film.
        public static MediaType KindOf(string path) => IsDocument(path) ? MediaType.Book
            : ReleaseParser.TryParse(Path.GetFileNameWithoutExtension(path), out var parsed) && (parsed.SeasonNumber is not null || parsed.AbsoluteEpisodeStart is not null || parsed.AirDate is not null)
                ? MediaType.Series : MediaType.Movie;

        // The names a file is known by, the most telling first: its own, then its folders up to the one a person chose, whose name ends the list.
        public static IReadOnlyList<string> Names(string path, string folderName)
        {
            var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var names = new List<string> { Path.GetFileNameWithoutExtension(parts[^1]) };
            names.AddRange(parts[..^1].Reverse());
            if (folderName.Length > 0) names.Add(folderName);
            return [.. names.Where(name => name.Trim().Length > 0).Distinct(StringComparer.Ordinal)];
        }

        // The three longest words of each name, one of which a title of the library must contain, as a search narrows the candidates before Recognise decides
        // (a name may carry edition words after its title, "Green Lantern 2011 Extended Cut").
        public static IReadOnlyList<string> SearchWords(IEnumerable<(string Name, bool Document)> names) => [.. names.Select(name => TitleOf(name.Name, name.Document))
            .Where(title => title is not null).SelectMany(title => TitleMatcher.SignificantWords(title!).Where(word => !word.All(char.IsAsciiDigit)).OrderByDescending(word => word.Length).Take(3))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        // The title a name gives: a release's title as the acquisition parses it, a book's without its volumes, format and tags.
        public static string? TitleOf(string name, bool document)
        {
            if (document)
            {
                var book = BookReleaseJudge.ReadName(name).Title;
                return book.Trim().Length > 0 ? book : null;
            }

            return ReleaseParser.TryParse(name, out var parsed) && parsed.SeriesTitle.Trim().Length > 0 && !parsed.SeriesTitle.Trim().All(char.IsAsciiDigit) ? parsed.SeriesTitle : null;
        }

        // The title of the library a file is: the first of its names that fits exactly one title decides; a video whose name counts episodes is an episode of a series, any other a film
        // (or, when no film fits, a series); an ebook or comic is a book. Several fitting titles and no clearer name leave it to a person.
        public static ManualImportRecognition Recognise(string path, IReadOnlyList<string> names, IReadOnlyList<ManualImportCandidate> candidates)
        {
            var document = IsDocument(path);
            var ambiguous = false;
            foreach (var name in names)
            {
                var fitting = document ? FittingBooks(name, candidates) : FittingVideos(name, candidates);
                if (fitting.Count == 1) return new ManualImportRecognition(fitting[0], null);
                ambiguous |= fitting.Count > 1;
            }

            return new ManualImportRecognition(null, ambiguous ? WorkAmbiguous : WorkUnknown);
        }

        private static IReadOnlyList<ManualImportCandidate> FittingVideos(string name, IReadOnlyList<ManualImportCandidate> candidates)
        {
            if (!ReleaseParser.TryParse(name, out var parsed) || parsed.SeriesTitle.Trim().Length == 0)
            {
                return [];
            }

            bool Fits(ManualImportCandidate candidate) => candidate.MediaTypeId == MediaType.Movie
                ? candidate.Titles.Any(title => VideoReleaseJudge.JudgeTitle(AcquisitionKind.Movie, [title], candidate.Year, parsed) == VideoIdentityMatch.Matches)
                : VideoReleaseJudge.JudgeTitle(candidate.MediaSubtypeId == MediaSubtype.Anime ? AcquisitionKind.Anime : AcquisitionKind.Tv, candidate.Titles, candidate.Year, parsed) == VideoIdentityMatch.Matches;

            var series = candidates.Where(candidate => candidate.MediaTypeId == MediaType.Series && Fits(candidate)).ToArray();
            if (parsed.SeasonNumber is not null || parsed.AbsoluteEpisodeStart is not null || parsed.AirDate is not null)
            {
                return series;
            }

            var films = candidates.Where(candidate => candidate.MediaTypeId == MediaType.Movie && Fits(candidate)).ToArray();
            return films.Length > 0 ? films : series;
        }

        private static IReadOnlyList<ManualImportCandidate> FittingBooks(string name, IReadOnlyList<ManualImportCandidate> candidates)
        {
            var book = BookReleaseJudge.ReadName(name);
            if (book.Title.Trim().Length == 0) return [];
            var books = candidates.Where(candidate => candidate.MediaTypeId == MediaType.Book).ToArray();

            // The whole name, else one of its parts around " - " ("Author - Title"), names the book exactly.
            string[] titles = [book.Title, .. name.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).Select(part => BookReleaseJudge.ReadName(part).Title)];
            foreach (var title in titles.Where(title => title.Trim().Length > 0))
            {
                var same = books.Where(candidate => TitleMatcher.Compare(candidate.Titles, title, candidate.Year) == TitleComparison.Same).ToArray();
                if (same.Length > 0) return same;
            }

            // A name that adds a subtitle still names a book whose whole title of two words or more it carries.
            return [.. books.Where(candidate => candidate.Titles.Any(title => TitleMatcher.SignificantWords(title).Count >= 2 && TitleMatcher.Matches(title, book.Title)))];
        }

        // The files of a title that are only a second file of the same film, episode, volume or chapter: of each the largest is the one.
        public static IReadOnlySet<string> Seconds(MediaType mediaType, IReadOnlyList<ManualImportProposal> proposals) => proposals
            .Select(proposal => (proposal, Key: mediaType switch
            {
                MediaType.Movie => "film",
                MediaType.Series when proposal.Season is { } season && proposal.Episode is { } episode => season + "x" + episode,
                MediaType.Book when proposal.TargetName is not null => proposal.Season + "/" + proposal.Episode,
                _ => null
            }))
            .Where(item => item.Key is not null).GroupBy(item => item.Key)
            .SelectMany(same => same.OrderByDescending(item => item.proposal.SizeBytes).Skip(1).Select(item => item.proposal.Path)).ToHashSet(StringComparer.Ordinal);

        // What a person must check of a recognised file: an episode without its numbers, a book's file that names no volume or chapter of it, a unit the library holds already.
        public static string? ProblemOf(MediaType mediaType, MediaSubtype? subtype, ManualImportProposal proposal, IReadOnlyList<TargetUnit> units, IReadOnlyList<TargetUnit> chapters,
            bool workHasMedia = false)
        {
            if (mediaType == MediaType.Series)
            {
                if (proposal.Season is not { } season || proposal.Episode is not { } episode) return EpisodeUnknown;
                return units.Any(unit => unit.ParentNumber == season && unit.Number == episode && unit.HasFile) ? AlreadyInLibrary : null;
            }

            if (mediaType == MediaType.Book)
            {
                if (proposal.TargetName is null) return VolumeUnknown;
                var held = proposal.Episode is { } chapter ? chapters.Any(unit => unit.Number == chapter && unit.HasFile)
                    : proposal.Season is { } volume ? units.Any(unit => unit.Number == volume && unit.HasFile) : workHasMedia;
                return held ? AlreadyInLibrary : null;
            }

            // A film the library holds already.
            return mediaType == MediaType.Movie && workHasMedia ? AlreadyInLibrary : null;
        }

        // An episode named only by its number ("03", "E03", "Episode 3") in a season folder ("Season 1", "S01", "Staffel 1"), as Sonarr reads such folders.
        public static (int Season, int Episode)? FolderEpisode(string path)
        {
            var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var number = BareEpisode().Match(Path.GetFileNameWithoutExtension(parts[^1]).Trim());
            if (!number.Success) return null;
            foreach (var folder in parts[..^1].Reverse())
            {
                if (SeasonFolder().Match(folder.Trim()) is { Success: true } season)
                {
                    return (int.Parse(season.Groups["season"].Value, CultureInfo.InvariantCulture), int.Parse(number.Groups["episode"].Value, CultureInfo.InvariantCulture));
                }
            }

            return null;
        }

        [GeneratedRegex(@"^(?:e|ep|episode|folge)?[ ._-]*0*(?<episode>\d{1,4})$", RegexOptions.IgnoreCase)]
        private static partial Regex BareEpisode();

        [GeneratedRegex(@"^(?:season|staffel|s)[ ._-]*0*(?<season>\d{1,3})$", RegexOptions.IgnoreCase)]
        private static partial Regex SeasonFolder();

        // The release group a name states: a video's from its release facts, a book's from its tags.
        public static string? ReleaseGroupOf(string path, MediaType? mediaType) => IsDocument(path)
            ? BookReleaseJudge.ReadName(Path.GetFileNameWithoutExtension(path)).Group
            : ReleaseFacts.OfPath(Path.ChangeExtension(path, null), mediaType == MediaType.Movie)?.ReleaseGroup;

        // The files a person can import: the videos of a film or series (no samples or extras), the ebooks and comics of a book, in the order of their names.
        public static StorageFile[] Importable(MediaType mediaType, IEnumerable<StorageFile> files) => [.. files.Where(file => mediaType == MediaType.Book
                ? LibraryFileTypes.GetAssetType(file.RelativePath) is MediaAssetType.Ebook or MediaAssetType.Comic
                : LibraryFileTypes.GetAssetType(file.RelativePath) == MediaAssetType.Video && !MediaPathParser.IsSample(file.RelativePath) && !ImportJudgement.IsExtra(file.RelativePath))
            .OrderBy(file => ImportPlacementLogic.NaturalKey(file.RelativePath), StringComparer.OrdinalIgnoreCase)];

        // Whether files still hold something an import takes: a video that is no sample or extra, a track, an ebook, a comic or a game. Release notes, pictures and subtitles do not count.
        public static bool HoldsMedia(IEnumerable<StorageFile> files) => files.Any(file => LibraryFileTypes.GetAssetType(file.RelativePath) switch
        {
            MediaAssetType.Video => !MediaPathParser.IsSample(file.RelativePath) && !ImportJudgement.IsExtra(file.RelativePath),
            null => false,
            _ => true
        });

        // A download is finished once every file it holds is imported, or when the person says so after they imported or left out what they wanted.
        public static bool CompletesDownload(MediaType mediaType, IEnumerable<StorageFile> files, IEnumerable<string> handled, bool finishes)
        {
            var done = handled.ToHashSet(StringComparer.Ordinal);
            return finishes || Importable(mediaType, files).All(file => done.Contains(file.RelativePath));
        }

        // The quality a file's name says: a film's or episode's release quality, a book's format.
        public static string? QualityOf(MediaType mediaType, MediaSubtype? subtype, string path) => mediaType == MediaType.Book
            ? AcquisitionSelectionLogic.QualityKey(AcquisitionSelectionLogic.GetKind(mediaType, subtype), Path.GetFileName(path))
            : ReleaseFacts.OfPath(path, mediaType == MediaType.Movie)?.QualityKey;

        public static IReadOnlyList<ManualImportProposal> Propose(MediaType mediaType, MediaSubtype? subtype, string title, int? year, IReadOnlyList<StorageFile> files, NamingSet patterns,
            IReadOnlyList<TargetUnit> units, NamingValues? known = null, IReadOnlyList<TargetUnit>? chapters = null)
        {
            if (mediaType == MediaType.Book)
            {
                return ProposeBook(subtype, title, year, files, patterns, units, known, chapters ?? []);
            }

            var videos = Importable(mediaType, files);
            var isMovie = mediaType == MediaType.Movie;
            var guesses = videos.Select(video =>
            {
                // A film needs no numbers; an episode takes the ones its name says, else a bare number below a season folder.
                if (isMovie || !ReleaseParser.TryParse(Path.GetFileNameWithoutExtension(video.RelativePath), out var parsed) || EpisodeNumbering.Resolve(parsed, units) is not var (season, start, _))
                {
                    return isMovie || FolderEpisode(video.RelativePath) is not var (folderSeason, folderEpisode) ? (video.RelativePath, Season: (int?)null, Episode: (int?)null)
                        : (video.RelativePath, Season: (int?)folderSeason, Episode: (int?)folderEpisode);
                }

                return (video.RelativePath, Season: (int?)season, Episode: (int?)start);
            }).ToArray();

            var named = ImportPlacementLogic.PlanChosen(mediaType, subtype, title, year, files, [.. guesses.Select(guess => (guess.RelativePath, guess.Season, guess.Episode))], patterns, units, known: known)
                .Where(placement => placement.IsMedia).ToDictionary(placement => placement.SourcePath, placement => placement.TargetStem + placement.Extension, StringComparer.Ordinal);
            return [.. videos.Zip(guesses, (video, guess) => new ManualImportProposal(video.RelativePath, video.SizeBytes, guess.Season, guess.Episode,
                QualityOf(mediaType, subtype, video.RelativePath), named.GetValueOrDefault(video.RelativePath), Languages(video.RelativePath)))];
        }

        // A book's documents with what the import itself would make of them: the volume or chapter their names say, else the whole Work; a file it would not place has no name until a person says what it is.
        private static IReadOnlyList<ManualImportProposal> ProposeBook(MediaSubtype? subtype, string title, int? year, IReadOnlyList<StorageFile> files, NamingSet patterns, IReadOnlyList<TargetUnit> volumes,
            NamingValues? known, IReadOnlyList<TargetUnit> chapters)
        {
            var documents = Importable(MediaType.Book, files);
            var recognised = ImportPlacementLogic.Plan(MediaType.Book, subtype, title, year, null, documents, patterns, null, known?.Author, episodes: volumes, known: known, chapters: chapters)
                .Where(placement => placement.IsMedia).ToDictionary(placement => placement.SourcePath, StringComparer.Ordinal);
            return [.. documents.Select(document =>
            {
                var placement = recognised.GetValueOrDefault(document.RelativePath);
                var (season, episode) = placement?.WorkUnitTypeId switch
                {
                    WorkUnitType.Volume => ((int?)placement.UnitNumber, (int?)null),
                    WorkUnitType.Chapter => ((int?)placement.ParentUnitNumber, (int?)placement.UnitNumber),
                    _ => ((int?)null, (int?)null)
                };
                var languages = ReleaseWords.ReadLanguages(Path.GetFileNameWithoutExtension(document.RelativePath), "");
                return new ManualImportProposal(document.RelativePath, document.SizeBytes, season, episode, QualityOf(MediaType.Book, subtype, document.RelativePath),
                    placement is null ? null : placement.TargetStem + placement.Extension, languages.Length > 0 ? string.Join('+', languages) : null);
            })];
        }

        // The names the library would give the chosen files, with the numbers and qualities their owners set.
        public static IReadOnlyDictionary<string, string> NameChosen(MediaType mediaType, MediaSubtype? subtype, string title, int? year, IReadOnlyList<(string Path, int? Season, int? Episode)> choices,
            IReadOnlyDictionary<string, string> qualities, NamingSet patterns, IReadOnlyList<TargetUnit> units, NamingValues? known = null, IReadOnlyList<TargetUnit>? chapters = null)
        {
            var files = choices.Select(choice => new StorageFile(choice.Path, 0, DateTime.UnixEpoch)).ToArray();
            return ImportPlacementLogic.PlanChosen(mediaType, subtype, title, year, files, choices, patterns, units, qualities, known, chapters).Where(placement => placement.IsMedia)
                .ToDictionary(placement => placement.SourcePath, placement => placement.TargetStem + placement.Extension, StringComparer.Ordinal);
        }

        // The audio languages a name states, as the acquisition reads them (an untagged name is English), such as DE+EN.
        public static string? Languages(string path) => ReleaseParser.TryParse(Path.GetFileNameWithoutExtension(path), out var parsed) ? string.Join('+', VideoReleaseJudge.Languages(parsed, "en").Languages) : null;
    }
}
