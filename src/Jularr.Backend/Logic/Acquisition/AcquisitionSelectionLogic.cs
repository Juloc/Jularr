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
        internal static readonly JsonSerializerOptions DefinitionOptions = new() { Converters = { new JsonStringEnumConverter() } };

        public static AcquisitionKind GetKind(MediaType mediaType, MediaSubtype? subtype) => (mediaType, subtype) switch
        {
            (MediaType.Movie, _) => AcquisitionKind.Movie,
            (MediaType.Series, MediaSubtype.Anime) => AcquisitionKind.Anime,
            (MediaType.Series, _) => AcquisitionKind.Tv,
            (MediaType.Book, MediaSubtype.Manga) => AcquisitionKind.Manga,
            (MediaType.Book, MediaSubtype.LightNovel) => AcquisitionKind.LightNovel,
            (MediaType.Book, _) => AcquisitionKind.Book,
            (MediaType.Music, _) => AcquisitionKind.Music,
            (MediaType.Game, _) => AcquisitionKind.Game,
            _ => throw new ArgumentOutOfRangeException(nameof(mediaType))
        };

        public static QualityProfile ReadProfile(WantedTargetData target, DateTime? now = null)
        {
            // Validated once per search; requested languages order releases of equal quality; fallback qualities join once the target has waited long enough.
            var definition = JsonSerializer.Deserialize<AcquisitionProfileDefinition>(target.Definition!, DefinitionOptions)
                ?? throw new InvalidOperationException("The acquisition profile is invalid.");
            var fallback = definition.FallbackAfterHours is { } hours && now is { } current && target.WantedSince.AddHours(hours) <= current ? definition.FallbackQualities : [];
            var allowed = definition.QualityOrder.Where(quality => definition.AllowedQualities.Contains(quality, StringComparer.OrdinalIgnoreCase)
                || fallback.Contains(quality, StringComparer.OrdinalIgnoreCase)).ToArray();
            var profile = new QualityProfile(target.AcquisitionProfileId!.Value.ToString(CultureInfo.InvariantCulture), target.ProfileName!, allowed,
                definition.QualityOrder, definition.UpgradeAllowed, definition.UpgradeCutoffQuality, definition.MinimumSizeBytes, definition.MaximumSizeBytes, definition.MustContain,
                definition.MustNotContain, definition.RequiredRegex, definition.RejectedRegex, definition.ScoreRules)
            {
                LanguageOrder = target.RequestedLocales,
                DelayMinutes = definition.DelayMinutes,
                FallbackQualities = fallback
            };
            if (ReleaseScorer.ValidateProfile(profile) is { Count: > 0 } errors)
            {
                throw new InvalidOperationException("The acquisition profile is invalid: " + string.Join("; ", errors));
            }

            return profile;
        }

        // The quality a release is stored with.
        public static string? QualityKey(AcquisitionKind kind, string title, int? igdbPlatform = null) => kind switch
        {
            AcquisitionKind.Book or AcquisitionKind.LightNovel => ReleaseQuality.GetKey(BookReleaseJudge.Parse(title)),
            AcquisitionKind.Manga => ReleaseQuality.GetKey(MangaReleaseJudge.Parse(title)),
            AcquisitionKind.Music => ReleaseQuality.GetKey(MusicReleaseJudge.Parse(title)),
            AcquisitionKind.Game => ReleaseQuality.GetKey(GameReleaseJudge.Parse(title, igdbPlatform)),
            _ => ReleaseParser.TryParse(title, out var parsed) ? ReleaseQuality.GetKey(parsed) : null
        };

        public static SearchIntent ReadIntent(WantedTargetData target, AcquisitionKind kind, UnitScope? scope)
        {
            // Provider mappings in the planner's key space.
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in target.ExternalIdentities.Select(identity => identity.Split(':', 2)).Where(pair => pair.Length == 2))
            {
                ids[pair[0].StartsWith("tmdb", StringComparison.Ordinal) ? "tmdb" : pair[0]] = pair[1];
            }

            return new SearchIntent(kind, target.CanonicalTitle)
            {
                Aliases = target.Titles, Year = target.ReleaseYear, Creator = target.Author, ExternalIds = ids, Season = kind == AcquisitionKind.Manga ? null : scope?.ParentNumber,
                Episode = kind == AcquisitionKind.Manga ? null : scope?.UnitNumber, Volume = kind == AcquisitionKind.Manga ? scope?.UnitNumber : null,
                AbsoluteEpisode = kind == AcquisitionKind.Anime && scope?.UnitNumber is not null ? scope.Unit.Absolute : null,
                AirDate = kind == AcquisitionKind.Tv && scope is { UnitNumber: not null, ParentNumber: >= 1900 } ? scope.Unit.ReleasedOn : null
            };
        }

        public static int[] ReadCategories(AcquisitionKind kind, int[] configured, NewznabCapabilities? capabilities, int? igdbPlatform = null)
        {
            // Configured categories count for their own Newznab range only; a game searches its platform's category.
            if (kind == AcquisitionKind.Game)
            {
                var category = GameReleaseJudge.Category(igdbPlatform);
                return new[] { category }.Where(value => capabilities?.Offers(value) ?? true).ToArray();
            }

            var range = kind switch
            {
                AcquisitionKind.Movie => 2000,
                AcquisitionKind.Tv or AcquisitionKind.Anime => 5000,
                AcquisitionKind.Book or AcquisitionKind.LightNovel or AcquisitionKind.Manga => 7000,
                AcquisitionKind.Audiobook or AcquisitionKind.Music => 3000,
                _ => 0
            };
            var own = configured.Where(category => category / 1000 * 1000 == range).ToArray();
            return (own.Length > 0 ? own : range == 0 ? [] : [range]).Where(category => capabilities?.Offers(category) ?? true).ToArray();
        }

        // Equal releases from several queries are one candidate with every stage that found it.
        public static ReleaseOffer Merge(ReleaseOffer existing, ReleaseOffer found) =>
            new(existing.Title, existing.ConnectionId, existing.ConnectionName, existing.ConnectionPriority, existing.Guid, existing.DownloadUri, existing.InfoUrl, existing.SizeBytes,
                existing.PublishedAt, [.. existing.Stages.Union(found.Stages)]);

        public static SelectionResult Select(QualityProfile profile, WantedTargetData target, AcquisitionKind kind, UnitScope? scope, IEnumerable<ReleaseOffer> offers)
        {
            // Failed and blocked releases stay visible but are never taken.
            var blocked = target.ReleaseIdentities.ToHashSet(StringComparer.Ordinal);
            return ReleaseSelectionEngine.Select(profile, [.. offers.Select(offer => kind switch
            {
                AcquisitionKind.Book or AcquisitionKind.LightNovel => Candidate(offer, BookReleaseJudge.Judge(target.CanonicalTitle, target.Author, offer)),
                AcquisitionKind.Manga => Candidate(offer, MangaReleaseJudge.Judge([target.CanonicalTitle, .. target.Titles], scope, offer)),
                AcquisitionKind.Music => Candidate(offer, MusicReleaseJudge.Judge([target.CanonicalTitle, .. target.Titles], target.Author, offer)),
                AcquisitionKind.Game => Candidate(offer, GameReleaseJudge.Judge([target.CanonicalTitle, .. target.Titles], target.PlatformIgdbId, target.PlatformAbbreviation, offer)),
                _ => Candidate(offer, VideoReleaseJudge.Judge(kind, target.CanonicalTitle, target.ReleaseYear, scope?.Unit, scope?.Units ?? UnitSet.Empty, offer))
            })]);

            SelectionCandidate Candidate<TMatch>(ReleaseOffer offer, ReleaseJudgement<TMatch> judgement) =>
                new(offer.Identity, judgement.Parsed, offer.SizeBytes, offer.ConnectionName, offer.ConnectionPriority, offer.PublishedAt, judgement.Evidence, judgement.Coverage)
                {
                    SafetyRejection = blocked.Contains(offer.Identity) ? "Blocked" : judgement.SafetyRejection,
                    ContextScore = judgement.ContextScore,
                    Languages = judgement.Languages ?? judgement.Parsed?.AudioLanguages ?? [],
                    InstalledQuality = scope is null && target.HasMedia ? target.InstalledQuality : null
                };
        }

        // An automatic grab waits until the winner is as old as the profile's delay, unless it has the best allowed quality.
        public static DateTime? DelayUntil(QualityProfile profile, CandidateEvaluation winner, DateTime now)
        {
            if (profile.DelayMinutes == 0 || winner.Candidate.PublishedAt is not { } published
                || string.Equals(winner.Score?.QualityKey, profile.QualityOrder.FirstOrDefault(quality => profile.AllowedQualities.Contains(quality, StringComparer.OrdinalIgnoreCase)),
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var until = published.UtcDateTime.AddMinutes(profile.DelayMinutes);
            return until > now ? until : null;
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

        public static UnitScope ReadUnitScope(IReadOnlyList<UnitStateData> states, int? parentNumber, int? unitNumber)
        {
            // Missing units of the group are wanted; the target is the asked or first missing one.
            var group = states.Where(state => state.ParentNumber == parentNumber).ToArray();
            var units = group.Select(state => new TargetUnit(state.WorkUnitId, state.ParentNumber, state.Number, state.HasMedia, state.AbsoluteNumber, state.ReleasedOn)).ToArray();
            var wanted = units.Where(unit => group.Any(state => state.WorkUnitId == unit.Id && state.IsMissing)).ToArray();
            var target = unitNumber is { } number
                ? units.FirstOrDefault(unit => unit.Number == number) ?? new TargetUnit(0, parentNumber, number, false)
                : wanted.FirstOrDefault() ?? units.FirstOrDefault() ?? new TargetUnit(0, parentNumber, 1, false);
            return new UnitScope(parentNumber, unitNumber, target, new UnitSet(units, wanted.Length > 0 ? wanted : [target]));
        }

        public static UnitScope? ReadNextUnitScope(IReadOnlyList<UnitStateData> states, int? parentNumber, bool firstAttempt)
        {
            // A group with nothing yet (a fully aired season, a Manga without volumes) is searched as a pack, otherwise unit by unit.
            var group = states.Where(state => state.ParentNumber == parentNumber).ToArray();
            if (group.FirstOrDefault(state => state.IsMissing) is not { } next)
            {
                return null;
            }

            return ReadUnitScope(states, parentNumber, firstAttempt && group.All(state => state.IsMissing) ? null : next.Number);
        }

        public static IReadOnlyList<UnitStateData> Claim(IReadOnlyList<UnitStateData> states, UnitScope scope, AcquisitionKind kind, ReleaseInfo? parsed)
        {
            // A grabbed release claims its group or unit range for the rest of the pass.
            var (start, end, pack) = ReadRange(kind, parsed);
            var first = start ?? scope.Unit.Number;
            var last = end ?? first;
            return states.Select(state => state.IsMissing && state.ParentNumber == scope.ParentNumber && (parsed is null || pack || state.Number >= first && state.Number <= last)
                ? new UnitStateData(state.WorkUnitId, state.ParentNumber, state.Number, state.IsAvailable, state.HasMedia, true, state.AbsoluteNumber, state.ReleasedOn)
                : state).ToArray();
        }

        public static (int? Parent, int? Start, int? End) ReadCoverage(AcquisitionKind kind, string title, IReadOnlyList<TargetUnit> episodes)
        {
            // Series releases claim a season or an episode range, Manga releases a volume range or the whole Work.
            if (kind == AcquisitionKind.Manga)
            {
                var (start, end, _) = ReadRange(kind, MangaReleaseJudge.Parse(title));
                return (null, start, end);
            }

            if (kind is not (AcquisitionKind.Tv or AcquisitionKind.Anime) || !ReleaseParser.TryParse(title, out var parsed))
            {
                return (null, null, null);
            }

            // A season pack claims its season; absolute and dated releases claim the episodes they map to.
            if (parsed.IsSeasonPack && parsed.SeasonNumber is { } packed)
            {
                return (packed, null, null);
            }

            return EpisodeNumbering.Resolve(parsed, episodes) is var (season, first, last) ? (season, first, last) : (parsed.SeasonNumber, null, null);
        }

        private static (int? Start, int? End, bool Pack) ReadRange(AcquisitionKind kind, ReleaseInfo? parsed) => kind == AcquisitionKind.Manga
            ? (parsed?.VolumeStart, parsed?.VolumeStart is { } volume ? Math.Max(volume, parsed.VolumeEnd ?? volume) : null, parsed?.VolumeStart is null)
            : (parsed?.EpisodeStart, parsed?.EpisodeStart is { } episode ? Math.Max(episode, parsed.EpisodeEnd ?? episode) : null, parsed?.IsSeasonPack == true);
    }
}
