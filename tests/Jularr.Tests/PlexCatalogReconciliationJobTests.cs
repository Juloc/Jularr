using System.Security.Claims;
using Jularr.Web.Features.ExternalPlayback.Plex;
using Jularr.Web.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

[TestClass]
public sealed class PlexCatalogReconciliationJobTests
{
    [TestMethod]
    public async Task NonAdminCannotScheduleMaintenanceScan()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var queue = new BackgroundJobQueue(
            services.GetRequiredService<IServiceScopeFactory>());
        var dispatcher = new PlexCatalogReconciliationJobs(queue);

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
            async () =>
            {
                await dispatcher.QueueBatchAsync(
                    new ClaimsPrincipal(new ClaimsIdentity()),
                    "plex-machine-1234", "1", "client-instance");
            });

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            async () =>
            {
                await dispatcher.QueueBatchAsync(
                    null!, "plex-machine-1234", "1", "client-instance");
            });
    }
}
