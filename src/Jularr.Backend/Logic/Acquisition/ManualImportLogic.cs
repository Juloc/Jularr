using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Integrations.Data.Storage;

namespace Jularr.Backend.Logic.Acquisition
{
    // A video of a folder with what its name says it is and the name the library would give it.
    internal sealed record ManualImportProposal(string Path, long SizeBytes, int? Season, int? Episode, string? QualityKey, string? TargetName);

    // What a person sees and decides when a folder is imported by hand.
    internal static class ManualImportLogic
    {
        public static IReadOnlyList<ManualImportProposal> Propose(MediaType mediaType, MediaSubtype? subtype, string title, int? year, IReadOnlyList<StorageFile> files, NamingSet patterns,
            IReadOnlyList<TargetUnit> episodes)
        {
            var videos = files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) == MediaAssetType.Video && !MediaPathParser.IsSample(file.RelativePath)
                && !ImportJudgement.IsExtra(file.RelativePath)).OrderBy(file => ImportPlacementLogic.NaturalKey(file.RelativePath), StringComparer.OrdinalIgnoreCase).ToArray();
            var isMovie = mediaType == MediaType.Movie;
            var guesses = videos.Select(video =>
            {
                // A film needs no numbers; an episode takes the ones its name says.
                if (isMovie || !ReleaseParser.TryParse(Path.GetFileNameWithoutExtension(video.RelativePath), out var parsed) || EpisodeNumbering.Resolve(parsed, episodes) is not var (season, start, _))
                {
                    return (video.RelativePath, Season: (int?)null, Episode: (int?)null);
                }

                return (video.RelativePath, Season: (int?)season, Episode: (int?)start);
            }).ToArray();

            var named = ImportPlacementLogic.PlanChosen(mediaType, subtype, title, year, files, [.. guesses.Select(guess => (guess.RelativePath, guess.Season, guess.Episode))], patterns, episodes)
                .Where(placement => placement.IsMedia).ToDictionary(placement => placement.SourcePath, placement => placement.TargetStem + placement.Extension, StringComparer.Ordinal);
            return [.. videos.Zip(guesses, (video, guess) => new ManualImportProposal(video.RelativePath, video.SizeBytes, guess.Season, guess.Episode,
                ReleaseFacts.OfPath(video.RelativePath, isMovie)?.QualityKey, named.GetValueOrDefault(video.RelativePath)))];
        }
    }
}
