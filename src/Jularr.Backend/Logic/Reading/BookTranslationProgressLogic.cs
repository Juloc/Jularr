using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Reading
{
    // How far the book translation is, kept in "BookTranslationProgress" so the progress view reads it instead of parsing every structure and cache. A book is
    // counted again when one of its sections becomes finished or unfinished (LocalReadingPaths.StoreAsync), when it is prepared, cleared or leaves the cache,
    // and when its translations are deleted; the bookTranslations job counts every book again after a start and whenever the languages profiles read change.
    // Rows are written only by RecountBookTranslationProgressAsync, under each book's count lock taken in Id order and after any lock of the book itself.
    internal static class BookTranslationProgressLogic
    {
        // The languages books are translated into: every profile's interface language and the languages chosen for books.
        public static async Task<string[]> ReadBookTranslationLanguagesAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT targets."Language"
                FROM (
                    SELECT DISTINCT lower(split_part(split_part(locale."Locale", '-', 1), '_', 1)) AS "Language"
                    FROM "Profiles" AS profile
                        JOIN "UiLocales" AS locale ON locale."Id" = profile."UiLocaleId"
                    UNION
                    SELECT DISTINCT lower(split_part(split_part(preference."Language", '-', 1), '_', 1))
                    FROM "ReadingPreferences" AS preference
                    WHERE preference."Language" IS NOT NULL AND preference."Language" <> 'original'
                ) AS targets
                WHERE targets."Language" ~ '^[a-z]{2,3}$'
                ORDER BY targets."Language";
                """;
            return [.. (await ctx.ReadSql.ReadListAsync<BookTranslationLanguageData>(sql, ct)).Select(language => language.Language)];
        }

        // The next books of the full count from the given document on, counted again in one step; the document to go on from, none after the last.
        public static async Task<long?> RecountBookTranslationBatchAsync(this LogicContext ctx, BookTranslationBatchData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT document."Id" AS "DocumentId"
                FROM "ReadingDocuments" AS document
                WHERE document."Id" >= @DocumentId
                ORDER BY document."Id"
                LIMIT @Count;
                """;
            sql.Parameters.Add(data);
            long[] batch = [.. (await ctx.Sql.ReadListAsync<ReadingDocumentIdData>(sql, ct)).Select(document => document.DocumentId)];
            if (batch.Length > 0) await ctx.RecountBookTranslationProgressAsync(new BookTranslationRecountData(batch, null), ct);
            return batch.Length < data.Count ? null : batch[^1] + 1;
        }

        // Counts the books again: per language their sections and blocks, and those complete in any model (ModelKey and Digest empty) and in each model and version.
        // Complete means a cache of the section with a text for every block and no earlier version's texts left. A book's counts are written one after the other,
        // so a count never overwrites a later one with what it read before.
        public static async Task RecountBookTranslationProgressAsync(this LogicContext ctx, BookTranslationRecountData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT pg_advisory_xact_lock(hashtextextended('book-translation-progress:' || book.id::text, 0))
                FROM unnest(@DocumentIds::bigint[]) AS book (id)
                ORDER BY book.id;

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
                        lower(split_part(split_part(coalesce(document."Language", work."OriginalLanguage"), '-', 1), '_', 1)) AS "SourceLanguage",
                        jsonb_array_length(document."Structure" -> 'Sections') AS "Sections"
                    FROM
                        "ReadingDocuments" AS document
                        JOIN "StoredFiles" AS file ON file."Id" = document."StoredFileId"
                        JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                        JOIN "Works" AS work ON work."Id" = asset."WorkId"
                    WHERE
                        document."Id" = ANY(@DocumentIds)
                        AND coalesce(document."Language", work."OriginalLanguage") IS NOT NULL
                        AND jsonb_typeof(document."Structure" -> 'Sections') = 'array'
                ),
                sections AS MATERIALIZED (
                    -- The blocks of every section, read from the structure once for each book.
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
                    -- The blocks per section as an array, so a finished section's blocks are one lookup.
                    SELECT
                        section."DocumentId",
                        sum(section."Blocks")::integer AS "Blocks",
                        array_agg(section."Blocks" ORDER BY section."Section") AS "SectionBlocks"
                    FROM sections AS section
                    GROUP BY section."DocumentId"
                ),
                pairs AS (
                    SELECT
                        book."Id" AS "DocumentId",
                        target."Language",
                        book."Sections",
                        coalesce(total."Blocks", 0) AS "Blocks",
                        total."SectionBlocks"
                    FROM
                        books AS book
                        CROSS JOIN targets AS target
                        LEFT JOIN totals AS total ON total."DocumentId" = book."Id"
                    WHERE
                        target."Language" ~ '^[a-z]{2,3}$'
                        AND (@Language IS NULL OR target."Language" = @Language)
                        AND length(book."SourceLanguage") BETWEEN 2 AND 3
                        AND target."Language" <> book."SourceLanguage"
                ),
                finished AS MATERIALIZED (
                    -- Every complete cache of a pair: its section, model and version.
                    SELECT
                        pair."DocumentId",
                        pair."Language",
                        substring(cache."Path" FROM '[0-9]{5}$')::integer AS "Section",
                        split_part(cache."Path", '/', 3) AS "ModelKey",
                        stored.value ->> 'ModelDigest' AS "Digest"
                    FROM
                        pairs AS pair
                        JOIN "ReadingResources" AS cache ON cache."ReadingDocumentId" = pair."DocumentId"
                            AND cache."Path" ~ ('^:localtranslation/v4/[^/]+/' || pair."Language" || '/[0-9]{5}$')
                        -- Parsed once (OFFSET 0), not again for every condition on it.
                        CROSS JOIN LATERAL (
                            SELECT convert_from(cache."Bytes", 'UTF8')::jsonb AS value
                            OFFSET 0
                        ) AS stored
                    WHERE
                        jsonb_typeof(stored.value -> 'Stale') IS DISTINCT FROM 'object'
                        AND NOT EXISTS (
                            SELECT 1 FROM jsonb_array_elements(stored.value -> 'Texts') AS block (value)
                            WHERE block.value = 'null'::jsonb
                        )
                ),
                counted AS (
                    -- Complete in any model: each section once.
                    SELECT
                        pair."DocumentId",
                        pair."Language",
                        ''::text AS "ModelKey",
                        ''::text AS "Digest",
                        pair."Sections",
                        pair."Blocks",
                        count(done."Section")::integer AS "DoneSections",
                        coalesce(sum(pair."SectionBlocks"[done."Section" + 1]), 0)::integer AS "DoneBlocks"
                    FROM
                        pairs AS pair
                        LEFT JOIN (
                            SELECT DISTINCT finished."DocumentId", finished."Language", finished."Section"
                            FROM finished
                        ) AS done ON done."DocumentId" = pair."DocumentId" AND done."Language" = pair."Language"
                    GROUP BY pair."DocumentId", pair."Language", pair."Sections", pair."Blocks"
                    UNION ALL
                    -- Complete in one model and version.
                    SELECT
                        pair."DocumentId",
                        pair."Language",
                        done."ModelKey",
                        done."Digest",
                        pair."Sections",
                        pair."Blocks",
                        count(*)::integer,
                        coalesce(sum(pair."SectionBlocks"[done."Section" + 1]), 0)::integer
                    FROM
                        finished AS done
                        JOIN pairs AS pair ON pair."DocumentId" = done."DocumentId" AND pair."Language" = done."Language"
                    WHERE
                        length(done."ModelKey") BETWEEN 1 AND 80
                        AND length(done."Digest") BETWEEN 1 AND 80
                    GROUP BY pair."DocumentId", pair."Language", pair."Sections", pair."Blocks", done."ModelKey", done."Digest"
                ),
                removed AS (
                    DELETE FROM
                        "BookTranslationProgress" AS progress
                    WHERE
                        progress."ReadingDocumentId" = ANY(@DocumentIds)
                        AND (@Language IS NULL OR progress."Language" = @Language)
                        AND NOT EXISTS (
                            SELECT 1
                            FROM counted
                            WHERE counted."DocumentId" = progress."ReadingDocumentId"
                                AND counted."Language" = progress."Language"
                                AND counted."ModelKey" = progress."ModelKey"
                                AND counted."Digest" = progress."Digest"
                        )
                )
                INSERT INTO
                    "BookTranslationProgress" AS progress ("ReadingDocumentId", "Language", "ModelKey", "Digest", "Sections", "DoneSections", "Blocks", "DoneBlocks")
                SELECT
                    counted."DocumentId",
                    counted."Language",
                    counted."ModelKey",
                    counted."Digest",
                    counted."Sections",
                    counted."DoneSections",
                    counted."Blocks",
                    counted."DoneBlocks"
                FROM
                    counted
                ON CONFLICT ("ReadingDocumentId", "Language", "ModelKey", "Digest") DO UPDATE
                SET
                    "Sections" = EXCLUDED."Sections",
                    "DoneSections" = EXCLUDED."DoneSections",
                    "Blocks" = EXCLUDED."Blocks",
                    "DoneBlocks" = EXCLUDED."DoneBlocks",
                    "UpdatedAt" = now()
                WHERE
                    (progress."Sections", progress."DoneSections", progress."Blocks", progress."DoneBlocks")
                        IS DISTINCT FROM (EXCLUDED."Sections", EXCLUDED."DoneSections", EXCLUDED."Blocks", EXCLUDED."DoneBlocks");
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
