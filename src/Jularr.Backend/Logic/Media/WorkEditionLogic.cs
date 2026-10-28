using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Integrations.Data.OpenLibrary;

namespace Jularr.Backend.Logic.Media
{
    // A book's editions as its catalog knows them: one per language and format, the one with an ISBN and the latest year first.
    internal static class WorkEditionLogic
    {
        private const int MaximumEditions = 20;

        public static EditionFormat Format(string? physicalFormat) => physicalFormat?.ToLowerInvariant() switch
        {
            { } format when format.Contains("ebook", StringComparison.Ordinal) || format.Contains("e-book", StringComparison.Ordinal) || format.Contains("electronic", StringComparison.Ordinal)
                => EditionFormat.Ebook,
            { } format when format.Contains("audio", StringComparison.Ordinal) => EditionFormat.Audiobook,
            _ => EditionFormat.Print
        };

        // The chosen editions, each with the language code readers know ("de" for "ger").
        public static IReadOnlyList<(OpenLibraryEdition Edition, string Language, EditionFormat Format)> Choose(IEnumerable<OpenLibraryEdition> editions) =>
            [.. editions.Where(edition => edition.Language is not null)
                .Select(edition => (Edition: edition, Language: LanguageCodes.ToTwoLetter(edition.Language) ?? edition.Language!, Format: Format(edition.PhysicalFormat)))
                .GroupBy(item => (item.Language, item.Format))
                .Select(group => group.OrderByDescending(item => item.Edition.Isbns.Count > 0).ThenByDescending(item => item.Edition.Year ?? 0).ThenBy(item => item.Edition.EditionId, StringComparer.Ordinal).First())
                .OrderBy(item => item.Language, StringComparer.Ordinal).ThenBy(item => item.Format)
                .Take(MaximumEditions)];

        public static WorkEditionsData Data(long workId, IEnumerable<(OpenLibraryEdition Edition, string Language, EditionFormat Format)> editions) =>
            new(workId, JsonSerializer.Serialize(editions.Select(item => new
            {
                LanguageCode = item.Language,
                EditionFormatId = (int)item.Format,
                Title = TextLimit.Cut(item.Edition.Title, 500),
                Publisher = TextLimit.Cut(item.Edition.Publisher, 300),
                ReleaseYear = item.Edition.Year is >= 1400 and <= 2200 ? item.Edition.Year : null,
                Identities = item.Edition.Isbns.Select(isbn => new { Namespace = "isbn", Value = isbn })
                    .Append(new { Namespace = "openlibrary", Value = item.Edition.EditionId })
            })));

        public static async Task SaveWorkEditionsAsync(this LogicContext ctx, WorkEditionsData data, CancellationToken ct)
        {
            // Catalog editions are found again by language and format; their identities are the catalog's.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkEditions" ("WorkId", "LanguageCode", "EditionFormatId", "Title", "Publisher", "ReleaseYear")
                SELECT
                    @Id,
                    item."LanguageCode",
                    item."EditionFormatId",
                    item."Title",
                    item."Publisher",
                    item."ReleaseYear"
                FROM
                    jsonb_to_recordset(@Editions::jsonb) AS item ("LanguageCode" text, "EditionFormatId" smallint, "Title" text, "Publisher" text, "ReleaseYear" smallint)
                ON CONFLICT ("WorkId", "Name", "LanguageCode", "EditionFormatId") DO UPDATE
                SET
                    "Title" = EXCLUDED."Title",
                    "Publisher" = EXCLUDED."Publisher",
                    "ReleaseYear" = EXCLUDED."ReleaseYear";

                DELETE FROM
                    "WorkEditionExternalIdentities" AS identity
                USING
                    "WorkEditions" AS edition
                WHERE
                    edition."Id" = identity."WorkEditionId"
                    AND edition."WorkId" = @Id
                    AND (edition."LanguageCode" IS NOT NULL OR edition."EditionFormatId" IS NOT NULL);

                INSERT INTO
                    "WorkEditionExternalIdentities" ("WorkEditionId", "Namespace", "Value")
                SELECT
                    edition."Id",
                    identity."Namespace",
                    identity."Value"
                FROM
                    jsonb_to_recordset(@Editions::jsonb) AS item ("LanguageCode" text, "EditionFormatId" smallint, "Identities" jsonb)
                    JOIN "WorkEditions" AS edition ON edition."WorkId" = @Id AND edition."Name" IS NULL AND edition."LanguageCode" = item."LanguageCode"
                        AND edition."EditionFormatId" = item."EditionFormatId"
                    CROSS JOIN LATERAL jsonb_to_recordset(item."Identities") AS identity ("Namespace" text, "Value" text)
                ON CONFLICT DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // A list's ISBN names an edition whose language/format are unknown. Do not guess either or overwrite catalog editions.
        public static async Task SaveManualBookIsbnAsync(this LogicContext ctx, WorkIdentityData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH edition AS (
                    INSERT INTO "WorkEditions" ("WorkId", "Title", "ReleaseYear")
                    SELECT work."Id", work."CanonicalTitle", work."ReleaseYear" FROM "Works" AS work WHERE work."Id" = @WorkId
                    ON CONFLICT ("WorkId", "Name", "LanguageCode", "EditionFormatId") DO UPDATE SET "WorkId" = EXCLUDED."WorkId"
                    RETURNING "Id"
                )
                INSERT INTO "WorkEditionExternalIdentities" ("WorkEditionId", "Namespace", "Value")
                SELECT "Id", @Namespace, @Value FROM edition
                ON CONFLICT DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
