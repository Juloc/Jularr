using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Localization;
using Jularr.Backend.Data.Logic.Localization;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Localization
{
    internal static class UiLocalizationLogic
    {
        internal static async Task SynchronizeUiResourcesAsync(this LogicContext context,
            IReadOnlyList<UiTextResource> resources, CancellationToken cancellationToken)
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
            await context.ExecuteAsyncForAffectedRows(sql, cancellationToken);

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
            await context.ExecuteAsyncForAffectedRows(sql, cancellationToken);
        }

        public static async Task UpdateUiDescriptionAsync(this LogicContext context,
            UiDescriptionUpdateData data, CancellationToken cancellationToken)
        {
            data.Validate();
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
            if (await context.ExecuteAsyncForAffectedRows(sql, cancellationToken) == 0)
            {
                throw new DataValidationException(nameof(data.MessageVersion), "outdated_version");
            }
        }

        // Future administrator services own authorization. This writer never calls an AI provider.
        public static async Task SaveUiTranslationAsync(this LogicContext context,
            UiTranslationSaveData data, CancellationToken cancellationToken)
        {
            data.Validate();
            if (data.UiTranslationStatusTypeId is not (UiTranslationStatusType.Generated
                or UiTranslationStatusType.Reviewed or UiTranslationStatusType.Manual))
            {
                throw new DataValidationException(nameof(data.UiTranslationStatusTypeId), "invalid_transition");
            }

            // Serialize edits with source/context changes, including startup synchronization.
            var sql = new SqlStatement();
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
            var source = await context.ReadRequiredAsync<UiCatalogTextData>(sql, cancellationToken);
            UiTextPlaceholders.RequireMatching(source.Text, data.Text);

            sql = new SqlStatement();
            sql.Statement =
                """
                WITH changed AS (
                    INSERT INTO
                        "UiTranslations" AS translation
                        ("UiLocaleId", "UiTranslationMessageId", "Text", "UiTranslationStatusTypeId", "MessageVersion")
                    VALUES
                        (@UiLocaleId, @UiTranslationMessageId, @Text, @UiTranslationStatusTypeId, @MessageVersion)
                    ON CONFLICT ("UiLocaleId", "UiTranslationMessageId") DO UPDATE
                    SET
                        "Text" = EXCLUDED."Text",
                        "UiTranslationStatusTypeId" = EXCLUDED."UiTranslationStatusTypeId",
                        "MessageVersion" = EXCLUDED."MessageVersion",
                        "Version" = translation."Version" + 1,
                        "UpdatedAt" = now()
                    WHERE
                        translation."UiTranslationStatusTypeId" <> 4
                        OR EXCLUDED."UiTranslationStatusTypeId" = 4
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
            await context.ExecuteAsyncForAffectedRows(sql, cancellationToken);
        }
    }
}
