using Jularr.Backend.Service.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jularr.Backend.Tests.Infrastructure
{
    // Hosts of focused tests run on a database where the test installed only the baselines it needs. The real startup owner refuses such a
    // database (it matches no schema version), so these hosts leave schema installation to the test.
    internal sealed class PartialSchemaStartup(string connectionString) : DatabaseBaseline(connectionString)
    {
        public override Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    internal static class PartialSchema
    {
        public static IServiceCollection WithTestInstalledSchema(this IServiceCollection services, string connectionString) =>
            services.Replace(ServiceDescriptor.Singleton<DatabaseBaseline>(_ => new PartialSchemaStartup(connectionString)));
    }
}
