using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Integrations.Data.Storage;

namespace Jularr.Backend.Logic.Acquisition
{
    // A file of a folder with what its name says it is and the name the library would give it. Of a series Season and Episode are the episode's numbers; of a book Season is the
    // volume and Episode a manga's chapter (Season then its volume), and neither is the whole Work.
    internal sealed record ManualImportProposal(string Path, long SizeBytes, int? Season, int? Episode, string? QualityKey, string? TargetName, string? Languages);

    // What a person sees and decides when a folder is imported by hand.
    internal static class ManualImportLogic
    {
        // The files a person can import: the videos of a film or series (no samples or extras), the ebooks and comics of a book, in the order of their names.
        public static StorageFile[] Importable(MediaType mediaType, IEnumerable<StorageFile> files) => [.. files.Where(file => mediaType == MediaType.Book
                ? LibraryFileTypes.GetAssetType(file.RelativePath) is MediaAssetType.Ebook or MediaAssetType.Comic
                : LibraryFileTypes.GetAssetType(file.RelativePath) == MediaAssetType.Video && !MediaPathParser.IsSample(file.RelativePath) && !ImportJudgement.IsExtra(file.RelativePath))
            .OrderBy(file => ImportPlacementLogic.NaturalKey(file.RelativePath), StringComparer.OrdinalIgnoreCase)];

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
                // A film needs no numbers; an episode takes the ones its name says.
                if (isMovie || !ReleaseParser.TryParse(Path.GetFileNameWithoutExtension(video.RelativePath), out var parsed) || EpisodeNumbering.Resolve(parsed, units) is not var (season, start, _))
                {
                    return (video.RelativePath, Season: (int?)null, Episode: (int?)null);
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
        private static string? Languages(string path) => ReleaseParser.TryParse(Path.GetFileNameWithoutExtension(path), out var parsed) ? string.Join('+', VideoReleaseJudge.Languages(parsed, "en").Languages) : null;
    }
}
