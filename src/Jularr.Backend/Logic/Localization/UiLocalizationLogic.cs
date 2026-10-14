using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Localization;
using Jularr.Backend.Data.Logic.Localization;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Localization
{
    internal static class UiLocalizationLogic
    {
        public static async Task<UiLocaleData> CreateUiLocaleAsync(this LogicContext ctx, UiLocaleCreationData data, CancellationToken ct)
        {
            // Create supported language metadata.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "UiLocales" ("Locale", "Name", "IsRightToLeft")
                VALUES
                    (@Locale, @Name, @IsRightToLeft)
                ON CONFLICT ("Locale") DO NOTHING
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(data);
            return await ctx.ReadOptionalAsync<UiLocaleData>(sql, ct)
                ?? throw new DataValidationException(nameof(data.Locale), "duplicate_value");
        }

        public static async Task UpdateUiLocaleAsync(this LogicContext ctx, UiLocaleUpdateData data, CancellationToken ct)
        {
            // Retain the English fallback.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "UiLocales" AS locale
                SET
                    "Name" = @Name,
                    "IsEnabled" = @IsEnabled,
                    "IsRightToLeft" = @IsRightToLeft,
                    "CatalogVersion" = locale."CatalogVersion" + 1
                WHERE
                    locale."Id" = @Id
                    AND locale."CatalogVersion" = @CatalogVersion
                    AND (lower(locale."Locale") <> 'en' OR @IsEnabled = TRUE);
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.CatalogVersion), "invalid_transition");
            }
        }

        internal static async Task SynchronizeUiResourcesAsync(this LogicContext ctx, IReadOnlyList<UiTextResource> resources, CancellationToken ct)
        {
            foreach (var resource in resources)
            {
                resource.Validate();
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "UiLocales" ("Locale", "Name")
                VALUES
                    ('en', 'English')
                ON CONFLICT ("Locale") DO NOTHING;
                """;
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // Only source English is synchronized; administrator descriptions remain untouched.
            sql = new SqlStatement();
            sql.Statement =
                """
                WITH changed AS (
                    INSERT INTO
                        "UiTranslationMessages" AS message ("Key", "EnglishText")
                    SELECT
                        source."Key",
                        source."EnglishText"
                    FROM
                        unnest(@Keys, @EnglishTexts) AS source("Key", "EnglishText")
                    ON CONFLICT ("Key") DO UPDATE
                    SET
                        "EnglishText" = EXCLUDED."EnglishText",
                        "Version" = message."Version" + 1,
                        "UpdatedAt" = now()
                    WHERE
                        message."EnglishText" IS DISTINCT FROM EXCLUDED."EnglishText"
                    RETURNING
                        "Id",
                        "Version"
                ), outdated AS (
                    UPDATE
                        "UiTranslations" AS translation
                    SET
                        "UiTranslationStatusTypeId" = 5,
                        "Version" = translation."Version" + 1,
                        "UpdatedAt" = now()
                    FROM
                        changed
                    WHERE
                        translation."UiTranslationMessageId" = changed."Id"
                        AND translation."MessageVersion" <> changed."Version"
                        AND translation."UiTranslationStatusTypeId" IN (2, 3)
                    RETURNING
                        translation."Id"
                )
                UPDATE
                    "UiLocales"
                SET
                    "CatalogVersion" = "CatalogVersion" + 1
                WHERE
                    EXISTS (SELECT 1 FROM changed);
                """;
            sql.Parameters.Add(new UiResourceBatchData(resources));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task UpdateUiDescriptionAsync(this LogicContext ctx, UiDescriptionUpdateData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH changed AS (
                    UPDATE
                        "UiTranslationMessages"
                    SET
                        "Description" = @Description,
                        "Version" = "Version" + 1,
                        "UpdatedAt" = now()
                    WHERE
                        "Id" = @UiTranslationMessageId
                        AND "Version" = @MessageVersion
                    RETURNING
                        "Id"
                ), outdated AS (
                    UPDATE
                        "UiTranslations" AS translation
                    SET
                        "UiTranslationStatusTypeId" = 5,
                        "Version" = translation."Version" + 1,
                        "UpdatedAt" = now()
                    FROM
                        changed
                    WHERE
                        translation."UiTranslationMessageId" = changed."Id"
                        AND translation."UiTranslationStatusTypeId" IN (2, 3)
                    RETURNING
                        translation."Id"
                )
                UPDATE
                    "UiLocales"
                SET
                    "CatalogVersion" = "CatalogVersion" + 1
                WHERE
                    EXISTS (SELECT 1 FROM changed);
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 0)
            {
                throw new DataValidationException(nameof(data.MessageVersion), "outdated_version");
            }
        }

        public static async Task SaveUiTranslationAsync(this LogicContext ctx, UiTranslationSaveData data, CancellationToken ct)
        {
            if (data.UiTranslationStatusTypeId is not (UiTranslationStatusType.Generated
                or UiTranslationStatusType.Reviewed or UiTranslationStatusType.Manual))
            {
                throw new DataValidationException(nameof(data.UiTranslationStatusTypeId), "invalid_transition");
            }

            // Validate the target locale.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    locale."Id"
                FROM
                    "UiLocales" AS locale
                WHERE
                    locale."Id" = @UiLocaleId
                    AND locale."IsEnabled" = TRUE;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<UiLocaleData>(sql, ct) is null)
            {
                throw new DataValidationException(nameof(data.UiLocaleId), "unknown_reference");
            }

            // Serialize source changes.
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    message."Key",
                    message."EnglishText" AS "Text"
                FROM
                    "UiTranslationMessages" AS message
                WHERE
                    message."Id" = @UiTranslationMessageId
                    AND message."Version" = @MessageVersion
                FOR UPDATE OF message;
                """;
            sql.Parameters.Add(data);
            var source = await ctx.ReadOptionalAsync<UiCatalogTextData>(sql, ct)
                ?? throw new DataValidationException(nameof(data.MessageVersion), "outdated_version");
            UiTextPlaceholders.RequireMatching(source.Text, data.Text);

            sql = new SqlStatement();
            sql.Statement =
                """
                WITH changed AS (
                    INSERT INTO
                        "UiTranslations" AS translation
                        ("UiLocaleId", "UiTranslationMessageId", "Text", "UiTranslationStatusTypeId", "MessageVersion")
                    SELECT
                        @UiLocaleId,
                        @UiTranslationMessageId,
                        @Text,
                        @UiTranslationStatusTypeId,
                        @MessageVersion
                    WHERE
                        @TranslationVersion IS NULL
                        OR EXISTS (
                            SELECT 1
                            FROM "UiTranslations" AS existing
                            WHERE existing."UiLocaleId" = @UiLocaleId
                                AND existing."UiTranslationMessageId" = @UiTranslationMessageId
                                AND existing."Version" = @TranslationVersion
                        )
                    ON CONFLICT ("UiLocaleId", "UiTranslationMessageId") DO UPDATE
                    SET
                        "Text" = EXCLUDED."Text",
                        "UiTranslationStatusTypeId" = EXCLUDED."UiTranslationStatusTypeId",
                        "MessageVersion" = EXCLUDED."MessageVersion",
                        "Version" = translation."Version" + 1,
                        "UpdatedAt" = now()
                    WHERE
                        (translation."UiTranslationStatusTypeId" <> 4 OR EXCLUDED."UiTranslationStatusTypeId" = 4)
                        AND (EXCLUDED."UiTranslationStatusTypeId" <> 4 OR translation."Version" = @TranslationVersion)
                    RETURNING
                        "UiLocaleId"
                )
                UPDATE
                    "UiLocales" AS locale
                SET
                    "CatalogVersion" = locale."CatalogVersion" + 1
                FROM
                    changed
                WHERE
                    locale."Id" = changed."UiLocaleId";
                """;
            sql.Parameters.Add(data);
            var affected = await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            if (affected == 0 && data.UiTranslationStatusTypeId == UiTranslationStatusType.Manual)
            {
                throw new DataValidationException(nameof(data.TranslationVersion), "outdated_version");
            }
        }

        // A chosen language stays enabled until the choice is saved.
        public static async Task RequireEnabledUiLocaleAsync(this LogicContext ctx, long uiLocaleId, string field, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    locale."Id"
                FROM
                    "UiLocales" AS locale
                WHERE
                    locale."Id" = @UiLocaleId
                    AND locale."IsEnabled" = TRUE
                FOR SHARE OF
                    locale;
                """;
            sql.Parameters.Add(new UiLocaleParameters(uiLocaleId));
            if (await ctx.ReadOptionalAsync<UiLocaleData>(sql, ct) is null)
            {
                throw new DataValidationException(field, "unknown_reference");
            }
        }
    }
}
