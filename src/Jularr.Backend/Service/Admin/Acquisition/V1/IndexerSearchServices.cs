using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Newznab;
using Jularr.Integrations.Data.Nyaa;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.Nyaa;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    // The indexers a free search can ask: the Newznab indexers and the torrent index with interactive search on, with the categories they offer.
    [ServiceEndpoint("GET", "/api/v1/admin/indexer-search/indexers")]
    public sealed class ReadIndexerSearchSourcesV1 : AdminService<IndexerSearchParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<IndexerSearchSourceV1>>()];
        protected override ModuleRequirement GetInstanceModules(IndexerSearchParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(IndexerSearchParametersV1 parameters, Type resultType) => new(IndexerSearch.Permission);

        protected override async Task<IServiceOutput> OnRead()
        {
            var connections = await Context.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Search, CancellationToken);
            return new BaseDataList<IndexerSearchSourceV1>([.. IndexerSearch.Sources(connections).Select(connection => new IndexerSearchSourceV1(connection.Id, connection.Name,
                IndexerSearch.Protocol(connection), IndexerSearch.Categories(connection)))]);
        }
    }

    // Prowlarr's search: one page of every chosen indexer at once, each within its own time; an indexer that fails or is slow costs only its own releases.
    [ServiceEndpoint("POST", "/api/v1/admin/indexer-search")]
    public sealed class SearchIndexersV1 : AdminService<IndexerSearchParametersV1, IndexerSearchDataV1>
    {
        private static readonly TimeSpan IndexerTimeout = TimeSpan.FromSeconds(15);
        private readonly NewznabClient _newznab;
        private readonly NyaaClient _nyaa;
        private readonly IndexerSearchOffers _offers;
        private readonly IDataProtector _protector;
        private readonly TimeProvider _time;
        private readonly ILogger<SearchIndexersV1> _logger;

        internal SearchIndexersV1(NewznabClient newznab, NyaaClient nyaa, IndexerSearchOffers offers, IDataProtectionProvider protection, TimeProvider time, ILogger<SearchIndexersV1> logger)
        {
            _logger = logger;
            _newznab = newznab;
            _nyaa = nyaa;
            _offers = offers;
            _time = time;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<IndexerSearchResultV1>()];
        protected override ModuleRequirement GetInstanceModules(IndexerSearchParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(IndexerSearchParametersV1 parameters, Type resultType) => new(IndexerSearch.Permission);

        protected override async Task<IServiceOutput> OnExecute()
        {
            var connections = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Search, ct), CancellationToken);
            var chosen = Data.IndexerIds.ToHashSet();
            var sources = IndexerSearch.Sources(connections).Where(connection => chosen.Count == 0 || chosen.Contains(connection.Id)).ToArray();
            var query = IndexerSearchQuery.Parse(Data.IndexerSearchTypeId, Data.Query);
            var found = new ConcurrentBag<(ReleaseOffer Offer, NewznabItem? Item, NyaaEntry? Entry)>();
            var statuses = new ConcurrentDictionary<long, IndexerSearchStatusV1>();
            await Parallel.ForEachAsync(sources, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, sources.Length), CancellationToken = CancellationToken }, async (source, ct) =>
            {
                var clock = Stopwatch.StartNew();
                (int? Count, string? Error) answer;
                using (var share = new CancellationTokenSource(IndexerTimeout, _time))
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, share.Token))
                {
                    try
                    {
                        answer = (await AskAsync(source, query, found, timeout.Token), null);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        answer = (null, "source_timeout");
                    }
                    catch (NewznabException exception)
                    {
                        // The message never holds the key or the link; it names what the indexer answered.
                        _logger.LogWarning("Indexer search of {IndexerId} failed: {Failure}, HTTP {StatusCode}: {Reason}", source.Id, exception.Failure, (int?)exception.StatusCode, exception.Message);
                        answer = (null, AcquisitionConnectionInput.ErrorKey(exception.Failure));
                    }
                    catch (Exception exception) when (exception is NyaaException or HttpRequestException)
                    {
                        answer = (null, "acquisition_unavailable");
                    }
                    catch (IndexerSkippedException exception)
                    {
                        answer = (null, exception.ErrorKey);
                    }
                }

                statuses[source.Id] = new IndexerSearchStatusV1(source.Id, source.Name, answer.Count, (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds), answer.Error);
            });

            // Every indexer asked records its use; one left out for its limit was not asked.
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                foreach (var status in statuses.Values.Where(status => status.ErrorKey != "acquisition_query_limit_reached" && status.ErrorKey != "category_not_offered"))
                {
                    await ctx.RecordIndexerUseAsync(new IndexerUseData(status.Id, 1, status.ErrorKey is null), ct);
                }

                return true;
            }, CancellationToken);

            var releases = found.GroupBy(item => item.Offer.Identity, StringComparer.Ordinal).Select(group => group.First())
                .OrderByDescending(item => item.Offer.PublishedAt).Select(item => IndexerSearch.Release(_offers.Keep(item.Offer), item.Offer, item.Item, item.Entry)).ToArray();
            return new IndexerSearchResultV1(new BaseDataList<IndexerSearchReleaseV1>(releases),
                new BaseDataList<IndexerSearchStatusV1>([.. sources.Select(source => statuses[source.Id])]));
        }

        // One page of one indexer; the number of its releases.
        private async Task<int> AskAsync(AcquisitionConnectionData source, IndexerSearchQuery query, ConcurrentBag<(ReleaseOffer, NewznabItem?, NyaaEntry?)> found, CancellationToken ct)
        {
            if (!AcquisitionConnectionLogic.HasQueries(source, 0)) throw new IndexerSkippedException("acquisition_query_limit_reached");
            var offered = IndexerSearch.Categories(source);
            var categories = Data.Categories.Length == 0 ? [] : Data.Categories.Where(category => offered.Length == 0 || Offers(offered, category)).ToArray();
            if (Data.Categories.Length > 0 && categories.Length == 0) throw new IndexerSkippedException("category_not_offered");
            if (source.AcquisitionConnectionTypeId == AcquisitionConnectionType.Nyaa)
            {
                // The torrent index has no pages: its one answer is the first page.
                if (Data.Offset > 0) return 0;
                var entries = (await _nyaa.SearchAsync(new Uri(source.HttpAddress.TrimEnd('/') + "/"), query.Words(), ["0_0"], ct))
                    .Where(entry => categories.Length == 0 || categories.Any(category => category / 1000 == IndexerSearch.NyaaCategory(entry.Category) / 1000)).Take(Data.Limit).ToArray();
                foreach (var entry in entries)
                {
                    found.Add((new ReleaseOffer(TextLimit.Cut(entry.Title, 1000)!, source.Id, source.Name, source.Priority, "nyaa:" + entry.Id,
                        entry.DownloadUri is { } download ? new Uri(download) : NyaaClient.TorrentUri(entry.Id, entry.ViewUri), entry.ViewUri, entry.SizeBytes, entry.Published, ["search"])
                        { Transport = DownloadTransport.Torrent }, null, entry));
                }

                return entries.Length;
            }

            var capabilities = WantedAcquisition.ReadCapabilities(source);
            var (mode, text, parameters) = query.For(capabilities);
            var limit = Math.Min(Data.Limit, capabilities?.MaximumLimit ?? Data.Limit);
            var connection = new NewznabConnection(source.HttpAddress, AcquisitionConnectionInput.IndexerKey(_protector, source.ProtectedApiKey));
            var items = await _newznab.SearchAsync(connection, new NewznabQuery(mode, text, [.. parameters, new("extended", "1")], categories, limit, Data.Offset), ct);
            foreach (var item in items)
            {
                found.Add((new ReleaseOffer(TextLimit.Cut(item.Title, 1000)!, source.Id, source.Name, source.Priority, item.Guid, item.DownloadUri, item.InfoUrl, item.SizeBytes, item.PublishedAt, ["search"])
                    { IndexerFlags = item.IndexerFlags, Transport = IndexerSearch.IsTorrent(item.DownloadUri) ? DownloadTransport.Torrent : DownloadTransport.Usenet }, item, null));
            }

            return items.Count;
        }

        // A parent category takes in its sub categories, and the other way round.
        private static bool Offers(int[] offered, int category) => offered.Contains(category) || offered.Any(offer => offer / 1000 == category / 1000 && (offer % 1000 == 0 || category % 1000 == 0));

        private sealed class IndexerSkippedException(string errorKey) : Exception(errorKey)
        {
            public string ErrorKey { get; } = errorKey;
        }
    }

    // The .nzb or .torrent of a release of the last searches, as the indexer delivers it.
    [ServiceEndpoint("POST", "/api/v1/admin/indexer-search/releases/{releaseId}/file")]
    public sealed class ReadIndexerReleaseFileV1 : AdminService<IndexerReleaseParametersV1, NoData>
    {
        private readonly NewznabClient _newznab;
        private readonly IndexerSearchOffers _offers;

        internal ReadIndexerReleaseFileV1(NewznabClient newznab, IndexerSearchOffers offers)
        {
            _newznab = newznab;
            _offers = offers;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<IndexerReleaseFileV1>()];
        protected override ModuleRequirement GetInstanceModules(IndexerReleaseParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(IndexerReleaseParametersV1 parameters, Type resultType) => new(IndexerSearch.Permission);

        protected override async Task<IServiceOutput> OnExecute()
        {
            var offer = _offers.Find(Parameters.ReleaseId) ?? throw new DataValidationException(nameof(IndexerReleaseParametersV1.ReleaseId), "outdated_version");
            if (offer.DownloadUri is not { Scheme: "http" or "https" } address)
            {
                throw new DataValidationException(nameof(IndexerReleaseParametersV1.ReleaseId), "invalid_transition");
            }

            // Jularr fetches the file; the indexer's link never reaches the browser.
            var torrent = offer.Transport == DownloadTransport.Torrent;
            try
            {
                return new IndexerReleaseFileV1(await _newznab.ReadFileAsync(address, CancellationToken), IndexerSearch.FileName(offer.Title, torrent ? ".torrent" : ".nzb"),
                    torrent ? "application/x-bittorrent" : "application/x-nzb");
            }
            catch (NewznabException exception)
            {
                throw new ServiceUnavailableException(AcquisitionConnectionInput.ErrorKey(exception.Failure));
            }
        }
    }

    // Prowlarr's download: the release to the best download client of its kind, without a work. It downloads like any other and waits in the queue, not imported, for a person.
    [ServiceEndpoint("POST", "/api/v1/admin/indexer-search/releases/{releaseId}/download")]
    public sealed class DownloadIndexerReleaseV1 : AdminService<IndexerReleaseParametersV1, NoData>
    {
        private readonly IndexerSearchOffers _offers;
        private readonly WantedAcquisition _acquisition;

        internal DownloadIndexerReleaseV1(IndexerSearchOffers offers, WantedAcquisition acquisition)
        {
            _offers = offers;
            _acquisition = acquisition;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(IndexerReleaseParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(IndexerReleaseParametersV1 parameters, Type resultType) => new(IndexerSearch.Permission);

        protected override async Task<IServiceOutput> OnExecute()
        {
            var offer = _offers.Find(Parameters.ReleaseId) ?? throw new DataValidationException(nameof(IndexerReleaseParametersV1.ReleaseId), "outdated_version");
            var clients = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Download, ct), CancellationToken);
            await _acquisition.DownloadAsync(Context, offer, clients, Auth.AccountId, CancellationToken);
            return new UpdateResult(true);
        }
    }

    // The releases of the last free searches by their ReleaseId, for an hour; the grab and the download take them from here, never from the browser.
    internal sealed class IndexerSearchOffers(IMemoryCache cache)
    {
        private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

        public string Keep(ReleaseOffer offer)
        {
            var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(offer.Identity)))[..32];
            cache.Set(Key(id), offer, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime, Size = 1 });
            return id;
        }

        public ReleaseOffer? Find(string releaseId) => cache.TryGetValue(Key(releaseId), out ReleaseOffer? offer) ? offer : null;

        private static string Key(string id) => "indexer-search.offer." + id;
    }

    internal static class IndexerSearch
    {
        public const string Permission = "indexers.search";

        // The Newznab categories of the torrent index's own (Prowlarr's mapping): anime, audio, literature, live action, pictures, software.
        private static readonly int[] NyaaCategories = [5070, 3000, 7000, 5000, 8000, 4000];

        public static IEnumerable<AcquisitionConnectionData> Sources(IEnumerable<AcquisitionConnectionData> connections) => connections.Where(connection =>
            connection.AcquisitionConnectionTypeId is AcquisitionConnectionType.Newznab or AcquisitionConnectionType.Nyaa && connection.SearchesInteractively);

        public static ReleaseProtocol Protocol(AcquisitionConnectionData connection) =>
            connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Nyaa ? ReleaseProtocol.Torrent : ReleaseProtocol.Usenet;

        public static int[] Categories(AcquisitionConnectionData connection) => connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Nyaa
            ? NyaaCategories : WantedAcquisition.ReadCapabilities(connection)?.Categories ?? [];

        // "1_2" (anime, English translated) is TV/Anime; an unknown main category counts as other.
        public static int NyaaCategory(string category) => int.TryParse(category.Split('_')[0], out var main) && main is >= 1 and <= 6 ? NyaaCategories[main - 1] : 8000;

        public static bool IsTorrent(Uri? address) => address is not null
            && (address.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase) || address.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase));

        public static IndexerSearchReleaseV1 Release(string releaseId, ReleaseOffer offer, NewznabItem? item, NyaaEntry? entry)
        {
            var categories = item is not null ? (item.Categories.Count > 0 ? [.. item.Categories] : item.Category is { } one ? [one] : [])
                : entry is not null ? new[] { NyaaCategory(entry.Category) } : [];
            // Without a category a name that reads as a video release is a film or, with a season, an episode.
            var video = ReleaseParser.TryParse(offer.Title, out var parsed);
            var kind = Kind(categories) ?? (video && parsed.Resolution is not null ? parsed.SeasonNumber is null ? AcquisitionKind.Movie : AcquisitionKind.Tv : null);
            var languages = video && parsed.AudioLanguages.Count > 0 ? [.. parsed.AudioLanguages] : ReleaseWords.ReadLanguages(offer.Title, "");
            var torrent = offer.Transport == DownloadTransport.Torrent;
            return new IndexerSearchReleaseV1(releaseId, offer.Title, offer.ConnectionId, offer.ConnectionName, torrent ? ReleaseProtocol.Torrent : ReleaseProtocol.Usenet, offer.SizeBytes,
                offer.PublishedAt?.UtcDateTime, Link(offer.InfoUrl), categories, item?.Grabs ?? entry?.Downloads, item?.Files, torrent ? item?.Seeders ?? entry?.Seeders : null,
                torrent ? (item?.Peers is { } peers && item.Seeders is { } seeders ? Math.Max(0, peers - seeders) : entry?.Leechers) : null,
                [.. offer.IndexerFlags], Id(item?.ImdbId, 32) is { } imdb ? "tt" + imdb.TrimStart('t').PadLeft(7, '0') : null, Id(item?.TmdbId, 32), Id(item?.TvdbId, 32), Id(item?.TvMazeId, 32),
                [.. languages.Select(language => language.ToLowerInvariant()).Where(language => language.Length is > 0 and <= 16).Distinct().Take(16)],
                TextLimit.Cut(kind is { } known ? AcquisitionSelectionLogic.QualityKey(known, offer.Title) : null, 64), kind == AcquisitionKind.Game ? Platform(offer.Title) : null);
        }

        // The kind a release's category names, for the quality read from its name.
        private static AcquisitionKind? Kind(IReadOnlyList<int> categories) => categories.Count == 0 ? null : categories[0] switch
        {
            5070 => AcquisitionKind.Anime,
            >= 5000 and < 6000 => AcquisitionKind.Tv,
            >= 2000 and < 3000 => AcquisitionKind.Movie,
            >= 3000 and < 4000 => AcquisitionKind.Music,
            >= 7000 and < 8000 => AcquisitionKind.Book,
            >= 1000 and < 2000 or 4050 => AcquisitionKind.Game,
            _ => null
        };

        // The platform a game's name states.
        private static string? Platform(string title)
        {
            var words = ReleaseWords.Tokens(title).Select(word => word.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
            return new[] { ("PS5", "PS5"), ("PS4", "PS4"), ("NSW", "Switch"), ("SWITCH", "Switch"), ("XBOX", "Xbox"), ("XBOXONE", "Xbox"), ("GOG", "PC"), ("PC", "PC") }
                .FirstOrDefault(pair => words.Contains(pair.Item1)).Item2;
        }

        private static string? Link(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var address) && address.Scheme is "http" or "https" && url!.Length <= 2048 ? url : null;

        private static string? Id(string? value, int length) => value is { Length: > 0 } && value.Length <= length ? value : null;

        public static string FileName(string title, string extension)
        {
            var name = new string([.. title.Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or ' ' or '[' or ']' or '(' or ')' ? character : '_')]).Trim();
            return (name.Length == 0 ? "release" : name.Length > 150 ? name[..150] : name) + extension;
        }
    }
}
