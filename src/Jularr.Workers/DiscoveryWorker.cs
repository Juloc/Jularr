using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Service.System.Media.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    internal sealed class DiscoveryWorker : PeriodicWorker
    {
        public DiscoveryWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<DiscoveryWorker> logger)
            : base(scopes, time, logger, TimeSpan.FromMinutes(1))
        {
        }

        protected override Task RunOnceAsync(CancellationToken ct) => RunAsync("Discovery cache refresh",
            (services, token) => services.GetRequiredService<RefreshDiscoverySnapshotsV1>().ExecuteAsync<UpdateResult>(new RefreshDiscoverySnapshotsParametersV1(), default, token), ct);
    }
}
