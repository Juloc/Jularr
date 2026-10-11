using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Public.Localization.V1
{
    [ServiceEndpoint("GET", "/api/v1/localization/texts/{uiLocaleId:long}")]
    public sealed class ReadUiTextsV1 : PublicService<ReadUiTextsParametersV1>
    {
        private readonly UiLocalizationCache _cache;

        public ReadUiTextsV1(UiLocalizationCache cache)
        {
            _cache = cache;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() =>
            [ServiceResultType.Default<ReadUiTextsResultV1>()];

        protected override ModuleRequirement GetInstanceModules(ReadUiTextsParametersV1 parameters) =>
            ModuleRequirement.None;

        protected override async Task<IServiceOutput> OnRead()
        {
            return await _cache.GetOrLoadAsync(Parameters.UiLocaleId, async () =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    WITH RECURSIVE language_codes AS (
                        SELECT
                            "Locale"::text AS "Code",
                            0 AS "Priority"
                        FROM
                            "UiLocales"
                        WHERE
                            "Id" = @UiLocaleId
                        UNION ALL
                        SELECT
                            regexp_replace("Code", '-[^-]*$', ''),
                            "Priority" + 1
                        FROM
                            language_codes
                        WHERE
                            strpos("Code", '-') > 0
                    ), fallback_locales AS (
                        SELECT
                            locale."Id",
                            locale."Locale",
                            locale."IsRightToLeft",
                            locale."CatalogVersion",
                            code."Priority"
                        FROM
                            language_codes AS code
                        INNER JOIN "UiLocales" AS locale
                            ON lower(locale."Locale") = lower(code."Code")
                        WHERE
                            locale."IsEnabled" = TRUE
                            AND lower(locale."Locale") <> 'en'
                        UNION ALL
                        SELECT
                            "Id",
                            "Locale",
                            "IsRightToLeft",
                            "CatalogVersion",
                            100 AS "Priority"
                        FROM
                            "UiLocales"
                        WHERE
                            lower("Locale") = 'en'
                    ), selected_locale AS (
                        SELECT
                            "Id",
                            "Locale",
                            "IsRightToLeft"
                        FROM
                            fallback_locales
                        ORDER BY
                            "Priority"
                        LIMIT 1
                    ), translations AS (
                        SELECT DISTINCT ON (translation."UiTranslationMessageId")
                            translation."UiTranslationMessageId",
                            translation."Text"
                        FROM
                            fallback_locales AS locale
                        INNER JOIN "UiTranslations" AS translation
                            ON translation."UiLocaleId" = locale."Id"
                        INNER JOIN "UiTranslationMessages" AS message
                            ON message."Id" = translation."UiTranslationMessageId"
                        WHERE
                            lower(locale."Locale") <> 'en'
                            AND (translation."UiTranslationStatusTypeId" = 4
                                OR (translation."UiTranslationStatusTypeId" IN (2, 3)
                                    AND translation."MessageVersion" = message."Version"))
                        ORDER BY
                            translation."UiTranslationMessageId",
                            locale."Priority"
                    )
                    SELECT
                        selected."Id" AS "UiLocaleId",
                        selected."Locale",
                        selected."IsRightToLeft",
                        (
                            SELECT string_agg(
                                "Id"::text || ':' || "CatalogVersion"::text || ':' || "Locale" || ':' || "IsRightToLeft"::text,
                                '/' ORDER BY "Priority")
                            FROM fallback_locales
                        ) AS "Version",
                        (
                            SELECT COALESCE(jsonb_object_agg(message."Key", COALESCE(translation."Text", message."EnglishText")), '{}'::jsonb)
                            FROM "UiTranslationMessages" AS message
                            LEFT JOIN translations AS translation
                                ON translation."UiTranslationMessageId" = message."Id"
                        ) AS "Texts"
                    FROM
                        selected_locale AS selected;
                    """;
                sql.Parameters.Add(Parameters);

                // One snapshot and one SELECT return the entire resolved language pack.
                return await Context.ReadRequiredAsync<ReadUiTextsResultV1>(sql, CancellationToken);
            }, CancellationToken);
        }
    }
}
