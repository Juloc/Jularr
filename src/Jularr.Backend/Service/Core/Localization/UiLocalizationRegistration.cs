using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Localization;
using Jularr.Backend.Data.Logic.Localization;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jularr.Backend.Service.Core.Localization
{
    public static class UiLocalizationRegistration
    {
        public static IServiceCollection AddJularrLocalization(this IServiceCollection services,
            IEnumerable<UiTextResource> resources, IEnumerable<UiTextCatalog>? catalogs = null)
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

            services.AddSingleton(new UiResourceSource(definitions.DistinctBy(resource => resource.Key).ToArray(), [.. catalogs ?? []]));
            services.AddHostedService<UiResourceSynchronization>();
            return services;
        }

        private sealed class UiResourceSource(UiTextResource[] resources, UiTextCatalog[] catalogs)
        {
            public UiTextResource[] Resources { get; } = resources;
            public UiTextCatalog[] Catalogs { get; } = catalogs;
        }

        private sealed class UiResourceSynchronization(SqlDatabase database, UiResourceSource source) : IHostedService
        {
            public async Task StartAsync(CancellationToken cancellationToken)
            {
                // Synchronize resources, never install or migrate the schema.
                await using var sql = new SqlContext(database.DataSource);
                await sql.BeginAsync(SqlAccessMode.ReadWrite, cancellationToken: cancellationToken);
                var context = new LogicContext(sql);
                await context.SynchronizeUiResourcesAsync(source.Resources, cancellationToken);

                // Shipped texts count for the English they were made from and keep its placeholders.
                var english = source.Resources.ToDictionary(resource => resource.Key, resource => resource.EnglishText, StringComparer.Ordinal);
                foreach (var catalog in source.Catalogs)
                {
                    var current = catalog.Texts.Where(text => english.TryGetValue(text.Key, out var source) && source == text.Value.English && UiTextPlaceholders.Match(source, text.Value.Text))
                        .ToArray();
                    await context.SynchronizeShippedTranslationsAsync(new UiShippedCatalogData(catalog.Locale, catalog.Name, catalog.IsRightToLeft, catalog.IsEnabled,
                        catalog.IsReviewed ? UiTranslationStatusType.Reviewed : UiTranslationStatusType.Generated, [.. current.Select(text => text.Key)],
                        [.. current.Select(text => text.Value.Text)]), cancellationToken);
                }

                await sql.CommitAsync(cancellationToken);
            }

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
