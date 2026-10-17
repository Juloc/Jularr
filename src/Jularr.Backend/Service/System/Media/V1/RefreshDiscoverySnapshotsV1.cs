using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Tmdb;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.System.Media.V1
{
    // A bounded refresh batch owns its own context; no request context survives the page response.
    public sealed class RefreshDiscoverySnapshotsV1 : SystemService<RefreshDiscoverySnapshotsParametersV1, NoData>
    {
        private readonly WorkMetadata _metadata;
        private readonly MetadataCatalog _catalog;
        private readonly ProviderOptions _providers;
        private readonly ILogger<RefreshDiscoverySnapshotsV1> _logger;

        internal RefreshDiscoverySnapshotsV1(WorkMetadata metadata, MetadataCatalog catalog, ProviderOptions providers, ILogger<RefreshDiscoverySnapshotsV1> logger)
        {
            _metadata = metadata;
            _catalog = catalog;
            _providers = providers;
            _logger = logger;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(RefreshDiscoverySnapshotsParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(RefreshDiscoverySnapshotsParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var updated = false;
            for (var index = 0; index < 4; index++)
            {
                var snapshot = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ClaimDiscoverySnapshotAsync(ct), CancellationToken);
                if (snapshot is null)
                {
                    break;
                }
                var request = JsonSerializer.Deserialize<DiscoverySnapshotRequest>(snapshot.Request)!;
                try
                {
                    object result;
                    if (request.Operation.StartsWith("tmdb.", StringComparison.Ordinal))
                    {
                        if (request.Origin != _providers.Tmdb.AbsoluteUri)
                        {
                            continue;
                        }
                        var connection = await _metadata.ConnectAsync(Context, CancellationToken);
                        result = await _metadata.FetchSnapshotAsync(request, new TmdbConnection(connection.Credential, request.Language!), CancellationToken);
                    }
                    else
                    {
                        if (request.Origin != _catalog.CatalogOrigin(request.MediaType, request.Subtype))
                        {
                            continue;
                        }
                        result = request.Operation == "catalog.anime.work"
                            ? await _catalog.FetchAnimeWorkAsync(request.Id!.Value, CancellationToken)
                            : await _catalog.FetchSnapshotAsync(Context, request, CancellationToken);
                    }
                    await DiscoverySnapshots.SaveAsync(Context, snapshot.Key, request, result, CancellationToken);
                    updated = true;
                }
                catch (Exception exception) when (exception is ServiceUnavailableException or KeyNotFoundException)
                {
                    // The lease is also the retry backoff. Preserve every previous payload on provider failure.
                    _logger.LogWarning("Discovery refresh unavailable for {Operation}; retaining the stored snapshot.", request.Operation);
                }
            }
            return new UpdateResult(updated);
        }
    }
}
