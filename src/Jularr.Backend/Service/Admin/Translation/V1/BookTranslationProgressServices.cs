using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Translation.V1;

namespace Jularr.Backend.Service.Admin.Translation.V1
{
    // How far the job "Book translations" is: sections translated completely per language and book, the section it works on, its pace and why it waits.
    [ServiceEndpoint("GET", "/api/v1/admin/translation/progress")]
    public sealed class ReadBookTranslationProgressV1 : AdminService<BookTranslationProgressParametersV1>
    {
        private const int ShownBooks = 400;
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
            // The counts kept per book and language ("BookTranslationProgress"): finished means complete in the model and version the job translates with,
            // and before its first round since the start complete in any model.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    progress."ReadingDocumentId" AS "DocumentId",
                    work."Id" AS "WorkId",
                    work."CanonicalTitle" AS "Title",
                    trim_scale(unit."Number")::text AS "Volume",
                    progress."Language",
                    progress."Sections" AS "Total",
                    CASE WHEN @Model IS NULL THEN progress."DoneSections" ELSE coalesce(model."DoneSections", 0) END AS "Done",
                    progress."Blocks" AS "TotalBlocks",
                    CASE WHEN @Model IS NULL THEN progress."DoneBlocks" ELSE coalesce(model."DoneBlocks", 0) END AS "DoneBlocks"
                FROM
                    "BookTranslationProgress" AS progress
                    JOIN "ReadingDocuments" AS document ON document."Id" = progress."ReadingDocumentId"
                    JOIN "StoredFiles" AS file ON file."Id" = document."StoredFileId"
                    JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                    JOIN "Works" AS work ON work."Id" = asset."WorkId"
                    LEFT JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
                    LEFT JOIN "BookTranslationProgress" AS model ON model."ReadingDocumentId" = progress."ReadingDocumentId"
                        AND model."Language" = progress."Language"
                        AND model."ModelKey" = @Model
                        AND model."Digest" = @Digest
                WHERE
                    progress."ModelKey" = ''
                    AND progress."Digest" = '';
                """;
            var snapshot = _progress.Snapshot();
            sql.Parameters.Add(new BookTranslationCurrentModelData(snapshot.ModelKey, snapshot.Digest));
            var rows = await Context.ReadSql.ReadListAsync<BookTranslationProgressRowData>(sql, CancellationToken);

            var languages = rows.GroupBy(row => row.Language).OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new BookTranslationLanguageV1(group.Key, group.Sum(row => row.Done), group.Sum(row => row.Total))).ToArray();
            // The books being translated first, then those still to start, then the finished ones.
            var books = rows.GroupBy(row => row.DocumentId).Select(group => new BookTranslationBookV1(group.First().WorkId, group.First().Title, group.First().Volume, group.Sum(row => row.Done), group.Sum(row => row.Total)))
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
            return new BookTranslationProgressV1(languages, books, current, snapshot.BlocksPerMinute, secondsRemaining, remainingSections == 0 ? null : snapshot.IssueKey, snapshot.Counting);
        }
    }

    // The books the server translated, per language: how many sections have a server text for every block (from any model, as the translated EPUB mixes
    // them), and per model how many sections it has done and how many still show an earlier version of it. A section counts as stale while its cache keeps
    // an earlier version's texts, or when it is from the model the job translates with in another version.
    [ServiceEndpoint("GET", "/api/v1/admin/translation/books")]
    public sealed class ReadTranslatedBooksV1 : AdminService<TranslatedBooksParametersV1>
    {
        private readonly BookTranslationProgress _progress;

        internal ReadTranslatedBooksV1(BookTranslationProgress progress)
        {
            _progress = progress;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<TranslatedBookV1>>()];
        protected override ModuleRequirement GetInstanceModules(TranslatedBooksParametersV1 parameters) => ModuleRequirement.All("Translation");
        protected override ServicePermission GetPermission(TranslatedBooksParametersV1 parameters, Type resultType) => new("instance.settings.read");
        protected override PageRequest GetPageRequest(TranslatedBooksParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH caches AS (
                    SELECT
                        cache."ReadingDocumentId" AS "DocumentId",
                        split_part(cache."Path", '/', 4) AS "Language",
                        split_part(cache."Path", '/', 3) AS "Model",
                        jsonb_array_length(stored.value -> 'Texts') > 0 AS "HasText",
                        NOT EXISTS (
                            SELECT 1 FROM jsonb_array_elements(stored.value -> 'Texts') WITH ORDINALITY AS block (value, position)
                            WHERE block.value = 'null'::jsonb AND coalesce(stored.value -> 'Stale' -> 'Texts' -> (block.position::integer - 1), 'null'::jsonb) = 'null'::jsonb
                        ) AS "IsDone",
                        jsonb_typeof(stored.value -> 'Stale') = 'object'
                            OR (split_part(cache."Path", '/', 3) = @Model AND stored.value ->> 'ModelDigest' IS DISTINCT FROM @Digest) AS "IsStale"
                    FROM
                        "ReadingResources" AS cache
                        -- Parsed once (OFFSET 0), not again for every column read from it.
                        CROSS JOIN LATERAL (
                            SELECT convert_from(cache."Bytes", 'UTF8')::jsonb AS value
                            OFFSET 0
                        ) AS stored
                    WHERE
                        cache."Path" ~ '^:localtranslation/v4/[^/]+/[a-z]{2,3}/[0-9]{5}$'
                ),
                visible AS (
                    -- A section is translated once every block has a text from some model.
                    SELECT
                        cache."ReadingDocumentId" AS "DocumentId",
                        split_part(cache."Path", '/', 4) AS "Language",
                        max(block."Blocks") AS "Blocks",
                        count(DISTINCT block.position) FILTER (WHERE block."IsVisible") AS "Visible"
                    FROM
                        "ReadingResources" AS cache
                        -- Each stored section is parsed once (OFFSET 0), not again for every block of it.
                        CROSS JOIN LATERAL (
                            SELECT
                                jsonb_array_length(stored.value -> 'Texts') AS "Blocks",
                                block.position,
                                block.value <> 'null'::jsonb OR coalesce(stored.value -> 'Stale' -> 'Texts' -> (block.position::integer - 1), 'null'::jsonb) <> 'null'::jsonb AS "IsVisible"
                            FROM
                                (SELECT convert_from(cache."Bytes", 'UTF8')::jsonb AS value OFFSET 0) AS stored
                                CROSS JOIN LATERAL jsonb_array_elements(stored.value -> 'Texts') WITH ORDINALITY AS block (value, position)
                            OFFSET 0
                        ) AS block
                    WHERE
                        cache."Path" ~ '^:localtranslation/v4/[^/]+/[a-z]{2,3}/[0-9]{5}$'
                    GROUP BY
                        cache."ReadingDocumentId",
                        split_part(cache."Path", '/', 4),
                        split_part(cache."Path", '/', 5)
                ),
                translated AS (
                    SELECT
                        section."DocumentId",
                        section."Language",
                        (count(*) FILTER (WHERE section."Visible" >= section."Blocks"))::integer AS "Translated"
                    FROM
                        visible AS section
                    GROUP BY
                        section."DocumentId",
                        section."Language"
                ),
                models AS (
                    SELECT
                        cache."DocumentId",
                        cache."Language",
                        cache."Model",
                        (count(*) FILTER (WHERE cache."HasText" AND cache."IsDone"))::integer AS "Done",
                        (count(*) FILTER (WHERE cache."HasText" AND cache."IsStale"))::integer AS "Stale"
                    FROM
                        caches AS cache
                    GROUP BY
                        cache."DocumentId",
                        cache."Language",
                        cache."Model"
                )
                SELECT
                    document."Id" AS "DocumentId",
                    work."Id" AS "WorkId",
                    work."CanonicalTitle" AS "Title",
                    trim_scale(unit."Number")::text AS "Volume",
                    model."Language",
                    total."Sections",
                    coalesce(translated."Translated", 0) AS "Translated",
                    array_agg(model."Model" ORDER BY model."Done" DESC, model."Model") AS "Models",
                    array_agg(model."Done" ORDER BY model."Done" DESC, model."Model") AS "Done",
                    array_agg(model."Stale" ORDER BY model."Done" DESC, model."Model") AS "Stale"
                FROM
                    models AS model
                    JOIN "ReadingDocuments" AS document ON document."Id" = model."DocumentId"
                    JOIN "StoredFiles" AS file ON file."Id" = document."StoredFileId"
                    JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                    JOIN "Works" AS work ON work."Id" = asset."WorkId"
                    LEFT JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
                    LEFT JOIN translated ON translated."DocumentId" = model."DocumentId" AND translated."Language" = model."Language"
                    CROSS JOIN LATERAL (
                        SELECT count(*)::integer AS "Sections"
                        FROM jsonb_array_elements(document."Structure" -> 'Sections') AS section
                        WHERE (section ->> 'Blocks')::integer > 0
                    ) AS total
                WHERE
                    total."Sections" > 0
                GROUP BY
                    document."Id",
                    work."Id",
                    work."CanonicalTitle",
                    unit."Number",
                    model."Language",
                    total."Sections",
                    translated."Translated"
                ORDER BY
                    work."CanonicalTitle",
                    unit."Number" NULLS FIRST,
                    document."Id",
                    model."Language"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            var snapshot = _progress.Snapshot();
            sql.Parameters.Add(new BookTranslationCurrentModelData(snapshot.ModelKey, snapshot.Digest));
            sql.Page = Page;
            var books = await Context.ReadSql.ReadPageAsync<TranslatedBookV1>(sql, CancellationToken);

            // The caches name Ollama's models without their provider; the list names every model by its id.
            return new PageResult<TranslatedBookV1>([.. books.Items.Select(book => new TranslatedBookV1(book.DocumentId, book.WorkId, book.Title, book.Volume, book.Language, book.Sections, book.Translated,
                [.. book.Models.Select(LocalReadingPaths.ModelId)], book.Done, book.Stale))], Page);
        }
    }
}
