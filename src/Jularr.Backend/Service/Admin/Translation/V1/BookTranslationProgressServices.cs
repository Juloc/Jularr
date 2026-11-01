using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Translation.V1
{
    // How far the job "Book translations" is: sections translated completely per language and book, the section it works on, its pace and why it waits.
    [ServiceEndpoint("GET", "/api/v1/admin/translation/progress")]
    public sealed class ReadBookTranslationProgressV1 : AdminService<BookTranslationProgressParametersV1>
    {
        private const int ShownBooks = 40;
        private readonly BookTranslationProgress _progress;

        internal ReadBookTranslationProgressV1(BookTranslationProgress progress)
        {
            _progress = progress;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BookTranslationProgressV1>()];
        protected override ModuleRequirement GetInstanceModules(BookTranslationProgressParametersV1 parameters) => ModuleRequirement.All("Translation");
        protected override ServicePermission GetPermission(BookTranslationProgressParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH targets AS (
                    SELECT DISTINCT lower(split_part(split_part(locale."Locale", '-', 1), '_', 1)) AS "Language"
                    FROM "Profiles" AS profile
                        JOIN "UiLocales" AS locale ON locale."Id" = profile."UiLocaleId"
                    UNION
                    SELECT DISTINCT lower(split_part(split_part(preference."Language", '-', 1), '_', 1))
                    FROM "ReadingPreferences" AS preference
                    WHERE preference."Language" IS NOT NULL AND preference."Language" <> 'original'
                ),
                books AS (
                    SELECT
                        document."Id",
                        work."CanonicalTitle" AS "Title",
                        trim_scale(unit."Number")::text AS "Volume",
                        lower(split_part(split_part(coalesce(document."Language", work."OriginalLanguage"), '-', 1), '_', 1)) AS "SourceLanguage",
                        jsonb_array_length(document."Structure" -> 'Sections') AS "Sections"
                    FROM
                        "ReadingDocuments" AS document
                        JOIN "StoredFiles" AS file ON file."Id" = document."StoredFileId"
                        JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                        JOIN "Works" AS work ON work."Id" = asset."WorkId"
                        LEFT JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
                    WHERE
                        coalesce(document."Language", work."OriginalLanguage") IS NOT NULL
                        AND jsonb_typeof(document."Structure" -> 'Sections') = 'array'
                )
                SELECT
                    book."Id" AS "DocumentId",
                    book."Title",
                    book."Volume",
                    target."Language",
                    book."Sections" AS "Total",
                    (
                        SELECT count(DISTINCT substring(cache."Path" FROM '[0-9]{5}$'))::integer
                        FROM "ReadingResources" AS cache
                        WHERE cache."ReadingDocumentId" = book."Id"
                            AND cache."Path" ~ ('^:localtranslation/v4/[^/]+/' || target."Language" || '/[0-9]{5}$')
                            AND NOT EXISTS (
                                SELECT 1 FROM jsonb_array_elements(convert_from(cache."Bytes", 'UTF8')::jsonb -> 'Texts') AS block (value)
                                WHERE block.value = 'null'::jsonb
                            )
                    ) AS "Done"
                FROM
                    books AS book
                    CROSS JOIN targets AS target
                WHERE
                    length(target."Language") BETWEEN 2 AND 3
                    AND length(book."SourceLanguage") BETWEEN 2 AND 3
                    AND target."Language" <> book."SourceLanguage";
                """;
            var rows = await Context.ReadSql.ReadListAsync<BookTranslationProgressRowData>(sql, CancellationToken);
            var snapshot = _progress.Snapshot();

            var languages = rows.GroupBy(row => row.Language).OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new BookTranslationLanguageV1(group.Key, group.Sum(row => row.Done), group.Sum(row => row.Total))).ToArray();
            // The books being translated first, then those still to start, then the finished ones.
            var books = rows.GroupBy(row => row.DocumentId).Select(group => new BookTranslationBookV1(group.First().Title, group.First().Volume, group.Sum(row => row.Done), group.Sum(row => row.Total)))
                .OrderBy(book => book.Done >= book.Total ? 2 : book.Done > 0 ? 0 : 1).ThenByDescending(book => book.Total == 0 ? 0 : (double)book.Done / book.Total)
                .ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(book => decimal.TryParse(book.Volume, global::System.Globalization.NumberStyles.Number, global::System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0m)
                .Take(ShownBooks).ToArray();

            BookTranslationCurrentV1? current = null;
            if (snapshot.Current is { } unit && rows.FirstOrDefault(row => row.DocumentId == unit.DocumentId) is { } row0)
                current = new BookTranslationCurrentV1(row0.Title, row0.Volume, unit.Section + 1, unit.Language, snapshot.SecondsWithoutProgress ?? 0);

            var remaining = rows.Sum(row => (long)Math.Max(0, row.Total - row.Done));
            long? secondsRemaining = remaining > 0 && snapshot.SectionsPerMinute > 0 ? (long)Math.Round(remaining / snapshot.SectionsPerMinute * 60) : null;
            return new BookTranslationProgressV1(languages, books, current, snapshot.BlocksPerMinute, secondsRemaining, remaining == 0 ? null : snapshot.IssueKey);
        }
    }
}
