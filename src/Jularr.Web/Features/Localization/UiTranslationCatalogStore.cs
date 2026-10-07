using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Localization;

public sealed class UiTranslationCatalogStore(AppDbContext db)
{
    public async Task SyncSourceMessagesAsync(CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var now = DateTime.UtcNow;

            await EnsureSourceLocaleAsync(connection, transaction, now, cancellationToken);

            foreach (var message in UiTranslationResources.All)
            {
                await MarkGeneratedTranslationsOutdatedAsync(
                    connection,
                    transaction,
                    message,
                    now,
                    cancellationToken);
                await UpsertSourceMessageAsync(
                    connection,
                    transaction,
                    message,
                    now,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task<UiLocaleMetadata> AddLocaleAsync(
        string locale,
        CancellationToken cancellationToken)
    {
        var metadata = UiTranslationCatalog.ParseLocale(locale);
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "UiLocales" (
                    "Locale", "EnglishName", "NativeName", "Direction",
                    "IsEnabled", "IsSource", "CreatedAt", "UpdatedAt")
                VALUES (
                    @locale, @englishName, @nativeName, @direction,
                    1, @isSource, @now, @now)
                ON CONFLICT("Locale") DO UPDATE SET
                    "EnglishName" = excluded."EnglishName",
                    "NativeName" = excluded."NativeName",
                    "Direction" = excluded."Direction",
                    "IsEnabled" = 1,
                    "UpdatedAt" = excluded."UpdatedAt";
                """;
            Add(command, "@locale", metadata.Locale);
            Add(command, "@englishName", metadata.EnglishName);
            Add(command, "@nativeName", metadata.NativeName);
            Add(command, "@direction", metadata.Direction);
            Add(command, "@isSource", metadata.Locale.Equals(
                UiTranslationCatalog.SourceLocale,
                StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            Add(command, "@now", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken);

            return metadata;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task<IReadOnlyList<UiLocaleSummary>> ListLocalesAsync(
        CancellationToken cancellationToken)
    {
        var total = UiTranslationResources.All.Count;
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var locales = new List<(UiLocaleMetadata Metadata, bool IsSource)>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT "Locale", "EnglishName", "NativeName", "Direction", "IsSource"
                    FROM "UiLocales"
                    WHERE "IsEnabled" = 1
                    ORDER BY "IsSource" DESC, "EnglishName", "Locale";
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    locales.Add((
                        new UiLocaleMetadata(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetString(3)),
                        reader.GetInt32(4) != 0));
                }
            }

            var result = new List<UiLocaleSummary>();
            foreach (var item in locales)
            {
                if (item.IsSource)
                {
                    result.Add(new UiLocaleSummary(
                        item.Metadata.Locale,
                        item.Metadata.EnglishName,
                        item.Metadata.NativeName,
                        item.Metadata.Direction,
                        true,
                        total,
                        total,
                        0,
                        0,
                        total,
                        0,
                        0));
                    continue;
                }

                var counts = await ReadCountsAsync(
                    connection,
                    item.Metadata.Locale,
                    cancellationToken);
                var ready = counts.Generated + counts.Reviewed + counts.Manual;
                var missing = Math.Max(0, total - ready - counts.Outdated);

                result.Add(new UiLocaleSummary(
                    item.Metadata.Locale,
                    item.Metadata.EnglishName,
                    item.Metadata.NativeName,
                    item.Metadata.Direction,
                    false,
                    total,
                    ready,
                    missing,
                    counts.Generated,
                    counts.Reviewed,
                    counts.Manual,
                    counts.Outdated));
            }

            return result;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task<IReadOnlyList<UiTranslationEntry>> GetEntriesAsync(
        string locale,
        string? search,
        CancellationToken cancellationToken)
    {
        var normalized = UiTranslationCatalog.ParseLocale(locale).Locale;
        var translated = new Dictionary<string, TranslationRow>(StringComparer.Ordinal);

        if (!normalized.Equals(UiTranslationCatalog.SourceLocale, StringComparison.OrdinalIgnoreCase))
        {
            var connection = db.Database.GetDbConnection();
            var openedHere = connection.State != ConnectionState.Open;
            if (openedHere)
            {
                await connection.OpenAsync(cancellationToken);
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT "MessageKey", "Text", "Status", "SourceHash",
                           "Provider", "Model", "UpdatedAt"
                    FROM "UiTranslations"
                    WHERE "Locale" = @locale;
                    """;
                Add(command, "@locale", normalized);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var updatedAt = DateTime.TryParse(
                        reader.GetString(6),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var parsed)
                        ? parsed
                        : (DateTime?)null;

                    translated[reader.GetString(0)] = new TranslationRow(
                        reader.GetString(1),
                        ParseStatus(reader.GetString(2)),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        updatedAt);
                }
            }
            finally
            {
                if (openedHere)
                {
                    await connection.CloseAsync();
                }
            }
        }

        IEnumerable<UiMessageDefinition> messages = UiTranslationResources.All;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            messages = messages.Where(x =>
                x.Key.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.DefaultText.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.Feature.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        return messages
            .OrderBy(x => x.Feature, StringComparer.Ordinal)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(message =>
            {
                if (normalized.Equals(UiTranslationCatalog.SourceLocale, StringComparison.OrdinalIgnoreCase))
                {
                    return new UiTranslationEntry(
                        message,
                        message.DefaultText,
                        UiTranslationStatus.Reviewed,
                        "source",
                        null,
                        null);
                }

                if (!translated.TryGetValue(message.Key, out var row))
                {
                    return new UiTranslationEntry(
                        message,
                        null,
                        UiTranslationStatus.Missing,
                        null,
                        null,
                        null);
                }

                var status = row.Status;
                if (status is UiTranslationStatus.Generated or UiTranslationStatus.Reviewed
                    && !string.Equals(row.SourceHash, message.SourceHash, StringComparison.Ordinal))
                {
                    status = UiTranslationStatus.Outdated;
                }

                return new UiTranslationEntry(
                    message,
                    row.Text,
                    status,
                    row.Provider,
                    row.Model,
                    row.UpdatedAt);
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<UiMessageDefinition>> GetPendingDefinitionsAsync(
        string locale,
        bool includeMissing,
        bool includeOutdated,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var entries = await GetEntriesAsync(locale, null, cancellationToken);
        return entries
            .Where(x =>
                (includeMissing && x.Status == UiTranslationStatus.Missing)
                || (includeOutdated && x.Status == UiTranslationStatus.Outdated))
            .Select(x => x.Message)
            .Take(limit)
            .ToArray();
    }

    public async Task SaveGeneratedAsync(
        string locale,
        UiTranslationGenerationResult result,
        CancellationToken cancellationToken)
    {
        var normalized = UiTranslationCatalog.ParseLocale(locale).Locale;
        if (normalized.Equals(UiTranslationCatalog.SourceLocale, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The English source locale is not AI-generated.");
        }

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var generated in result.Translations)
            {
                if (!UiTranslationResources.TryGet(generated.Key, out var message)
                    || !UiTranslationCatalog.IsGeneratedTranslationValid(message, generated.Text))
                {
                    continue;
                }

                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO "UiTranslations" (
                        "Locale", "MessageKey", "Text", "Status", "SourceHash",
                        "Provider", "Model", "PromptVersion", "GeneratedAt",
                        "ReviewedAt", "UpdatedAt")
                    VALUES (
                        @locale, @key, @text, 'Generated', @sourceHash,
                        @provider, @model, @promptVersion, @now,
                        NULL, @now)
                    ON CONFLICT("Locale", "MessageKey") DO UPDATE SET
                        "Text" = excluded."Text",
                        "Status" = 'Generated',
                        "SourceHash" = excluded."SourceHash",
                        "Provider" = excluded."Provider",
                        "Model" = excluded."Model",
                        "PromptVersion" = excluded."PromptVersion",
                        "GeneratedAt" = excluded."GeneratedAt",
                        "ReviewedAt" = NULL,
                        "UpdatedAt" = excluded."UpdatedAt"
                    WHERE "UiTranslations"."Status" <> 'Manual';
                    """;
                Add(command, "@locale", normalized);
                Add(command, "@key", message.Key);
                Add(command, "@text", generated.Text.Trim());
                Add(command, "@sourceHash", message.SourceHash);
                Add(command, "@provider", result.ProviderId);
                Add(command, "@model", result.Model);
                Add(command, "@promptVersion", result.PromptVersion);
                Add(command, "@now", DateTime.UtcNow);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task SaveManualAsync(
        string locale,
        string key,
        string text,
        CancellationToken cancellationToken)
    {
        var normalized = UiTranslationCatalog.ParseLocale(locale).Locale;
        var message = UiTranslationResources.Get(key);
        if (normalized.Equals(UiTranslationCatalog.SourceLocale, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Edit the English source resource in code, not as a translation.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Translation text is required.", nameof(text));
        }

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "UiTranslations" (
                    "Locale", "MessageKey", "Text", "Status", "SourceHash",
                    "Provider", "Model", "PromptVersion", "GeneratedAt",
                    "ReviewedAt", "UpdatedAt")
                VALUES (
                    @locale, @key, @text, 'Manual', @sourceHash,
                    NULL, NULL, NULL, NULL, @now, @now)
                ON CONFLICT("Locale", "MessageKey") DO UPDATE SET
                    "Text" = excluded."Text",
                    "Status" = 'Manual',
                    "SourceHash" = excluded."SourceHash",
                    "Provider" = NULL,
                    "Model" = NULL,
                    "PromptVersion" = NULL,
                    "ReviewedAt" = excluded."ReviewedAt",
                    "UpdatedAt" = excluded."UpdatedAt";
                """;
            Add(command, "@locale", normalized);
            Add(command, "@key", message.Key);
            Add(command, "@text", text.Trim());
            Add(command, "@sourceHash", message.SourceHash);
            Add(command, "@now", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task MarkReviewedAsync(
        string locale,
        string key,
        CancellationToken cancellationToken)
    {
        var normalized = UiTranslationCatalog.ParseLocale(locale).Locale;
        var message = UiTranslationResources.Get(key);
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE "UiTranslations"
                SET "Status" = 'Reviewed',
                    "ReviewedAt" = @now,
                    "UpdatedAt" = @now
                WHERE "Locale" = @locale
                  AND "MessageKey" = @key
                  AND "Status" = 'Generated'
                  AND "SourceHash" = @sourceHash;
                """;
            Add(command, "@locale", normalized);
            Add(command, "@key", key);
            Add(command, "@sourceHash", message.SourceHash);
            Add(command, "@now", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> LoadBundleAsync(
        string locale,
        CancellationToken cancellationToken)
    {
        var bundle = UiTranslationResources.All.ToDictionary(
            x => x.Key,
            x => x.DefaultText,
            StringComparer.Ordinal);

        var chain = UiTranslationCatalog.BuildFallbackChain(locale)
            .Where(x => !x.Equals(
                UiTranslationCatalog.SourceLocale,
                StringComparison.OrdinalIgnoreCase))
            .Reverse()
            .ToArray();

        if (chain.Length == 0)
        {
            return bundle;
        }

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            foreach (var fallbackLocale in chain)
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT "MessageKey", "Text", "Status", "SourceHash"
                    FROM "UiTranslations"
                    WHERE "Locale" = @locale
                      AND "Status" IN ('Generated', 'Reviewed', 'Manual');
                    """;
                Add(command, "@locale", fallbackLocale);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var key = reader.GetString(0);
                    if (!UiTranslationResources.TryGet(key, out var definition))
                    {
                        continue;
                    }

                    var status = ParseStatus(reader.GetString(2));
                    var sourceHash = reader.GetString(3);
                    if (status != UiTranslationStatus.Manual
                        && !string.Equals(sourceHash, definition.SourceHash, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    bundle[key] = reader.GetString(1);
                }
            }

            return bundle;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task<UiLocaleMetadata> GetProfileLocaleAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return UiTranslationCatalog.ParseLocale(
                UiTranslationCatalog.SourceLocale);
        }

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT l."Locale", l."EnglishName", l."NativeName", l."Direction"
                FROM "UiProfileLocales" AS p
                INNER JOIN "UiLocales" AS l ON l."Locale" = p."Locale"
                WHERE p."ProfileId" = @profileId
                  AND l."IsEnabled" = 1
                LIMIT 1;
                """;
            Add(command, "@profileId", profileId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return new UiLocaleMetadata(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3));
            }

            return UiTranslationCatalog.ParseLocale(
                UiTranslationCatalog.SourceLocale);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task SetProfileLocaleAsync(
        string profileId,
        string locale,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("Profile ID is required.", nameof(profileId));
        }

        var metadata = UiTranslationCatalog.ParseLocale(locale);
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using (var exists = connection.CreateCommand())
            {
                exists.CommandText =
                    """
                    SELECT COUNT(*)
                    FROM "UiLocales"
                    WHERE "Locale" = @locale
                      AND "IsEnabled" = 1;
                    """;
                Add(exists, "@locale", metadata.Locale);
                var count = Convert.ToInt32(
                    await exists.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture);
                if (count == 0)
                {
                    throw new InvalidOperationException(
                        $"UI locale {metadata.Locale} has not been added by an Owner.");
                }
            }

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "UiProfileLocales" ("ProfileId", "Locale", "UpdatedAt")
                VALUES (@profileId, @locale, @updatedAt)
                ON CONFLICT("ProfileId") DO UPDATE SET
                    "Locale" = excluded."Locale",
                    "UpdatedAt" = excluded."UpdatedAt";
                """;
            Add(command, "@profileId", profileId);
            Add(command, "@locale", metadata.Locale);
            Add(command, "@updatedAt", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task<UiTextBundle> LoadProfileBundleAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        var locale = await GetProfileLocaleAsync(profileId, cancellationToken);
        return await LoadLocaleBundleAsync(locale, cancellationToken);
    }

    public async Task<UiTextBundle> LoadLocaleBundleAsync(
        UiLocaleMetadata locale,
        CancellationToken cancellationToken)
    {
        var values = await LoadBundleAsync(locale.Locale, cancellationToken);
        return new UiTextBundle(locale.Locale, locale.Direction, values);
    }

    public async Task<IReadOnlyList<UiLocaleMetadata>> ListEnabledLocalesAsync(
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT "Locale", "EnglishName", "NativeName", "Direction"
                FROM "UiLocales"
                WHERE "IsEnabled" = 1
                ORDER BY "IsSource" DESC, "Locale";
                """;

            var result = new List<UiLocaleMetadata>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new UiLocaleMetadata(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }

            return result;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task EnsureSourceLocaleAsync(
        DbConnection connection,
        DbTransaction transaction,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var source = UiTranslationCatalog.ParseLocale(UiTranslationCatalog.SourceLocale);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO "UiLocales" (
                "Locale", "EnglishName", "NativeName", "Direction",
                "IsEnabled", "IsSource", "CreatedAt", "UpdatedAt")
            VALUES (
                @locale, @englishName, @nativeName, @direction,
                1, 1, @now, @now)
            ON CONFLICT("Locale") DO UPDATE SET
                "EnglishName" = excluded."EnglishName",
                "NativeName" = excluded."NativeName",
                "Direction" = excluded."Direction",
                "IsEnabled" = 1,
                "IsSource" = 1,
                "UpdatedAt" = excluded."UpdatedAt";
            """;
        Add(command, "@locale", source.Locale);
        Add(command, "@englishName", source.EnglishName);
        Add(command, "@nativeName", source.NativeName);
        Add(command, "@direction", source.Direction);
        Add(command, "@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkGeneratedTranslationsOutdatedAsync(
        DbConnection connection,
        DbTransaction transaction,
        UiMessageDefinition message,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE "UiTranslations"
            SET "Status" = 'Outdated',
                "UpdatedAt" = @now
            WHERE "MessageKey" = @key
              AND "Status" IN ('Generated', 'Reviewed')
              AND "SourceHash" <> @sourceHash;
            """;
        Add(command, "@key", message.Key);
        Add(command, "@sourceHash", message.SourceHash);
        Add(command, "@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertSourceMessageAsync(
        DbConnection connection,
        DbTransaction transaction,
        UiMessageDefinition message,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO "UiTranslationMessages" (
                "Key", "DefaultText", "Feature", "Surface", "Description",
                "Tone", "MaxLength", "PlaceholdersJson", "DoNotTranslateJson",
                "SourceHash", "SourceVersion", "UpdatedAt")
            VALUES (
                @key, @defaultText, @feature, @surface, @description,
                @tone, @maxLength, @placeholders, @doNotTranslate,
                @sourceHash, 1, @updatedAt)
            ON CONFLICT("Key") DO UPDATE SET
                "DefaultText" = excluded."DefaultText",
                "Feature" = excluded."Feature",
                "Surface" = excluded."Surface",
                "Description" = excluded."Description",
                "Tone" = excluded."Tone",
                "MaxLength" = excluded."MaxLength",
                "PlaceholdersJson" = excluded."PlaceholdersJson",
                "DoNotTranslateJson" = excluded."DoNotTranslateJson",
                "SourceHash" = excluded."SourceHash",
                "UpdatedAt" = excluded."UpdatedAt";
            """;
        Add(command, "@key", message.Key);
        Add(command, "@defaultText", message.DefaultText);
        Add(command, "@feature", message.Feature);
        Add(command, "@surface", message.Surface);
        Add(command, "@description", message.Description);
        Add(command, "@tone", message.Tone);
        Add(command, "@maxLength", message.MaxLength);
        Add(command, "@placeholders", JsonSerializer.Serialize(
            message.Placeholders ?? new Dictionary<string, string>()));
        Add(command, "@doNotTranslate", JsonSerializer.Serialize(
            message.DoNotTranslate ?? []));
        Add(command, "@sourceHash", message.SourceHash);
        Add(command, "@updatedAt", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<TranslationCounts> ReadCountsAsync(
        DbConnection connection,
        string locale,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                SUM(CASE WHEN "Status" = 'Generated' THEN 1 ELSE 0 END),
                SUM(CASE WHEN "Status" = 'Reviewed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN "Status" = 'Manual' THEN 1 ELSE 0 END),
                SUM(CASE WHEN "Status" = 'Outdated' THEN 1 ELSE 0 END)
            FROM "UiTranslations"
            WHERE "Locale" = @locale;
            """;
        Add(command, "@locale", locale);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new TranslationCounts(0, 0, 0, 0);
        }

        return new TranslationCounts(
            reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetInt64(0), CultureInfo.InvariantCulture),
            reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture),
            reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetInt64(2), CultureInfo.InvariantCulture),
            reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetInt64(3), CultureInfo.InvariantCulture));
    }

    private static UiTranslationStatus ParseStatus(string status) =>
        Enum.TryParse<UiTranslationStatus>(status, ignoreCase: true, out var parsed)
            ? parsed
            : UiTranslationStatus.Missing;

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record TranslationRow(
        string Text,
        UiTranslationStatus Status,
        string SourceHash,
        string? Provider,
        string? Model,
        DateTime? UpdatedAt);

    private sealed record TranslationCounts(
        int Generated,
        int Reviewed,
        int Manual,
        int Outdated);
}
