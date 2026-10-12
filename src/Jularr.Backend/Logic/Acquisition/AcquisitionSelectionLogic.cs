using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Integrations.Data.Newznab;

namespace Jularr.Backend.Logic.Acquisition
{
    // Rules of one acquisition decision: what is searched, how releases rank and what a grab claims.
    internal static class AcquisitionSelectionLogic
    {
        private static readonly JsonSerializerOptions DefinitionOptions = new() { Converters = { new JsonStringEnumConverter() } };

        public static AcquisitionKind? GetKind(MediaType mediaType) => mediaType switch
        {
            MediaType.Movie => AcquisitionKind.Movie,
            MediaType.Series => AcquisitionKind.Tv,
            _ => null
        };

        public static QualityProfile ReadProfile(WantedTargetData target)
        {
            // Validated once per search; requested languages order releases of equal quality.
            var definition = JsonSerializer.Deserialize<AcquisitionProfileDefinition>(target.Definition!, DefinitionOptions)
                ?? throw new InvalidOperationException("The acquisition profile is invalid.");
            var profile = new QualityProfile(target.AcquisitionProfileId!.Value.ToString(CultureInfo.InvariantCulture), target.ProfileName!, definition.AllowedQualities,
                definition.QualityOrder, definition.UpgradeAllowed, definition.UpgradeCutoffQuality, definition.MinimumSizeBytes, definition.MaximumSizeBytes, definition.MustContain,
                definition.MustNotContain, definition.RequiredRegex, definition.RejectedRegex, definition.ScoreRules) { LanguageOrder = target.RequestedLocales };
            if (ReleaseScorer.ValidateProfile(profile) is { Count: > 0 } errors)
            {
                throw new InvalidOperationException("The acquisition profile is invalid: " + string.Join("; ", errors));
            }

            return profile;
        }

        public static SearchIntent ReadIntent(WantedTargetData target, AcquisitionKind kind, EpisodeScope? episodes)
        {
            // Provider mappings in the planner's key space.
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in target.ExternalIdentities.Select(identity => identity.Split(':', 2)).Where(pair => pair.Length == 2))
            {
                ids[pair[0].StartsWith("tmdb", StringComparison.Ordinal) ? "tmdb" : pair[0]] = pair[1];
            }

            return new SearchIntent(kind, target.CanonicalTitle) { Aliases = target.Titles, Year = target.ReleaseYear, ExternalIds = ids, Season = episodes?.SeasonNumber, Episode = episodes?.EpisodeNumber };
        }

        public static int[] ReadCategories(AcquisitionKind kind, int[] configured, NewznabCapabilities? capabilities) =>
            (configured.Length > 0 ? configured : kind switch { AcquisitionKind.Movie => [2000], AcquisitionKind.Tv or AcquisitionKind.Anime => [5000], _ => [] })
                .Where(category => capabilities?.Offers(category) ?? true).ToArray();

        // Equal releases from several queries are one candidate with every stage that found it.
        public static ReleaseOffer Merge(ReleaseOffer existing, ReleaseOffer found) =>
            new(existing.Title, existing.ConnectionId, existing.ConnectionName, existing.ConnectionPriority, existing.Guid, existing.DownloadUri, existing.InfoUrl, existing.SizeBytes,
                existing.PublishedAt, [.. existing.Stages.Union(found.Stages)]);

        public static SelectionResult Select(QualityProfile profile, WantedTargetData target, AcquisitionKind kind, EpisodeScope? episodes, IEnumerable<ReleaseOffer> offers)
        {
            // Failed and blocked releases stay visible but are never taken.
            var blocked = target.ReleaseIdentities.ToHashSet(StringComparer.Ordinal);
            return ReleaseSelectionEngine.Select(profile, [.. offers.Select(offer =>
            {
                var judgement = VideoReleaseJudge.Judge(kind, target.CanonicalTitle, target.ReleaseYear, episodes?.Unit, episodes?.Units ?? VideoUnitScope.Empty, offer);
                return new SelectionCandidate(offer.Identity, judgement.Parsed, offer.SizeBytes, offer.ConnectionName, offer.ConnectionPriority, offer.PublishedAt, judgement.Evidence, judgement.Coverage)
                {
                    SafetyRejection = blocked.Contains(offer.Identity) ? "Blocked" : judgement.SafetyRejection,
                    ContextScore = judgement.ContextScore,
                    Languages = judgement.Languages ?? judgement.Parsed?.AudioLanguages ?? []
                };
            })]);
        }

        // The outcome a target remembers for the next automatic search.
        public static string OutcomeKey(SelectionOutcome outcome, bool indexersUnavailable) => indexersUnavailable ? "indexers_unavailable" : outcome switch
        {
            SelectionOutcome.Usable => "release_found",
            SelectionOutcome.ManualReviewOnly => "manual_review",
            SelectionOutcome.ProfileRejected => "profile_rejected",
            SelectionOutcome.IdentityInvalid => "identity_invalid",
            _ => "no_candidates"
        };

        public static EpisodeScope ReadEpisodeScope(IReadOnlyList<EpisodeStateData> episodes, int seasonNumber, int? episodeNumber)
        {
            // Missing episodes of the season are wanted; the target is the asked or first missing one.
            var season = episodes.Where(episode => episode.SeasonNumber == seasonNumber).ToArray();
            var units = season.Select(episode => new VideoUnit(episode.WorkUnitId, episode.SeasonNumber, episode.EpisodeNumber, episode.HasMedia)).ToArray();
            var wanted = units.Where(unit => season.Any(episode => episode.WorkUnitId == unit.Id && episode.IsMissing)).ToArray();
            var target = episodeNumber is { } number
                ? units.FirstOrDefault(unit => unit.EpisodeNumber == number) ?? new VideoUnit(0, seasonNumber, number, false)
                : wanted.FirstOrDefault() ?? units.FirstOrDefault() ?? new VideoUnit(0, seasonNumber, 1, false);
            return new EpisodeScope(seasonNumber, episodeNumber, target, new VideoUnitScope(units, wanted.Length > 0 ? wanted : [target]));
        }

        public static EpisodeScope? ReadNextSeriesScope(IReadOnlyList<EpisodeStateData> episodes, int seasonNumber, bool firstAttempt)
        {
            // A fully aired season with nothing yet is searched as a pack, otherwise episode by episode.
            var season = episodes.Where(episode => episode.SeasonNumber == seasonNumber).ToArray();
            if (season.FirstOrDefault(episode => episode.IsMissing) is not { } next)
            {
                return null;
            }

            return ReadEpisodeScope(episodes, seasonNumber, firstAttempt && season.All(episode => episode.IsMissing) ? null : next.EpisodeNumber);
        }

        public static IReadOnlyList<EpisodeStateData> Claim(IReadOnlyList<EpisodeStateData> episodes, EpisodeScope scope, ReleaseInfo? parsed)
        {
            // A grabbed release claims its season or episode range for the rest of the pass.
            var start = parsed?.EpisodeStart ?? scope.Unit.EpisodeNumber;
            var end = parsed?.EpisodeEnd ?? start;
            return episodes.Select(episode => episode.IsMissing && episode.SeasonNumber == scope.SeasonNumber
                && (parsed is null || parsed.IsSeasonPack || episode.EpisodeNumber >= start && episode.EpisodeNumber <= end)
                    ? new EpisodeStateData(episode.WorkUnitId, episode.SeasonNumber, episode.EpisodeNumber, episode.IsAired, episode.HasMedia, true)
                    : episode).ToArray();
        }

        public static (int? Season, int? Start, int? End) ReadCoverage(MediaType mediaType, string title)
        {
            // Series releases claim a season or an episode range.
            if (mediaType != MediaType.Series || !ReleaseParser.TryParse(title, out var parsed) || parsed.SeasonNumber is not { } season)
            {
                return (null, null, null);
            }

            return parsed.IsSeasonPack || parsed.EpisodeStart is not { } first ? (season, null, null) : (season, first, Math.Max(first, parsed.EpisodeEnd ?? first));
        }
    }
}
