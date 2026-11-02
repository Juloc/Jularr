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
                , sections AS MATERIALIZED (
                    -- The blocks of every section, read from the structure once for each book: a structure is megabytes, and picking one section out of it again and again is slow.
                    SELECT
                        book."Id" AS "DocumentId",
                        (section.ordinal - 1)::integer AS "Section",
                        coalesce(section.value ->> 'Blocks', '0')::integer AS "Blocks"
                    FROM
                        books AS book
                        JOIN "ReadingDocuments" AS document ON document."Id" = book."Id"
                        CROSS JOIN LATERAL jsonb_array_elements(document."Structure" -> 'Sections') WITH ORDINALITY AS section (value, ordinal)
                ),
                totals AS (
                    SELECT section."DocumentId", sum(section."Blocks")::integer AS "TotalBlocks" FROM sections AS section GROUP BY section."DocumentId"
                )
                SELECT
                    book."Id" AS "DocumentId",
                    book."Title",
                    book."Volume",
                    target."Language",
                    book."Sections" AS "Total",
                    finished."Done",
                    coalesce(total."TotalBlocks", 0) AS "TotalBlocks",
                    finished."DoneBlocks"
                FROM
                    books AS book
                    CROSS JOIN targets AS target
                    CROSS JOIN LATERAL (
                        SELECT
                            count(*)::integer AS "Done",
                            coalesce(sum(section."Blocks"), 0)::integer AS "DoneBlocks"
                        FROM (
                            SELECT substring(cache."Path" FROM '[0-9]{5}$')::integer AS "Section"
                            FROM "ReadingResources" AS cache
                            WHERE cache."ReadingDocumentId" = book."Id"
                                AND cache."Path" ~ ('^:localtranslation/v4/[^/]+/' || target."Language" || '/[0-9]{5}$')
                                AND NOT EXISTS (
                                    SELECT 1 FROM jsonb_array_elements(convert_from(cache."Bytes", 'UTF8')::jsonb -> 'Texts') AS block (value)
                                    WHERE block.value = 'null'::jsonb
                                )
                            GROUP BY 1
                        ) AS keys
                        LEFT JOIN sections AS section ON section."DocumentId" = book."Id" AND section."Section" = keys."Section"
                    ) AS finished
                    LEFT JOIN totals AS total ON total."DocumentId" = book."Id"
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

            // What is left in blocks over the blocks per minute of the last ten minutes: sections differ too much in size (a picture, a chapter of a thousand blocks) to count them.
            // Blocks without text cost nothing, so the time is a rough upper bound; it needs a pace of at least one block a minute to say anything.
            var remainingSections = rows.Sum(row => (long)Math.Max(0, row.Total - row.Done));
            var remainingBlocks = rows.Sum(row => (long)Math.Max(0, row.TotalBlocks - row.DoneBlocks));
            long? secondsRemaining = remainingSections > 0 && snapshot.BlocksPerMinute >= 1 ? (long)Math.Round(remainingBlocks / snapshot.BlocksPerMinute * 60) : null;
            return new BookTranslationProgressV1(languages, books, current, snapshot.BlocksPerMinute, secondsRemaining, remainingSections == 0 ? null : snapshot.IssueKey);
        }
    }
}
