using System.Data;
using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Fields.Localization;
using Jularr.Backend.Data.Logic.Localization;
using Jularr.Backend.Infrastructure.Sql;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Jularr.Backend.Service.Core.Localization
{
    public sealed class UiTextCatalog : IUiTextCatalog, IDisposable
    {
        private readonly NpgsqlDataSource _database;
        private readonly TimeProvider _time;
        private readonly SemaphoreSlim _loadLock = new(1, 1);
        private readonly MemoryCache _bundles = new(new MemoryCacheOptions { SizeLimit = 4 * 1024 * 1024 });
        private IReadOnlyList<UiLocaleData> _locales = [];
        private DateTimeOffset _refreshAt;

        public UiTextCatalog(NpgsqlDataSource database, TimeProvider time)
        {
            _database = database;
            _time = time;
        }

        public Task<UiTextBundle> GetAsync(CultureInfo culture, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(culture);
            return ReadAsync(culture, null, cancellationToken);
        }

        public Task<UiTextBundle> GetByLocaleIdAsync(long uiLocaleId, CancellationToken cancellationToken = default)
        {
            UiLocaleFields.Id.Validate(uiLocaleId, nameof(uiLocaleId));
            return ReadAsync(null, uiLocaleId, cancellationToken);
        }

        private async Task<UiTextBundle> ReadAsync(CultureInfo? culture, long? uiLocaleId, CancellationToken cancellationToken)
        {
            // Cold loads and metadata refreshes are coalesced; indexing a bundle never queries SQL.
            await _loadLock.WaitAsync(cancellationToken);
            try
            {
                await using var context = new SqlContext(_database);
                var opened = false;
                if (_time.GetUtcNow() >= _refreshAt)
                {
                    await context.BeginAsync(SqlAccessMode.ReadOnly, IsolationLevel.RepeatableRead, cancellationToken);
                    opened = true;
                    _locales = await ReadLocalesAsync(context, cancellationToken);
                    _refreshAt = _time.GetUtcNow().AddSeconds(30);
                }

                var requested = uiLocaleId is { } id
                    ? _locales.FirstOrDefault(locale => locale.Id == id)?.Locale ?? "en"
                    : culture?.Name ?? "en";
                var layers = GetFallbackLayers(requested);
                var selected = layers[^1];
                var version = string.Join("/", layers.Select(locale => $"{locale.Id}:{locale.CatalogVersion}:{locale.Locale}:{locale.IsRightToLeft}"));

                if (_bundles.TryGetValue(selected.Id, out UiTextBundle? cached) && cached is not null && cached.Version == version)
                {
                    return cached;
                }

                if (!opened)
                {
                    await context.BeginAsync(SqlAccessMode.ReadOnly, IsolationLevel.RepeatableRead, cancellationToken);
                    // Read versions and values from the same snapshot when rebuilding a language.
                    _locales = await ReadLocalesAsync(context, cancellationToken);
                    layers = GetFallbackLayers(requested);
                    selected = layers[^1];
                    version = string.Join("/", layers.Select(locale => $"{locale.Id}:{locale.CatalogVersion}:{locale.Locale}:{locale.IsRightToLeft}"));
                }

                var texts = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var layer in layers)
                {
                    foreach (var row in await ReadTextsAsync(context, layer, cancellationToken))
                    {
                        texts[row.Key] = row.Text;
                    }
                }

                var bundle = new UiTextBundle(selected.Id, selected.Locale, selected.IsRightToLeft, version, texts);
                // Approximate retained characters plus dictionary/key overhead. No semantic context is cached.
                var size = Math.Max(1, texts.Sum(text => (long)(text.Key.Length + text.Value.Length) * 2 + 96));
                _bundles.Set(selected.Id, bundle, new MemoryCacheEntryOptions
                {
                    Size = size,
                    SlidingExpiration = TimeSpan.FromMinutes(10)
                });
                return bundle;
            }
            finally
            {
                _loadLock.Release();
            }
        }

        private List<UiLocaleData> GetFallbackLayers(string requested)
        {
            var english = _locales.SingleOrDefault(locale => locale.Locale.Equals("en", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The English catalog must be synchronized before serving requests.");
            var layers = new List<UiLocaleData> { english };
            var current = CultureInfo.GetCultureInfo(requested);
            var exactToParent = new List<UiLocaleData>();
            while (!string.IsNullOrEmpty(current.Name))
            {
                var locale = _locales.SingleOrDefault(item => item.IsEnabled
                    && item.Locale.Equals(current.Name, StringComparison.OrdinalIgnoreCase));
                if (locale is not null && locale.Id != english.Id)
                {
                    exactToParent.Add(locale);
                }

                current = current.Parent;
            }

            exactToParent.Reverse();
            layers.AddRange(exactToParent);
            return layers;
        }

        private static Task<IReadOnlyList<UiLocaleData>> ReadLocalesAsync(SqlContext context, CancellationToken cancellationToken)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    "Id",
                    "Locale",
                    "IsEnabled",
                    "IsRightToLeft",
                    "CatalogVersion"
                FROM
                    "UiLocales";
                """;
            return context.ReadSql.ReadListAsync<UiLocaleData>(sql, cancellationToken);
        }

        private static Task<IReadOnlyList<UiCatalogTextData>> ReadTextsAsync(SqlContext context,
            UiLocaleData locale, CancellationToken cancellationToken)
        {
            var sql = new SqlStatement();
            if (locale.Locale.Equals("en", StringComparison.OrdinalIgnoreCase))
            {
                sql.Statement =
                    """
                    SELECT
                        "Key",
                        "EnglishText" AS "Text"
                    FROM
                        "UiTranslationMessages";
                    """;
            }
            else
            {
                sql.Statement =
                    """
                    SELECT
                        message."Key",
                        translation."Text"
                    FROM
                        "UiTranslations" AS translation
                    INNER JOIN "UiTranslationMessages" AS message
                        ON message."Id" = translation."UiTranslationMessageId"
                    WHERE
                        translation."UiLocaleId" = @UiLocaleId
                        AND (translation."UiTranslationStatusTypeId" = 4
                            OR (translation."UiTranslationStatusTypeId" IN (2, 3)
                                AND translation."MessageVersion" = message."Version"));
                    """;
                sql.Parameters.Add(new UiLocaleParameters(locale.Id));
            }

            return context.ReadSql.ReadListAsync<UiCatalogTextData>(sql, cancellationToken);
        }

        public void Dispose()
        {
            _bundles.Dispose();
            _loadLock.Dispose();
        }
    }
}
