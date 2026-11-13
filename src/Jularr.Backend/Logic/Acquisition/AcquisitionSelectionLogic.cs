using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Data.Newznab;

namespace Jularr.Backend.Logic.Acquisition
{
    // Rules of one acquisition decision: what is searched, how releases rank and what a grab claims.
    internal static class AcquisitionSelectionLogic
    {
        internal static readonly JsonSerializerOptions DefinitionOptions = new() { Converters = { new JsonStringEnumConverter() } };
        private static readonly ConditionalWeakTable<WantedTargetData, Dictionary<long, InstalledFile[]>> InstalledEvidence = new();

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
            var chain = target.MediaTypeId is MediaType.Book or MediaType.Movie or MediaType.Series ? Chain(target) : ([], 0, true);
            var profile = new QualityProfile(target.AcquisitionProfileId!.Value.ToString(CultureInfo.InvariantCulture), target.ProfileName!, definition.AllowedQualities,
                definition.QualityOrder, definition.UpgradeAllowed, definition.UpgradeCutoffQuality, definition.MinimumSizeBytes, definition.MaximumSizeBytes)
            {
                // A book is wanted once per language, each with its chain (first, second, the original): only the chains of the languages no file covers yet are asked for.
                LanguageOrder = chain.Order.Length > 0 ? chain.Order : definition.LanguageOrder.Length > 0 ? definition.LanguageOrder : target.RequestedLocales,
                LanguageFallback = chain.Order.Length > 0 ? chain.UsesOriginal ? LanguageFallbackType.Original : LanguageFallbackType.None : definition.LanguageFallback,
                FirstLanguages = chain.Order.Length > 0 ? chain.Firsts : null,
                WantedLanguages = target.WantedLanguages,
                IsVideo = target.MediaTypeId is MediaType.Movie or MediaType.Series,
                IsManga = target.MediaTypeId == MediaType.Book && target.MediaSubtypeId == MediaSubtype.Manga,
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
                Year = target.ReleaseYear,
                GroupRatings = new Dictionary<string, ReleaseGroupRating>(JsonSerializer.Deserialize<Dictionary<string, ReleaseGroupRating>>(target.ReleaseGroupRatings) ?? new Dictionary<string, ReleaseGroupRating>(),
                    StringComparer.OrdinalIgnoreCase),
                GoodGroupScore = definition.GoodGroupScore,
                BadGroupScore = definition.BadGroupScore,
                BlockBadSources = definition.BlockBadSources,
                CodecPreference = definition.CodecPreference,
                SizePreference = definition.SizePreference
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
        public static (string Quality, int FormatScore, IReadOnlyList<string> Languages, bool NeedsReplacement)? Installed(QualityProfile profile, WantedTargetData target, long? workUnitId = null)
        {
            return ReadInstalledEvidence(target).TryGetValue(workUnitId ?? 0, out var files) ? Installed(profile, files) : null;
        }

        private static (string Quality, int FormatScore, IReadOnlyList<string> Languages, bool NeedsReplacement)? Installed(QualityProfile profile, IEnumerable<InstalledFile> files)
        {
            var best = files
                .Select(file => (file.Quality, file.RuntimeMinutes, file.Facts, Candidate: StoredFileFormatLogic.Candidate(file.Facts)))
                .Select(file => (file.Quality, file.Facts, Score: file.Candidate is { } release ? ReleaseScorer.Score(profile,
                    new ReleaseCandidate(release.Release, release.SizeBytes) { IndexerFlags = release.IndexerFlags, Languages = release.Languages, RuntimeMinutes = file.RuntimeMinutes is > 0 ? file.RuntimeMinutes : null }) : null,
                    Languages: StoredFileFormatLogic.AudioLanguages(file.Facts, file.Candidate)))
                .Select(file => (file.Quality, Score: file.Score?.FormatScore ?? 0, file.Languages,
                    NeedsReplacement: profile.UpgradeAllowed && file.Score is { Accepted: false, QualityKey: not null and not "Unknown" }))
                .OrderBy(file => file.NeedsReplacement).ThenBy(file => UpgradePolicy.RankOf(profile, file.Quality)).ThenByDescending(file => file.Score)
                .ThenBy(file => UpgradePolicy.LanguageRank(profile, file.Languages)).Take(1).ToArray();
            return best.Length == 0 ? null : (best[0].Quality, best[0].Score, best[0].Languages, best[0].NeedsReplacement);
        }

        private sealed record InstalledFile(string Quality, long? WorkUnitId, StoredReleaseEvidence Facts, decimal? RuntimeMinutes = null);

        // A target is an immutable database snapshot; keep only its parsed evidence, never a cached policy decision.
        private static Dictionary<long, InstalledFile[]> ReadInstalledEvidence(WantedTargetData target) => InstalledEvidence.GetValue(target,
            snapshot => (JsonSerializer.Deserialize<InstalledFile[]>(snapshot.InstalledFiles) ?? []).GroupBy(file => file.WorkUnitId ?? 0).ToDictionary(group => group.Key, group => group.ToArray()));

        // Re-evaluate every covered unit once, using current profiles rather than a stale stored score.
        public static IReadOnlyList<UnitStateData> ReassessUnits(QualityProfile profile, WantedTargetData target, IReadOnlyList<UnitStateData> units)
        {
            var files = ReadInstalledEvidence(target).Where(group => group.Key != 0).ToDictionary(group => group.Key, group => Installed(profile, group.Value));
            return units.Select(unit => new UnitStateData(unit.WorkUnitId, unit.ParentNumber, unit.Number, unit.IsAvailable, unit.HasMedia, unit.IsCovered,
                unit.AbsoluteNumber, unit.ReleasedOn, files.TryGetValue(unit.WorkUnitId, out var installed) && installed is { } present
                    ? NeedsUpgrade(profile, present) : unit.NeedsUpgrade, unit.Title)).ToArray();
        }

        public static bool NeedsUpgrade(QualityProfile profile, (string Quality, int FormatScore, IReadOnlyList<string> Languages, bool NeedsReplacement) installed) =>
            profile.UpgradeAllowed && (installed.NeedsReplacement || UpgradePolicy.RankOf(profile, installed.Quality) != int.MaxValue
                && (!UpgradePolicy.IsCutoffMet(profile, installed.Quality) || installed.FormatScore < profile.CutoffFormatScore)
                || UpgradePolicy.FirstLanguageOpen(profile, installed.Languages));

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
                existing.PublishedAt, [.. existing.Stages.Union(found.Stages)])
            {
                Transport = existing.Transport == DownloadTransport.Torrent || found.Transport == DownloadTransport.Torrent
                    ? DownloadTransport.Torrent : DownloadTransport.Usenet,
                IndexerFlags = [.. existing.IndexerFlags.Union(found.IndexerFlags)],
                IsPart = existing.IsPart || found.IsPart
            };

        // An automatic choice rejects what a manual search only marks for a person (a book's size outside its limits).
        public static SelectionResult Select(QualityProfile profile, WantedTargetData target, AcquisitionKind kind, UnitScope? scope, IEnumerable<ReleaseOffer> offers, string? focusLanguage = null,
            bool automatic = true)
        {
            // A language no file covers is not an upgrade of the files in the others.
            var installed = target.MissingLanguages.Length == 0 && (scope is null || scope.UnitNumber is not null) ? Installed(profile, target, scope?.Unit.Id) : null;
            var languageRules = JsonSerializer.Deserialize<LanguageRule[]>(target.LanguageRules) ?? [];

            // Failed and blocked releases stay visible but are never taken.
            var blocked = target.ReleaseIdentities.ToHashSet(StringComparer.Ordinal);

            // The release names of the files present for this unit; one of them, or an imported release of the target, is the present file itself.
            var presentNames = installed is null ? [] : ReadInstalledEvidence(target).GetValueOrDefault(scope?.Unit.Id ?? 0)?.Select(file => file.Facts.ReleaseName).ToHashSet(StringComparer.Ordinal) ?? [];
            return ReleaseSelectionEngine.Select(profile, [.. offers.Select(offer => kind switch
            {
                AcquisitionKind.Book or AcquisitionKind.LightNovel => Candidate(offer, BookReleaseJudge.Judge([target.CanonicalTitle, .. target.EditionTitles], target.Author, offer,
                    target.OriginalLanguage, new BookSearch(kind == AcquisitionKind.LightNovel, profile.Sizes, automatic) { LanguageTitles = ReadLanguageTitles(target) })),
                AcquisitionKind.Manga => Candidate(offer, MangaReleaseJudge.Judge([target.CanonicalTitle, .. target.Titles], scope, offer, automatic)),
                AcquisitionKind.Music => Candidate(offer, MusicReleaseJudge.Judge([target.CanonicalTitle, .. target.Titles], target.Author, offer)),
                AcquisitionKind.Game => Candidate(offer, GameReleaseJudge.Judge([target.CanonicalTitle, .. target.Titles], target.PlatformIgdbId, target.PlatformAbbreviation, offer)),
                _ => Candidate(offer, VideoReleaseJudge.Judge(kind, [target.CanonicalTitle, .. target.Titles], target.ReleaseYear, scope, offer, automatic, target.OriginalLanguage))
            })]);

            SelectionCandidate Candidate<TMatch>(ReleaseOffer offer, ReleaseJudgement<TMatch> judgement)
            {
                // A release of a blocked group is never taken automatically; a manual search only marks it.
                var blockedGroup = judgement.Parsed is { } parsed && ReleaseScorer.GroupRatings(profile, parsed).Any(rated => rated.Rating == ReleaseGroupRating.Blocked)
                    ? new SelectionReason(SelectionReasonKind.Profile, "GroupBlocked") : null;
                var ruled = LanguageRuleLogic.Apply(languageRules, judgement.Languages ?? judgement.Parsed?.AudioLanguages ?? [], judgement.Parsed, offer.Title);
                return new(offer.Identity, judgement.Parsed, offer.SizeBytes, offer.ConnectionName, offer.ConnectionPriority, offer.PublishedAt, judgement.Evidence, judgement.Coverage)
                {
                    IndexerId = offer.ConnectionId,
                    IndexerFlags = offer.IndexerFlags,
                    IsPart = offer.IsPart,
                    SafetyRejection = blocked.Contains(offer.Identity) ? "Blocked" : judgement.SafetyRejection
                        ?? (EditionLanguageFilter.Rejects(judgement.Languages ?? judgement.Parsed?.AudioLanguages ?? [], target.MonitoredEditionLanguages, target.UnmonitoredEditionLanguages, focusLanguage) ? "EditionNotMonitored" : null),
                    ContextPoints = [.. judgement.Points, .. ruled.Points],
                    Rejection = judgement.Rejection ?? (automatic ? blockedGroup ?? ruled.Blocked : null),
                    Flag = automatic ? judgement.Flag : blockedGroup ?? ruled.Blocked ?? judgement.Flag,
                    SizeMark = judgement.SizeMark,
                    Languages = judgement.Languages ?? judgement.Parsed?.AudioLanguages ?? [],
                    LanguagesFromTitle = judgement.LanguagesFromTitle,
                    LanguagesAssumedOriginal = judgement.LanguagesAssumedOriginal,
                    SubtitleLanguages = judgement.SubtitleLanguages,
                    SubtitlesInChain = judgement.SubtitleLanguages.Any(language => language == "*" || profile.LanguageOrder.Contains(language, StringComparer.OrdinalIgnoreCase)),
                    InstalledQuality = installed?.Quality,
                    InstalledNeedsReplacement = installed?.NeedsReplacement ?? false,
                    IsInstalledRelease = installed is not null && (presentNames.Contains(TextLimit.Cut(offer.Title, 500)!) || target.ImportedReleaseIdentities.Contains(offer.Identity, StringComparer.Ordinal)),
                    RuntimeMinutes = judgement.Parsed?.IsSeasonPack == true && scope is { Units.All.Count: > 0 } && profile.RuntimeMinutes is { } minutes
                        ? (decimal)minutes * scope.Units.All.Count : null,
                    InstalledFormatScore = installed?.FormatScore,
                    InstalledLanguages = installed?.Languages
                };
            }
        }

        // The chains of a target as languages: "first|second|original" entries.
        public static (string First, string? Second, bool UsesOriginal)[] Chains(WantedTargetData target) =>
            [.. target.LanguageChains.Select(entry => entry.Split('|')).Where(parts => parts.Length == 3 && parts[0].Length > 0)
                .Select(parts => (parts[0], parts[1].Length > 0 ? parts[1] : null, parts[2] == "1"))];

        // The languages a search asks for in order, and whether the original is allowed: the first and the second language of every chain whose first language is still missing
        // (every chain once a file covers them all); empty for a target without chains.
        public static (string[] Order, int Firsts, bool UsesOriginal) Chain(WantedTargetData target)
        {
            var chains = Chains(target);
            var open = target.MissingLanguages.Length == 0 ? chains : [.. chains.Where(item => target.MissingLanguages.Contains(item.First, StringComparer.OrdinalIgnoreCase))];
            var firsts = open.Select(item => item.First).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var seconds = open.Select(item => item.Second).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Where(second => !firsts.Contains(second, StringComparer.OrdinalIgnoreCase));
            return ([.. firsts, .. seconds], firsts.Length, open.Any(item => item.UsesOriginal));
        }

        // The language a grab of a book is for: the first missing language the release carries, else the first missing one when the release names none; no language for other media
        // and for a release in a language nobody is missing.
        public static string? TargetLanguage(WantedTargetData target, IReadOnlyList<string> languages)
        {
            if (target.MissingLanguages.Length == 0)
            {
                return null;
            }

            // The release is for the missing language it carries; one in the second language or in the original language of a chain stands in for that chain's first language
            // until a release in it comes (the first language of a chain comes before the others, in the order of the missing languages).
            var named = languages.Select(language => LanguageCodes.ToTwoLetter(language) ?? language.ToLowerInvariant()).ToArray();
            if (target.MissingLanguages.FirstOrDefault(missing => named.Contains(missing, StringComparer.OrdinalIgnoreCase)) is { } direct)
            {
                return direct;
            }

            var chains = Chains(target);
            var standIn = target.MissingLanguages.FirstOrDefault(missing => chains.Any(item => string.Equals(item.First, missing, StringComparison.OrdinalIgnoreCase)
                && (item.Second is { } second && named.Contains(second, StringComparer.OrdinalIgnoreCase)
                    || item.UsesOriginal && target.OriginalLanguage is { } original && named.Contains(original, StringComparer.OrdinalIgnoreCase))));
            return standIn ?? (named.Length == 0 ? target.MissingLanguages[0] : null);
        }

        // "language|title" pairs of the target as pairs.
        private static (string Language, string Title)[] ReadLanguageTitles(WantedTargetData target) =>
            [.. target.LanguageTitles.Select(entry => entry.Split('|', 2)).Where(pair => pair.Length == 2 && pair[0].Length > 0 && pair[1].Length > 0).Select(pair => (pair[0], pair[1]))];

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
