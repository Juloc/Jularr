using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Servarr;
using Jularr.Integrations.Logic.Servarr;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    // A one-time takeover from Prowlarr, Sonarr or Radarr: indexers, clients, quality profiles with their custom formats and missing titles; nothing keeps running alongside.
    [ServiceEndpoint("POST", "/api/v1/admin/acquisition/takeover")]
    public sealed class RunTakeoverV1 : AdminService<TakeoverParametersV1, RunTakeoverDataV1>
    {
        private const int TitlesPerRun = 50;
        private readonly ServarrApiClient _servarr;
        private readonly MetadataCatalog _catalog;
        private readonly IDataProtector _protector;

        internal RunTakeoverV1(ServarrApiClient servarr, MetadataCatalog catalog, IDataProtectionProvider protection)
        {
            _servarr = servarr;
            _catalog = catalog;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<TakeoverItemV1>>()];
        protected override ModuleRequirement GetInstanceModules(TakeoverParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(TakeoverParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (!Uri.TryCreate(Data.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
            {
                throw new DataValidationException(nameof(RunTakeoverDataV1.BaseUrl), "invalid_url");
            }

            // Everything is read before anything is written.
            var source = new ServarrConnection(address, Data.ApiKey, Data.TakeoverSourceTypeId is TakeoverSourceType.Sonarr or TakeoverSourceType.Radarr ? 3 : 1);
            IReadOnlyList<ServarrIndexer> indexers;
            IReadOnlyList<ServarrClient> clients;
            IReadOnlyList<ServarrTitle> titles;
            IReadOnlyList<ServarrQualityProfile> profiles = [];
            IReadOnlyList<ServarrCustomFormat> formats = [];
            try
            {
                if (Data.TakeoverSourceTypeId is TakeoverSourceType.Sonarr or TakeoverSourceType.Radarr)
                {
                    profiles = await _servarr.ReadQualityProfilesAsync(source, CancellationToken);
                    formats = await _servarr.ReadCustomFormatsAsync(source, CancellationToken);
                }

                indexers = await _servarr.ReadIndexersAsync(source, CancellationToken);
                clients = Data.TakeoverSourceTypeId == TakeoverSourceType.Prowlarr ? [] : await _servarr.ReadClientsAsync(source, CancellationToken);
                titles = Data.TakeoverSourceTypeId switch
                {
                    TakeoverSourceType.Radarr => await _servarr.ReadMoviesAsync(source, CancellationToken),
                    TakeoverSourceType.Sonarr => await _servarr.ReadSeriesAsync(source, CancellationToken),
                    TakeoverSourceType.Lidarr => await _servarr.ReadAlbumsAsync(source, CancellationToken),
                    TakeoverSourceType.Readarr => await _servarr.ReadBooksAsync(source, CancellationToken),
                    _ => []
                };
            }
            catch (ServarrException exception)
            {
                throw new ServiceUnavailableException(exception.Failure == ServarrFailure.Unauthorized ? "acquisition_authentication_failed" : "acquisition_unavailable");
            }

            var (known, jularrProfiles) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        connection."BaseUrl"
                    FROM
                        "AcquisitionConnections" AS connection;
                    """;
                var profileSql = new SqlStatement();
                profileSql.Statement =
                    """
                    SELECT
                        profile."Id",
                        profile."Name",
                        profile."MediaTypeId"
                    FROM
                        "AcquisitionProfiles" AS profile;
                    """;
                return (await ctx.ReadSql.ReadListAsync<AcquisitionConnectionUrlData>(sql, ct), await ctx.ReadSql.ReadListAsync<AcquisitionProfileNameData>(profileSql, ct));
            }, CancellationToken);
            var existing = known.Select(connection => connection.BaseUrl.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Connections without a readable key are completed in the connection form.
            var items = new List<TakeoverItemV1>();
            foreach (var indexer in indexers)
            {
                items.Add(await TakeConnectionAsync(TakeoverItemType.Indexer, indexer.Name, indexer.BaseUrl, indexer.ApiKey, existing,
                    new SaveAcquisitionConnectionDataV1(Limit(indexer.Name, 128), AcquisitionConnectionType.Newznab, indexer.BaseUrl, indexer.ApiKey, indexer.IsEnabled, indexer.Priority,
                        indexer.Categories, null, null, null)));
            }

            foreach (var client in clients)
            {
                items.Add(await TakeConnectionAsync(TakeoverItemType.Client, client.Name, client.BaseUrl, client.ApiKey, existing,
                    new SaveAcquisitionConnectionDataV1(Limit(client.Name, 128), AcquisitionConnectionType.Sabnzbd, client.BaseUrl, client.ApiKey, client.IsEnabled, client.Priority, [],
                        client.Category is { Length: <= 64 } category ? category : null, null, null)));
            }

            // Profiles keep their name; one of that name and type is not taken twice.
            var profileIds = new Dictionary<int, long>();
            var profileType = Data.TakeoverSourceTypeId == TakeoverSourceType.Radarr ? MediaType.Movie : MediaType.Series;
            var formatsById = formats.ToDictionary(format => format.Id);
            foreach (var profile in profiles)
            {
                var mapped = ServarrProfileLogic.Map(profile, formatsById, profileType);
                var name = Limit(profile.Name, 500);
                if (jularrProfiles.FirstOrDefault(existing => existing.MediaTypeId == profileType && string.Equals(existing.Name, mapped.Input.Name, StringComparison.OrdinalIgnoreCase)) is { } same)
                {
                    profileIds[profile.Id] = same.Id;
                    items.Add(new TakeoverItemV1(TakeoverItemType.Profile, name, "takeover_exists", null));
                    continue;
                }

                if (mapped.Input.AllowedQualities.Length == 0)
                {
                    items.Add(new TakeoverItemV1(TakeoverItemType.Profile, name, "takeover_no_qualities", null));
                    continue;
                }

                if (Data.Apply)
                {
                    profileIds[profile.Id] = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                    {
                        var id = await ctx.CreateAcquisitionProfileAsync(mapped.Input, ct);
                        if (mapped.Rules.Count > 0) await ctx.AppendProfileRulesAsync(id, mapped.Rules, ct);
                        return id;
                    }, CancellationToken);
                }

                items.Add(new TakeoverItemV1(TakeoverItemType.Profile, name, Data.Apply ? "takeover_added" : "takeover_will_add", null));
                if (profile.MinimumFormatScore > 0) items.Add(new TakeoverItemV1(TakeoverItemType.Profile, name, "takeover_minimum_score", null));
                items.AddRange(mapped.SkippedFormats.Select(format => new TakeoverItemV1(TakeoverItemType.Format, Limit(format, 500), "takeover_format_skipped", null)));
            }

            // Watched titles without files become wanted through their provider id; large libraries continue in later runs.
            var (type, mediaType) = Data.TakeoverSourceTypeId switch
            {
                TakeoverSourceType.Radarr => (TakeoverItemType.Movie, MediaType.Movie),
                TakeoverSourceType.Sonarr => (TakeoverItemType.Series, MediaType.Series),
                TakeoverSourceType.Lidarr => (TakeoverItemType.Album, MediaType.Music),
                _ => (TakeoverItemType.Book, MediaType.Book)
            };
            var taken = 0;
            foreach (var title in titles.Where(title => title.IsMonitored && title.IsMissing))
            {
                var name = Limit(title.Year is { } year ? title.Title + " (" + year + ")" : title.Title, 500);
                var identity = mediaType switch
                {
                    MediaType.Movie or MediaType.Series => title.TmdbId?.ToString(CultureInfo.InvariantCulture) is { } tmdbId ? (mediaType == MediaType.Movie ? "tmdb_movie" : "tmdb_tv", tmdbId) : default,
                    MediaType.Music => title.ExternalId is { } album ? ("musicbrainz", album) : default,
                    _ => ("openlibrary", "")
                };
                if (identity.Item1 is null)
                {
                    items.Add(new TakeoverItemV1(type, name, "takeover_no_id", null));
                }
                else if (!Data.Apply)
                {
                    items.Add(new TakeoverItemV1(type, name, "takeover_will_want", null));
                }
                else if (taken++ >= TitlesPerRun)
                {
                    items.Add(new TakeoverItemV1(type, name, "takeover_next_run", null));
                }
                else if ((mediaType == MediaType.Book ? await FindBookAsync(title) : identity.Item2) is not { } externalId)
                {
                    items.Add(new TakeoverItemV1(type, name, "takeover_no_match", null));
                }
                else
                {
                    var workId = await _catalog.ImportAsync(Context, mediaType, null, identity.Item1, externalId, CancellationToken);
                    await Context.ExecuteBatchAsync(async (ctx, ct) =>
                    {
                        await ctx.EnsureWantedItemAsync(new WantedTakeoverData(workId, title.QualityProfileId is { } profileId && profileIds.TryGetValue(profileId, out var id) ? id : null), ct);
                        return true;
                    }, CancellationToken);
                    items.Add(new TakeoverItemV1(type, name, "takeover_wanted", null));
                }
            }

            return new BaseDataList<TakeoverItemV1>([.. items.Take(1000)]);
        }

        private async Task<TakeoverItemV1> TakeConnectionAsync(TakeoverItemType type, string name, string baseUrl, string? apiKey, HashSet<string> existing, SaveAcquisitionConnectionDataV1 data)
        {
            if (existing.Contains(baseUrl.TrimEnd('/')))
            {
                return new TakeoverItemV1(type, Limit(name, 500), "takeover_exists", null);
            }

            if (apiKey is null)
            {
                return new TakeoverItemV1(type, Limit(name, 500), "takeover_needs_key", baseUrl);
            }

            if (!Data.Apply)
            {
                return new TakeoverItemV1(type, Limit(name, 500), "takeover_will_add", null);
            }

            await Context.ExecuteBatchAsync((ctx, ct) => ctx.CreateAcquisitionConnectionAsync(AcquisitionConnectionInput.Create(null, data, _protector), ct), CancellationToken);
            existing.Add(baseUrl.TrimEnd('/'));
            return new TakeoverItemV1(type, Limit(name, 500), "takeover_added", null);
        }

        // The one Open Library work of that title, author and year; anything less clear is left for a person.
        private async Task<string?> FindBookAsync(ServarrTitle title)
        {
            var found = (await _catalog.SearchAsync(Context, MediaType.Book, null, title.Title, title.Year, CancellationToken))
                .Where(result => result.Namespace == BookMetadata.Namespace && TitleMatcher.Matches(title.Title, result.Title)
                    && (title.Year is null || result.ReleaseYear is null || Math.Abs(title.Year.Value - result.ReleaseYear.Value) <= 1)
                    && (title.Creator is null || result.Author?.Contains(title.Creator, StringComparison.OrdinalIgnoreCase) == true))
                .ToArray();
            return found.Length == 1 ? found[0].ExternalId : null;
        }

        private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
    }
}
