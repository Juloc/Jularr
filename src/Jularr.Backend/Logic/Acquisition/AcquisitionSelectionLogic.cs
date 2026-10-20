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

        public static QualityProfile ReadProfile(WantedTargetData target)
        {
            var definition = JsonSerializer.Deserialize<AcquisitionProfileDefinition>(target.Definition!, DefinitionOptions)
                ?? throw new InvalidOperationException("The acquisition profile is invalid.");
            var profile = new QualityProfile(target.AcquisitionProfileId!.Value.ToString(CultureInfo.InvariantCulture), target.ProfileName!, definition.AllowedQualities,
                definition.QualityOrder, definition.UpgradeAllowed, definition.UpgradeCutoffQuality, definition.MinimumSizeBytes, definition.MaximumSizeBytes)
            {
                LanguageOrder = target.RequestedLocales,
                IsMovie = target.MediaTypeId == MediaType.Movie,
                IsBook = target.MediaTypeId == MediaType.Book && target.MediaSubtypeId != MediaSubtype.Manga,
                Formats = Formats(target.Formats, definition.FormatScores),
                MinimumFormatScore = definition.MinimumFormatScore,
                CutoffFormatScore = definition.CutoffFormatScore,
                MinimumUpgradeFormatScore = definition.MinimumUpgradeFormatScore,
                QualityGroups = definition.QualityGroups,
                Sizes = (JsonSerializer.Deserialize<QualitySize[]>(target.QualitySizes) ?? []).ToDictionary(size => size.Quality, StringComparer.OrdinalIgnoreCase),
                RuntimeMinutes = target.RuntimeMinutes,
                ReleaseProfiles = JsonSerializer.Deserialize<ReleaseTerms[]>(target.ReleaseProfiles) ?? [],
                OriginalLanguage = target.OriginalLanguage,
                Year = target.ReleaseYear
            };
            if (ReleaseScorer.ValidateProfile(profile) is { Count: > 0 } errors)
            {
                throw new InvalidOperationException("The acquisition profile is invalid: " + string.Join("; ", errors));
            }

            return profile;
        }

        // The profile's scored formats; a format without a score plays no part.
        private static IReadOnlyList<(CustomFormat Format, int Score)> Formats(string formats, FormatScore[] scores)
        {
            var byId = (JsonSerializer.Deserialize<CustomFormat[]>(formats, DefinitionOptions) ?? []).ToDictionary(format => format.Id);
            return [.. scores.Select(score => (byId.GetValueOrDefault(score.FormatId) ?? throw new InvalidOperationException("A referenced custom format is missing."), score.Score))];
        }

        // The best present file by the profile's quality order, with the format score of its release name.
        public static (string Quality, int FormatScore, IReadOnlyList<string> Languages)? Installed(QualityProfile profile, WantedTargetData target, long? workUnitId = null)
        {
            var files = JsonSerializer.Deserialize<InstalledFile[]>(target.InstalledFiles) ?? [];
            var best = files.Where(file => file.WorkUnitId == workUnitId && UpgradePolicy.RankOf(profile, file.Quality) != int.MaxValue)
                .Select(file => (file.Quality, Candidate: StoredFileFormatLogic.Candidate(file.Facts)))
                .Select(file => (file.Quality, Score: file.Candidate is { } release ? ReleaseScorer.Score(profile, release).FormatScore : 0,
                    Languages: file.Candidate?.Languages ?? []))
                .OrderBy(file => UpgradePolicy.RankOf(profile, file.Quality)).ThenByDescending(file => file.Score)
                .ThenBy(file => UpgradePolicy.LanguageRank(profile, file.Languages)).Take(1).ToArray();
            return best.Length == 0 ? null : (best[0].Quality, best[0].Score, best[0].Languages);
        }

        private sealed record InstalledFile(string Quality, long? WorkUnitId, StoredReleaseEvidence Facts);

        // The quality a release is stored with.
        public static string? QualityKey(AcquisitionKind kind, string title, int? igdbPlatform = null) => kind switch
        {
            AcquisitionKind.Book or AcquisitionKind.LightNovel => ReleaseQuality.GetKey(BookReleaseJudge.Parse(title)),
            AcquisitionKind.Manga => ReleaseQuality.GetKey(MangaReleaseJudge.Parse(title)),
            AcquisitionKind.Music => ReleaseQuality.GetKey(MusicReleaseJudge.Parse(title)),
            AcquisitionKind.Game => ReleaseQuality.GetKey(GameReleaseJudge.Parse(title, igdbPlatform)),
            _ => ReleaseParser.TryParse(title, out var parsed) ? ReleaseQuality.GetKey(parsed, kind == AcquisitionKind.Movie) : null
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
                Aliases = [.. target.Titles, .. target.EditionTitles], Year = target.ReleaseYear, Creator = target.Author, ExternalIds = ids, Season = kind == AcquisitionKind.Manga ? null : scope?.ParentNumber,
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

        public static SelectionResult Select(QualityProfile profile, WantedTargetData target, AcquisitionKind kind, UnitScope? scope, IEnumerable<ReleaseOffer> offers, string? focusLanguage = null)
        {
            var installed = scope is null || scope.UnitNumber is not null ? Installed(profile, target, scope?.Unit.Id) : null;
            // Failed and blocked releases stay visible but are never taken.
            var blocked = target.ReleaseIdentities.ToHashSet(StringComparer.Ordinal);
            return ReleaseSelectionEngine.Select(profile, [.. offers.Select(offer => kind switch
            {
                AcquisitionKind.Book or AcquisitionKind.LightNovel => Candidate(offer, BookReleaseJudge.Judge([target.CanonicalTitle, .. target.EditionTitles], target.Author, offer,
                    target.OriginalLanguage)),
                AcquisitionKind.Manga => Candidate(offer, MangaReleaseJudge.Judge([target.CanonicalTitle, .. target.Titles], scope, offer)),
                AcquisitionKind.Music => Candidate(offer, MusicReleaseJudge.Judge([target.CanonicalTitle, .. target.Titles], target.Author, offer)),
                AcquisitionKind.Game => Candidate(offer, GameReleaseJudge.Judge([target.CanonicalTitle, .. target.Titles], target.PlatformIgdbId, target.PlatformAbbreviation, offer)),
                _ => Candidate(offer, VideoReleaseJudge.Judge(kind, target.CanonicalTitle, target.ReleaseYear, scope?.Unit, scope?.Units ?? UnitSet.Empty, offer))
            })]);

            SelectionCandidate Candidate<TMatch>(ReleaseOffer offer, ReleaseJudgement<TMatch> judgement) =>
                new(offer.Identity, judgement.Parsed, offer.SizeBytes, offer.ConnectionName, offer.ConnectionPriority, offer.PublishedAt, judgement.Evidence, judgement.Coverage)
                {
                    IndexerId = offer.ConnectionId,
                    IndexerFlags = offer.IndexerFlags,
                    IsPart = offer.IsPart,
                    SafetyRejection = blocked.Contains(offer.Identity) ? "Blocked" : judgement.SafetyRejection
                        ?? (EditionLanguageFilter.Rejects(judgement.Languages ?? judgement.Parsed?.AudioLanguages ?? [], target.MonitoredEditionLanguages, target.UnmonitoredEditionLanguages, focusLanguage) ? "EditionNotMonitored" : null),
                    ContextScore = judgement.ContextScore,
                    Languages = judgement.Languages ?? judgement.Parsed?.AudioLanguages ?? [],
                    InstalledQuality = installed?.Quality,
                    InstalledFormatScore = installed?.FormatScore,
                    InstalledLanguages = installed?.Languages
                };
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
            var wanted = units.Where(unit => group.Any(state => state.WorkUnitId == unit.Id && state.NeedsSearch)).ToArray();
            var target = unitNumber is { } number
                ? units.FirstOrDefault(unit => unit.Number == number) ?? new TargetUnit(0, parentNumber, number, false)
                : wanted.FirstOrDefault() ?? units.FirstOrDefault() ?? new TargetUnit(0, parentNumber, 1, false);
            return new UnitScope(parentNumber, unitNumber, target, new UnitSet(units, wanted.Length > 0 ? wanted : [target]));
        }

        public static UnitScope? ReadNextUnitScope(IReadOnlyList<UnitStateData> states, int? parentNumber, bool firstAttempt)
        {
            // A group with nothing yet (a fully aired season, a Manga without volumes) is searched as a pack, otherwise unit by unit.
            var group = states.Where(state => state.ParentNumber == parentNumber).ToArray();
            if (group.FirstOrDefault(state => state.NeedsSearch) is not { } next)
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
            return states.Select(state => state.NeedsSearch && state.ParentNumber == scope.ParentNumber && (parsed is null || pack || state.Number >= first && state.Number <= last)
                ? new UnitStateData(state.WorkUnitId, state.ParentNumber, state.Number, state.IsAvailable, state.HasMedia, true, state.AbsoluteNumber, state.ReleasedOn, state.NeedsUpgrade)
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
