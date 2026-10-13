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
    // A one-time takeover from Prowlarr, Sonarr or Radarr: indexers, clients and missing titles; nothing keeps running alongside.
    [ServiceEndpoint("POST", "/api/v1/admin/acquisition/takeover")]
    public sealed class RunTakeoverV1 : AdminService<TakeoverParametersV1, RunTakeoverDataV1>
    {
        private const int TitlesPerRun = 50;
        private readonly ServarrApiClient _servarr;
        private readonly WorkMetadata _metadata;
        private readonly IDataProtector _protector;

        internal RunTakeoverV1(ServarrApiClient servarr, WorkMetadata metadata, IDataProtectionProvider protection)
        {
            _servarr = servarr;
            _metadata = metadata;
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
            var source = new ServarrConnection(address, Data.ApiKey, Data.TakeoverSourceTypeId == TakeoverSourceType.Prowlarr ? 1 : 3);
            IReadOnlyList<ServarrIndexer> indexers;
            IReadOnlyList<ServarrClient> clients;
            IReadOnlyList<ServarrTitle> titles;
            try
            {
                indexers = await _servarr.ReadIndexersAsync(source, CancellationToken);
                clients = Data.TakeoverSourceTypeId == TakeoverSourceType.Prowlarr ? [] : await _servarr.ReadClientsAsync(source, CancellationToken);
                titles = Data.TakeoverSourceTypeId switch
                {
                    TakeoverSourceType.Radarr => await _servarr.ReadMoviesAsync(source, CancellationToken),
                    TakeoverSourceType.Sonarr => await _servarr.ReadSeriesAsync(source, CancellationToken),
                    _ => []
                };
            }
            catch (ServarrException exception)
            {
                throw new ServiceUnavailableException(exception.Failure == ServarrFailure.Unauthorized ? "acquisition_authentication_failed" : "acquisition_unavailable");
            }

            var known = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        connection."BaseUrl"
                    FROM
                        "AcquisitionConnections" AS connection;
                    """;
                return ctx.ReadSql.ReadListAsync<AcquisitionConnectionUrlData>(sql, ct);
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

            // Watched titles without files become wanted; large libraries continue in later runs.
            var type = Data.TakeoverSourceTypeId == TakeoverSourceType.Radarr ? TakeoverItemType.Movie : TakeoverItemType.Series;
            var mediaType = type == TakeoverItemType.Movie ? MediaType.Movie : MediaType.Series;
            var wanted = titles.Where(title => title.IsMonitored && title.IsMissing).ToArray();
            var connection = Data.Apply && wanted.Any(title => title.TmdbId is not null) ? await _metadata.ConnectAsync(Context, CancellationToken) : null;
            var taken = 0;
            foreach (var title in wanted)
            {
                var name = title.Year is { } year ? title.Title + " (" + year + ")" : title.Title;
                if (title.TmdbId is not { } tmdbId)
                {
                    items.Add(new TakeoverItemV1(type, Limit(name, 500), "takeover_no_tmdb", null));
                }
                else if (connection is null)
                {
                    items.Add(new TakeoverItemV1(type, Limit(name, 500), "takeover_will_want", null));
                }
                else if (taken++ >= TitlesPerRun)
                {
                    items.Add(new TakeoverItemV1(type, Limit(name, 500), "takeover_next_run", null));
                }
                else
                {
                    var workId = await _metadata.ImportAsync(Context, connection, mediaType, null, tmdbId, CancellationToken);
                    await Context.ExecuteBatchAsync(async (ctx, ct) =>
                    {
                        await ctx.EnsureWantedItemAsync(new WorkIdData(workId), ct);
                        return true;
                    }, CancellationToken);
                    items.Add(new TakeoverItemV1(type, Limit(name, 500), "takeover_wanted", null));
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

        private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
    }
}
