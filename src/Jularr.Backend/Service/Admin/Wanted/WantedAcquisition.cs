using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Integrations.Data.Newznab;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.Sabnzbd;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;

namespace Jularr.Backend.Service.Admin.Wanted
{
    // Search, ranking and grab of one wanted Work; shared by manual search and the automatic worker.
    internal sealed class WantedAcquisition
    {
        private const int ResultLimit = 100;
        private static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(30);
        private static readonly JsonSerializerOptions DefinitionOptions = new() { Converters = { new JsonStringEnumConverter() } };

        private readonly NewznabClient _newznab;
        private readonly SabnzbdClient _sabnzbd;
        private readonly IDataProtector _protector;
        private readonly IMemoryCache _offers;

        public WantedAcquisition(NewznabClient newznab, SabnzbdClient sabnzbd, IDataProtectionProvider protection, IMemoryCache offers)
        {
            _newznab = newznab;
            _sabnzbd = sabnzbd;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
            _offers = offers;
        }

        public static AcquisitionKind? GetKind(MediaType mediaType) => mediaType switch
        {
            MediaType.Movie => AcquisitionKind.Movie,
            MediaType.Series => AcquisitionKind.Tv,
            _ => null
        };

        public async Task<WantedSearchResult> SearchAsync(WantedTargetData target, IReadOnlyList<AcquisitionConnectionData> indexers, SearchDepth depth, CancellationToken ct,
            EpisodeScope? episodes = null)
        {
            // Only supported media types with a profile can be ranked.
            if (GetKind(target.MediaTypeId) is not { } kind || target.Definition is null)
            {
                throw new DataValidationException(nameof(WantedItemParameters.WantedItemId), "invalid_transition");
            }

            var profile = ReadProfile(target);
            var intent = new SearchIntent(kind, target.CanonicalTitle)
            {
                Aliases = target.Titles,
                Year = target.ReleaseYear,
                ExternalIds = ExternalIds(target),
                Season = episodes?.SeasonNumber,
                Episode = episodes?.EpisodeNumber
            };
            var budget = SearchBudget.For(depth);
            var offers = new ConcurrentDictionary<string, ReleaseOffer>(StringComparer.Ordinal);
            var failures = 0;
            var plans = indexers.ToDictionary(indexer => indexer.Id, indexer => SearchPlanner.Plan(intent, ReadCapabilities(indexer), depth));

            // Expansion tiers only run when the stronger queries found nothing usable.
            SelectionResult selection = new([], null, null, SelectionOutcome.NoCandidates);
            for (var tier = 0; tier <= budget.MaxTier; tier++)
            {
                var current = tier;
                if (!plans.Values.Any(plan => plan.Any(query => query.Tier == current)))
                {
                    continue;
                }

                await Parallel.ForEachAsync(indexers, new ParallelOptions { MaxDegreeOfParallelism = budget.MaxConcurrentIndexers, CancellationToken = ct }, async (indexer, token) =>
                {
                    if (!await SearchIndexerAsync(indexer, kind, plans[indexer.Id].Where(query => query.Tier == current), budget, offers, token))
                    {
                        Interlocked.Increment(ref failures);
                    }
                });

                selection = Select(profile, target, kind, episodes, offers.Values);
                if (selection.Outcome == SelectionOutcome.Usable)
                {
                    break;
                }
            }

            // Manual grabs pick from what was shown.
            _offers.Set(CacheKey(target.WantedItemId), offers.ToDictionary(StringComparer.Ordinal), OfferLifetime);
            return new WantedSearchResult(selection, offers, indexers.Count > 0 && failures >= indexers.Count && offers.IsEmpty);
        }

        public ReleaseOffer? FindOffer(long wantedItemId, string candidateId) =>
            _offers.TryGetValue(CacheKey(wantedItemId), out Dictionary<string, ReleaseOffer>? offers) && offers!.TryGetValue(candidateId, out var offer) ? offer : null;

        public async Task GrabAsync(LogicContext context, WantedTargetData target, ReleaseOffer offer, string? qualityKey, IReadOnlyList<AcquisitionConnectionData> clients, CancellationToken ct)
        {
            if (clients.Count == 0)
            {
                throw new ServiceUnavailableException("acquisition_no_download_client");
            }

            if (offer.DownloadUri is null)
            {
                throw new DataValidationException("CandidateId", "invalid_transition");
            }

            // The best client by priority receives the release.
            var client = clients[0];
            SabnzbdGrabResult result;
            try
            {
                result = await _sabnzbd.GrabAsync(new SabnzbdConnection(client.BaseUrl, AcquisitionConnectionInput.Unprotect(_protector, client.ProtectedApiKey)),
                    new SabnzbdGrabRequest(offer.DownloadUri, offer.Title, client.DownloadCategory), ct);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new ServiceUnavailableException("acquisition_unavailable");
            }
            catch (SabnzbdException)
            {
                throw new ServiceUnavailableException("acquisition_grab_failed");
            }

            if (!result.Success || result.NzoIds.Count == 0)
            {
                throw new ServiceUnavailableException("acquisition_grab_failed");
            }

            // Series releases record which episodes they claim.
            int? season = null, start = null, end = null;
            if (target.MediaTypeId == MediaType.Series && ReleaseParser.TryParse(offer.Title, out var parsed) && parsed.SeasonNumber is { } number)
            {
                season = number;
                if (!parsed.IsSeasonPack && parsed.EpisodeStart is { } first)
                {
                    start = first;
                    end = Math.Max(first, parsed.EpisodeEnd ?? first);
                }
            }

            var grab = new AcquisitionGrabData(target.WantedItemId, offer.ConnectionId, Limit(offer.Identity, 600), Limit(offer.Title, 1000), qualityKey, client.Id, Limit(result.NzoIds[0], 128),
                season, start, end);
            await context.ExecuteBatchAsync((ctx, token) => ctx.RecordGrabAsync(grab, token), ct);
        }

        private async Task<bool> SearchIndexerAsync(AcquisitionConnectionData indexer, AcquisitionKind kind, IEnumerable<PlannedQuery> queries, SearchBudget budget,
            ConcurrentDictionary<string, ReleaseOffer> offers, CancellationToken ct)
        {
            // A slow indexer only costs its own share.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(budget.IndexerTimeout);
            var capabilities = ReadCapabilities(indexer);
            var categories = (indexer.Categories.Length > 0 ? indexer.Categories : DefaultCategories(kind))
                .Where(category => capabilities?.Offers(category) ?? true).ToArray();
            var connection = new NewznabConnection(indexer.BaseUrl, AcquisitionConnectionInput.Unprotect(_protector, indexer.ProtectedApiKey));
            try
            {
                foreach (var query in queries)
                {
                    var items = await _newznab.SearchAsync(connection, new NewznabQuery(query.Mode, query.Text, query.Parameters, query.AnyCategory ? [] : categories,
                        Math.Min(ResultLimit, capabilities?.MaximumLimit ?? ResultLimit)), timeout.Token);
                    foreach (var item in items.Take(budget.MaxResults))
                    {
                        var offer = new ReleaseOffer(item.Title, indexer.Id, indexer.Name, indexer.Priority, item.Guid, item.DownloadUri, item.InfoUrl, item.SizeBytes, item.PublishedAt, [query.Stage]);
                        offers.AddOrUpdate(offer.Identity, offer, (_, existing) => existing with { Stages = [.. existing.Stages.Union(offer.Stages)] });
                    }
                }

                return true;
            }
            catch (NewznabException)
            {
                return false;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }

        private static SelectionResult Select(QualityProfile profile, WantedTargetData target, AcquisitionKind kind, EpisodeScope? episodes, IEnumerable<ReleaseOffer> offers)
        {
            // Failed and blocked releases stay visible but are never taken.
            var blocked = target.ReleaseIdentities.ToHashSet(StringComparer.Ordinal);
            return ReleaseSelectionEngine.Select(profile, [.. offers.Select(offer =>
            {
                var judgement = VideoReleaseJudge.Judge(kind, target.CanonicalTitle, target.ReleaseYear, episodes?.Unit, episodes?.Units ?? VideoUnitScope.Empty, offer);
                return new SelectionCandidate(offer.Identity, judgement.Parsed, offer.SizeBytes, offer.ConnectionName, offer.ConnectionPriority, offer.PublishedAt, judgement.Evidence,
                    judgement.Coverage)
                {
                    SafetyRejection = blocked.Contains(offer.Identity) ? "Blocked" : judgement.SafetyRejection,
                    ContextScore = judgement.ContextScore,
                    Languages = judgement.Languages ?? judgement.Parsed?.AudioLanguages ?? []
                };
            })]);
        }

        private static QualityProfile ReadProfile(WantedTargetData target)
        {
            var definition = JsonSerializer.Deserialize<ProfileDefinition>(target.Definition!, DefinitionOptions)
                ?? throw new InvalidOperationException("The acquisition profile is invalid.");
            return new QualityProfile(target.AcquisitionProfileId!.Value.ToString(CultureInfo.InvariantCulture), target.ProfileName!, definition.AllowedQualities,
                definition.QualityOrder, definition.UpgradeAllowed, definition.UpgradeCutoffQuality, definition.MinimumSizeBytes, definition.MaximumSizeBytes, definition.MustContain,
                definition.MustNotContain, definition.RequiredRegex, definition.RejectedRegex, definition.ScoreRules);
        }

        // Provider mappings in the planner's key space.
        private static Dictionary<string, string> ExternalIds(WantedTargetData target)
        {
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in target.ExternalIdentities.Select(identity => identity.Split(':', 2)).Where(pair => pair.Length == 2))
            {
                ids[pair[0].StartsWith("tmdb", StringComparison.Ordinal) ? "tmdb" : pair[0]] = pair[1];
            }

            return ids;
        }

        private static NewznabCapabilities? ReadCapabilities(AcquisitionConnectionData indexer) =>
            indexer.Capabilities is null ? null : JsonSerializer.Deserialize<NewznabCapabilities>(indexer.Capabilities);

        private static int[] DefaultCategories(AcquisitionKind kind) => kind switch
        {
            AcquisitionKind.Movie => [2000],
            AcquisitionKind.Tv or AcquisitionKind.Anime => [5000],
            _ => []
        };

        private static string CacheKey(long wantedItemId) => "acquisition.offers." + wantedItemId.ToString(CultureInfo.InvariantCulture);

        private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];

        private sealed record ProfileDefinition(string[] AllowedQualities, string[] QualityOrder, bool UpgradeAllowed, string? UpgradeCutoffQuality, long? MinimumSizeBytes,
            long? MaximumSizeBytes, string[] MustContain, string[] MustNotContain, string[] RequiredRegex, string[] RejectedRegex, ReleaseScoreRule[] ScoreRules);
    }

    // The season or episode a series search is for and the units it may cover.
    internal sealed record EpisodeScope(int SeasonNumber, int? EpisodeNumber, VideoUnit Unit, VideoUnitScope Units)
    {
        public static EpisodeScope For(IReadOnlyList<EpisodeStateData> episodes, int seasonNumber, int? episodeNumber)
        {
            var season = episodes.Where(episode => episode.SeasonNumber == seasonNumber).ToArray();
            var units = season.Select(episode => new VideoUnit(episode.WorkUnitId, episode.SeasonNumber, episode.EpisodeNumber, episode.HasMedia)).ToArray();
            var wanted = units.Where(unit => season.Any(episode => episode.WorkUnitId == unit.Id && episode.IsMissing)).ToArray();
            var target = episodeNumber is { } number
                ? units.FirstOrDefault(unit => unit.EpisodeNumber == number) ?? new VideoUnit(0, seasonNumber, number, false)
                : wanted.FirstOrDefault() ?? units.FirstOrDefault() ?? new VideoUnit(0, seasonNumber, 1, false);
            return new EpisodeScope(seasonNumber, episodeNumber, target, new VideoUnitScope(units, wanted.Length > 0 ? wanted : [target]));
        }
    }

    internal sealed record WantedSearchResult(SelectionResult Selection, IReadOnlyDictionary<string, ReleaseOffer> Offers, bool IndexersUnavailable)
    {
        // The outcome a target remembers for the next automatic search.
        public string OutcomeKey => IndexersUnavailable ? "indexers_unavailable" : Selection.Outcome switch
        {
            SelectionOutcome.Usable => "release_found",
            SelectionOutcome.ManualReviewOnly => "manual_review",
            SelectionOutcome.ProfileRejected => "profile_rejected",
            SelectionOutcome.IdentityInvalid => "identity_invalid",
            _ => "no_candidates"
        };
    }
}
