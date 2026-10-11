using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Jularr.Backend.Service.Core.Localization
{
    public static class UiLocalizationRegistration
    {
        public static IServiceCollection AddJularrLocalization(this IServiceCollection services,
            IEnumerable<UiTextResource> resources)
        {
            var definitions = ServiceErrorResources.English.Concat(resources).ToArray();
            foreach (var definition in definitions)
            {
                definition.Validate();
            }

            if (definitions.GroupBy(resource => resource.Key, StringComparer.Ordinal)
                .Any(group => group.Select(resource => resource.EnglishText).Distinct(StringComparer.Ordinal).Count() > 1))
            {
                throw new InvalidOperationException("A localization key has conflicting English declarations.");
            }

            services.AddSingleton(new UiResourceSource(definitions.DistinctBy(resource => resource.Key).ToArray()));
            services.AddHostedService<UiResourceSynchronization>();
            return services;
        }

        private sealed class UiResourceSource(UiTextResource[] resources)
        {
            public UiTextResource[] Resources { get; } = resources;
        }

        private sealed class UiResourceSynchronization(NpgsqlDataSource database, UiResourceSource source) : IHostedService
        {
            public async Task StartAsync(CancellationToken cancellationToken)
            {
                // Synchronize resources, never install or migrate the schema.
                await using var sql = new SqlContext(database);
                await sql.BeginAsync(SqlAccessMode.ReadWrite, cancellationToken: cancellationToken);
                var context = new LogicContext(sql);
                await context.SynchronizeUiResourcesAsync(source.Resources, cancellationToken);
                await sql.CommitAsync(cancellationToken);
            }

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
