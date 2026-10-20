using System.Text.Json;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Integrations.Data.Storage;

namespace Jularr.Backend.Logic.Acquisition
{
    internal enum ImportVerdictKind : byte
    {
        // Every file is placed by itself.
        Clear = 1,
        // The download waits for a person, with its reason and where Jularr would place the files.
        Doubt = 2,
        // The release is no use: it is excluded and the next best is searched at once.
        Discard = 3
    }

    // A file of a finished download, where its name says it belongs and where the grab wanted it.
    internal sealed record ImportFileProposal(string Path, int? Season, int? Episode, int? WantedSeason, int? WantedEpisode);

    internal sealed record ImportVerdict(ImportVerdictKind Kind, string? ReasonKey = null, IReadOnlyList<ImportFileProposal>? Proposal = null)
    {
        public static ImportVerdict Clear { get; } = new(ImportVerdictKind.Clear);

        public string? ProposalJson => Kind == ImportVerdictKind.Doubt ? JsonSerializer.Serialize(Proposal ?? []) : null;
    }

    // Decides what a finished film or episode download is: clear enough to import, a doubt for a person, or no use.
    internal static class ImportJudgement
    {
        public const string NoMedia = "import_no_media";
        public const string OnlyExtras = "import_only_extras";
        public const string WrongSeries = "import_wrong_series";
        public const string WrongTitle = "import_wrong_title";
        public const string WrongEpisode = "import_wrong_episode";
        public const string AmbiguousTitle = "import_ambiguous_title";
        public const string UnreadableNames = "import_unreadable_names";
        public const string QualityNotAllowed = "import_quality_not_allowed";

        private static readonly string[] ExtraFolders = ["extras", "featurettes", "featurette", "behind the scenes", "deleted scenes", "trailers", "bonus", "interviews", "shorts", "scenes", "other"];

        public static ImportVerdict Judge(MediaType mediaType, MediaSubtype? subtype, string title, int? year, string releaseTitle, int? expectedSeason, int? expectedStart, int? expectedEnd,
            IReadOnlyList<StorageFile> files, IReadOnlyList<TargetUnit> units, QualityProfile? profile)
        {
            // Other media types keep their own rules.
            if (mediaType is not (MediaType.Movie or MediaType.Series))
            {
                return ImportVerdict.Clear;
            }

            var media = files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) == MediaAssetType.Video).ToArray();
            if (media.Length == 0)
            {
                return new ImportVerdict(ImportVerdictKind.Discard, NoMedia);
            }

            var videos = media.Where(file => !MediaPathParser.IsSample(file.RelativePath) && !IsExtra(file.RelativePath)).ToArray();
            if (videos.Length == 0)
            {
                return new ImportVerdict(ImportVerdictKind.Discard, OnlyExtras);
            }

            return mediaType == MediaType.Movie
                ? JudgeMovie(title, year, videos, profile)
                : JudgeSeries(subtype == MediaSubtype.Anime ? AcquisitionKind.Anime : AcquisitionKind.Tv, title, releaseTitle, expectedSeason, expectedStart, expectedEnd, videos, units, profile);
        }

        // A file in a folder of extras, or named as one, is no episode or feature.
        public static bool IsExtra(string path)
        {
            var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts[..^1].Any(part => ExtraFolders.Contains(part.Trim().ToLowerInvariant()))
                || Path.GetFileNameWithoutExtension(parts[^1]) is var name && (name.EndsWith("-trailer", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".trailer", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("-featurette", StringComparison.OrdinalIgnoreCase));
        }

        private static ImportVerdict JudgeMovie(string title, int? year, StorageFile[] videos, QualityProfile? profile)
        {
            // The largest video is the feature, as the placement takes it.
            var feature = videos.MaxBy(video => video.SizeBytes)!;
            var stem = Path.GetFileNameWithoutExtension(feature.RelativePath);
            var parsed = Parse(stem);
            if (parsed is not null && HasEvidence(parsed))
            {
                switch (VideoReleaseJudge.JudgeTitle(AcquisitionKind.Movie, title, year, parsed))
                {
                    case VideoIdentityMatch.WrongTitle:
                        return new ImportVerdict(ImportVerdictKind.Discard, WrongTitle);
                    case VideoIdentityMatch.WrongYear or VideoIdentityMatch.AmbiguousTitle:
                        return new ImportVerdict(ImportVerdictKind.Doubt, AmbiguousTitle, [new ImportFileProposal(feature.RelativePath, null, null, null, null)]);
                }
            }

            return IsQualityNotAllowed(profile, parsed)
                ? new ImportVerdict(ImportVerdictKind.Doubt, QualityNotAllowed, [new ImportFileProposal(feature.RelativePath, null, null, null, null)])
                : ImportVerdict.Clear;
        }

        private static ImportVerdict JudgeSeries(AcquisitionKind kind, string title, string releaseTitle, int? expectedSeason, int? expectedStart, int? expectedEnd, StorageFile[] videos,
            IReadOnlyList<TargetUnit> units, QualityProfile? profile)
        {
            var candidates = videos.Select(video =>
            {
                var stem = Path.GetFileNameWithoutExtension(video.RelativePath);
                var parsed = Parse(stem);

                // A name of another series is no episode of this one, but a name without evidence of its own (a bare number) is not against it.
                var conflicts = parsed is not null && HasEvidence(parsed) && parsed.SeriesTitle.Length > 0 && !TitleMatcher.Matches(title, parsed.SeriesTitle);
                return (Video: video, Parsed: parsed, Conflicts: conflicts, Mapped: parsed is null ? null : EpisodeNumbering.Resolve(parsed, units));
            }).ToArray();

            var fitting = candidates.Where(candidate => !candidate.Conflicts).ToArray();
            if (fitting.Length == 0)
            {
                return new ImportVerdict(ImportVerdictKind.Discard, WrongSeries);
            }

            var expected = units.Where(unit => unit.ParentNumber is not null && (expectedSeason is null || unit.ParentNumber == expectedSeason)
                && (expectedStart is null || expectedEnd is null || unit.Number >= expectedStart && unit.Number <= expectedEnd)).OrderBy(unit => unit.ParentNumber).ThenBy(unit => unit.Number).ToArray();
            var only = fitting.Length == 1 && expected.Length == 1 ? expected[0] : null;
            var mapped = fitting.Where(candidate => candidate.Mapped is not null).ToArray();
            if (mapped.Length > 0)
            {
                // A file for an episode somebody wanted makes the download what was grabbed; every other episode of the pack comes along.
                var hitsExpected = expectedSeason is null || mapped.Any(candidate => candidate.Mapped!.Value.Season == expectedSeason
                    && (expectedStart is null || expectedEnd is null || candidate.Mapped.Value.End >= expectedStart && candidate.Mapped.Value.Start <= expectedEnd));
                if (!hitsExpected)
                {
                    return new ImportVerdict(ImportVerdictKind.Doubt, WrongEpisode, [.. mapped.Select(candidate => new ImportFileProposal(candidate.Video.RelativePath,
                        candidate.Mapped!.Value.Season, candidate.Mapped.Value.Start, only?.ParentNumber, only?.Number))]);
                }

                return mapped.Any(candidate => IsQualityNotAllowed(profile, candidate.Parsed))
                    ? new ImportVerdict(ImportVerdictKind.Doubt, QualityNotAllowed, [.. mapped.Select(candidate => new ImportFileProposal(candidate.Video.RelativePath,
                        candidate.Mapped!.Value.Season, candidate.Mapped.Value.Start, null, null))])
                    : ImportVerdict.Clear;
            }

            // Files whose names say nothing: as many files as episodes wanted can follow each other, in the order of their names.
            var ordered = fitting.OrderBy(candidate => ImportPlacementLogic.NaturalKey(candidate.Video.RelativePath), StringComparer.OrdinalIgnoreCase).ToArray();
            var matching = ordered.Length == expected.Length;
            return new ImportVerdict(ImportVerdictKind.Doubt, UnreadableNames, [.. ordered.Select((candidate, index) => new ImportFileProposal(candidate.Video.RelativePath,
                matching ? expected[index].ParentNumber : null, matching ? expected[index].Number : null, matching ? expected[index].ParentNumber : null, matching ? expected[index].Number : null))]);
        }

        private static ReleaseInfo? Parse(string stem) => ReleaseParser.TryParse(stem, out var parsed) ? parsed : null;

        // A name that carries release facts says something about what it is; a bare word does not.
        private static bool HasEvidence(ReleaseInfo parsed) =>
            parsed.SeasonNumber is not null || parsed.AbsoluteEpisodeStart is not null || parsed.AirDate is not null || parsed.Resolution is not null || parsed.ReleaseGroup is not null;

        // The profile's own reading of a quality: the same key, its group and the allowed groups as when the release was chosen.
        private static bool IsQualityNotAllowed(QualityProfile? profile, ReleaseInfo? parsed) =>
            profile is not null && parsed is not null && profile.AllowedQualities.Length > 0
                && !profile.AllowedQualities.Contains(profile.Item(ReleaseQuality.GetKey(parsed, profile.IsMovie)), StringComparer.OrdinalIgnoreCase);
    }
}
