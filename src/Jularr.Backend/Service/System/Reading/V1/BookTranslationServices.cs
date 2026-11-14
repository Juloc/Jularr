using System.Net.Http;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Translation.V1;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.System.Reading.V1
{
    // Translates the library ahead of reading with the local TranslateGemma model, into every language a profile uses (its
    // interface language and the languages chosen for books), into the same cache the Reader fills while reading. Books someone
    // is reading come first, from the chapter they are in; passages a reader re-translated with another model come before them, with that model.
    // A few blocks per call; returns whether there was work.
    // A reader's own request comes first: the job waits behind it for its provider's lane, and while the model is busy the round ends and the job looks again later.
    public sealed class TranslateReadingBooksV1 : SystemService<BookTranslationParametersV1, NoData>
    {
        private const int BlocksPerCall = 6;
        private const int BooksPerCount = 25;
        private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(2);
        private readonly TranslationProviders _providers;
        private readonly TranslationLanes _lanes;
        private readonly BookTranslationProgress _progress;

        internal TranslateReadingBooksV1(TranslationProviders providers, TranslationLanes lanes, BookTranslationProgress progress)
        {
            _providers = providers;
            _lanes = lanes;
            _progress = progress;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(BookTranslationParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(BookTranslationParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var counting = await CountAsync();
            var translated = await TranslateNextAsync();
            return new UpdateResult(counting || translated);
        }

        // The progress counts every book again after a start and whenever the languages profiles read change, a batch of books per round beside the
        // translation; whether books are left to count.
        private async Task<bool> CountAsync()
        {
            var languages = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadBookTranslationLanguagesAsync(ct), CancellationToken);
            if (_progress.CountFrom(languages) is not { } from) return false;
            var next = await Context.ExecuteBatchAsync((ctx, ct) => ctx.RecountBookTranslationBatchAsync(new BookTranslationBatchData(from, BooksPerCount), ct), CancellationToken);
            _progress.Counted(languages, next);
            return next is not null;
        }

        // A few blocks of the next section; whether there was work.
        private async Task<bool> TranslateNextAsync()
        {
            // The Owner's default model (else the largest installed TranslateGemma) on the first machine of the order that answers with it; one that
            // fails rests, and the next round goes to the next one.
            var setup = await Context.ExecuteBatchAsync(TranslationProviders.SetupAsync, CancellationToken);
            var machine = await _providers.ChooseAsync(setup, CancellationToken);

            // Passages a reader re-translated with another model come first: they were asked for by name.
            if (await TranslateChosenAsync(setup, machine?.ModelId) is { } chosen) return chosen;
            if (machine is null)
            {
                _progress.Idle("noMachine");
                return false;
            }
            string digest;
            try
            {
                digest = await machine.Provider.DigestAsync(machine, CancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException && !CancellationToken.IsCancellationRequested)
            {
                machine.Provider.Fail(machine);
                _progress.Idle("machineFailed");
                return false;
            }

            var key = LocalReadingPaths.ModelKey(machine.ModelId);
            _progress.Translating(key, digest);
            var unit = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadOptionalAsync<BookTranslationUnitData>(NextSection(key, digest), ct), CancellationToken);
            if (unit is null)
            {
                _progress.Idle(null);
                return false;
            }

            _progress.Picked(unit.DocumentId, unit.SectionIndex, unit.Language);
            return await TranslateBlocksAsync(machine, digest, unit, null) ?? false;
        }

        // A few blocks of the next section someone re-translated with a model other than the default, with that model; null when none waits or
        // no machine has such a model now. The progress view keeps counting the default model only.
        private async Task<bool?> TranslateChosenAsync(TranslationSetupV1 setup, string? defaultModel)
        {
            var models = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadSql.ReadListAsync<ReadingTranslationChosenModelData>(new SqlStatement
            {
                Statement = """SELECT DISTINCT choice."Model" AS "ChosenModel" FROM "ReadingTranslationChoices" AS choice ORDER BY choice."Model";"""
            }, ct), CancellationToken);
            foreach (var chosen in models)
            {
                if (chosen.ChosenModel == defaultModel || _providers.Find(chosen.ChosenModel) is null) continue;
                if (await _providers.ChooseAsync(setup, CancellationToken, chosen.ChosenModel) is not { } machine) continue;
                string digest;
                try
                {
                    digest = await machine.Provider.DigestAsync(machine, CancellationToken);
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException && !CancellationToken.IsCancellationRequested)
                {
                    machine.Provider.Fail(machine);
                    continue;
                }

                var (unit, blocks) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    var next = await ctx.ReadOptionalAsync<BookTranslationUnitData>(NextChosenSection(chosen, LocalReadingPaths.ModelKey(chosen.ChosenModel), digest), ct);
                    if (next is null) return ((BookTranslationUnitData?)null, new HashSet<int>());
                    var sql = new SqlStatement();
                    sql.Statement =
                        """
                        SELECT
                            choice."BlockIndex",
                            choice."Model"
                        FROM "ReadingTranslationChoices" AS choice
                        WHERE choice."ReadingDocumentId" = @DocumentId
                            AND choice."SectionIndex" = @SectionIndex
                            AND choice."Language" = @Language
                            AND choice."Model" = @ChosenModel;
                        """;
                    sql.Parameters.Add(new ReadingTranslationSectionData(next.DocumentId, next.SectionIndex, next.Language, []));
                    sql.Parameters.Add(chosen);
                    return ((BookTranslationUnitData?)next, (await ctx.ReadSql.ReadListAsync<ReadingTranslationChoiceData>(sql, ct)).Select(choice => choice.BlockIndex).ToHashSet());
                }, CancellationToken);
                if (unit is not null && await TranslateBlocksAsync(machine, digest, unit, blocks) is { } worked) return worked;
            }
            return null;
        }

        // Up to a few blocks of the section that this version of the model has not translated yet (only the given ones, when given), each kept at once;
        // whether there was work, none when none of the given blocks was left to do.
        private async Task<bool?> TranslateBlocksAsync(TranslationMachine machine, string digest, BookTranslationUnitData unit, IReadOnlySet<int>? only)
        {
            var reports = only is null;
            var nodes = new HtmlParser().ParseDocument(unit.Html).QuerySelectorAll("[data-b]").ToArray();
            var path = LocalReadingPaths.Cache(unit.SectionIndex, unit.Language, machine.ModelId);
            var texts = (unit.Cache is null ? null : LocalReadingPaths.Parse(unit.Cache, unit.Html, unit.SourceLanguage, digest, nodes.Length))?.Texts ?? new string?[nodes.Length];
            if (nodes.Length == 0)
            {
                // A page of one picture has nothing to translate; it is done once its empty cache exists.
                await Context.ExecuteBatchAsync((ctx, ct) => LocalReadingPaths.StoreAsync(ctx, unit.DocumentId, path, unit.Html, unit.SourceLanguage, digest, 0, new Dictionary<int, string>(), ct), CancellationToken);
                return true;
            }

            if (machine.Provider is IBatchTranslationProvider batcher)
                return await TranslateBatchAsync(batcher, machine, unit, nodes, texts, path, digest, only);

            var translated = 0;
            var stored = 0;
            for (var index = 0; index < nodes.Length && translated < BlocksPerCall; index++)
            {
                if (!string.IsNullOrWhiteSpace(texts[index]) || only is not null && !only.Contains(index)) continue;
                var node = nodes[index];
                var source = ReadingHtml.TranslationText(node);
                string value;
                // A block holding others, one without text or an oversized one keeps its original ("" in the cache, which takes no text over 20,000 characters).
                if (LocalReadingPaths.Untranslated(node)) value = "";
                else
                {
                    using var lane = await _lanes.TryEnterAsync(machine.Provider.Key, GateWait, CancellationToken, background: true);
                    if (lane is null) break;
                    try
                    {
                        var context = ReadingHtml.TranslationContext(nodes, index);
                        // A failed block keeps its readable original rather than caching a nonsensical translation.
                        value = await machine.Provider.TranslateBlockAsync(machine, unit.SourceLanguage, unit.Language, source, context, CancellationToken) ?? source;
                        if (reports) _progress.BlockDone();
                    }
                    catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException && !CancellationToken.IsCancellationRequested)
                    {
                        machine.Provider.Fail(machine);
                        if (reports) _progress.Idle("machineFailed");
                        break;
                    }
                    translated++;
                }

                texts[index] = value;
                var block = index;
                texts = await Context.ExecuteBatchAsync((ctx, ct) => LocalReadingPaths.StoreAsync(ctx, unit.DocumentId, path, unit.Html, unit.SourceLanguage, digest, nodes.Length,
                    new Dictionary<int, string> { [block] = value }, ct), CancellationToken);
                stored++;
            }

            // Chosen blocks this section's text no longer has leave the round to other work rather than being picked again and again.
            if (only is not null && stored == 0) return null;
            return translated > 0 || texts.Where((_, index) => only is null || only.Contains(index)).All(text => text is not null);
        }

        // The next section with blocks someone chose this model for that its current version has not translated yet, the latest choice first and
        // a book's sections in order.
        private static SqlStatement NextChosenSection(ReadingTranslationChosenModelData chosen, string modelKey, string digest)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH chosen AS (
                    SELECT
                        choice."ReadingDocumentId",
                        choice."SectionIndex",
                        choice."Language",
                        array_agg(choice."BlockIndex") AS "Blocks",
                        max(choice."ChosenAt") AS "ChosenAt"
                    FROM "ReadingTranslationChoices" AS choice
                    WHERE choice."Model" = @ChosenModel
                        AND choice."SectionIndex" < 100000
                    GROUP BY choice."ReadingDocumentId", choice."SectionIndex", choice."Language"
                ),
                sections AS (
                    SELECT
                        chosen."ReadingDocumentId",
                        chosen."SectionIndex",
                        chosen."Language",
                        chosen."Blocks",
                        chosen."ChosenAt",
                        lower(split_part(split_part(coalesce(document."Language", work."OriginalLanguage"), '-', 1), '_', 1)) AS "SourceLanguage",
                        coalesce((document."Structure" -> 'Sections' -> chosen."SectionIndex" ->> 'Blocks')::integer, 0) AS "Count",
                        lpad(chosen."SectionIndex"::text, 5, '0') AS "Key"
                    FROM
                        chosen
                        JOIN "ReadingDocuments" AS document ON document."Id" = chosen."ReadingDocumentId"
                        JOIN "StoredFiles" AS file ON file."Id" = document."StoredFileId"
                        JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                        JOIN "Works" AS work ON work."Id" = asset."WorkId"
                    WHERE jsonb_typeof(document."Structure" -> 'Sections') = 'array'
                )
                SELECT
                    section."ReadingDocumentId" AS "DocumentId",
                    section."SectionIndex",
                    section."Language",
                    section."SourceLanguage",
                    convert_from(text."Bytes", 'UTF8') AS "Html",
                    section."Count" AS "Blocks",
                    stored.value::text AS "Cache"
                FROM
                    sections AS section
                    JOIN "ReadingResources" AS text ON text."ReadingDocumentId" = section."ReadingDocumentId"
                        AND text."Path" = ':section/' || section."Key"
                    LEFT JOIN "ReadingResources" AS cache ON cache."ReadingDocumentId" = section."ReadingDocumentId"
                        AND cache."Path" = ':localtranslation/v4/' || @Model || '/' || section."Language" || '/' || section."Key"
                    -- Parsed once (OFFSET 0), not again for every condition and sort key on it.
                    CROSS JOIN LATERAL (
                        SELECT convert_from(cache."Bytes", 'UTF8')::jsonb AS value
                        OFFSET 0
                    ) AS stored
                WHERE
                    length(section."SourceLanguage") BETWEEN 2 AND 3
                    AND section."Language" <> section."SourceLanguage"
                    AND EXISTS (
                        SELECT 1
                        FROM unnest(section."Blocks") AS block (number)
                        WHERE block.number < section."Count"
                            AND (
                                stored.value IS NULL
                                OR stored.value ->> 'ModelDigest' IS DISTINCT FROM @Digest
                                OR coalesce(stored.value -> 'Texts' -> block.number, 'null'::jsonb) = 'null'::jsonb
                            )
                    )
                ORDER BY
                    section."ChosenAt" DESC,
                    section."ReadingDocumentId",
                    section."SectionIndex",
                    section."Language"
                LIMIT 1;
                """;
            sql.Parameters.Add(chosen);
            sql.Parameters.Add(new BookTranslationModelData(modelKey, digest));
            return sql;
        }

        // A provider that takes several blocks in one request translates the next stretch of the section together, with the passage before it as context; a block
        // whose answer fails the checks is asked for alone, and one that still fails keeps its readable original. Only the given blocks, when given.
        private async Task<bool?> TranslateBatchAsync(IBatchTranslationProvider batcher, TranslationMachine machine, BookTranslationUnitData unit, AngleSharp.Dom.IElement[] nodes,
            string?[] texts, string path, string digest, IReadOnlySet<int>? only)
        {
            var reports = only is null;
            var values = new Dictionary<int, string>();
            var sources = new List<(int Index, string Text)>();
            var characters = 0;
            for (var index = 0; index < nodes.Length; index++)
            {
                if (!string.IsNullOrWhiteSpace(texts[index]) || only is not null && !only.Contains(index)) continue;
                var source = ReadingHtml.TranslationText(nodes[index]);
                // A block holding others, one without text or an oversized one keeps its original ("" in the cache).
                if (LocalReadingPaths.Untranslated(nodes[index])) { values[index] = ""; continue; }
                if (sources.Count > 0 && characters + source.Length > batcher.BatchCharacters) break;
                sources.Add((index, source));
                characters += source.Length;
            }

            var failed = false;
            if (sources.Count > 0)
            {
                using var lane = await _lanes.TryEnterAsync(machine.Provider.Key, GateWait, CancellationToken, background: true);
                if (lane is null) return false;
                try
                {
                    var answers = await batcher.TranslateBlocksAsync(machine, unit.SourceLanguage, unit.Language, [.. sources.Select(item => item.Text)], ReadingHtml.TranslationContext(nodes, sources[0].Index), CancellationToken);
                    for (var k = 0; k < sources.Count; k++)
                    {
                        var (index, source) = sources[k];
                        values[index] = answers[k] ?? await machine.Provider.TranslateBlockAsync(machine, unit.SourceLanguage, unit.Language, source, ReadingHtml.TranslationContext(nodes, index), CancellationToken) ?? source;
                        if (reports) _progress.BlockDone();
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException && !CancellationToken.IsCancellationRequested)
                {
                    machine.Provider.Fail(machine);
                    if (reports) _progress.Idle("machineFailed");
                    // What was translated before the failure is kept; the rest waits for the next round.
                    failed = true;
                }
            }

            if (values.Count > 0)
                texts = await Context.ExecuteBatchAsync((ctx, ct) => LocalReadingPaths.StoreAsync(ctx, unit.DocumentId, path, unit.Html, unit.SourceLanguage, digest, nodes.Length, values, ct), CancellationToken);
            if (only is not null && values.Count == 0 && !failed) return null;
            return !failed && (sources.Count > 0 || texts.Where((_, index) => only is null || only.Contains(index)).All(text => text is not null));
        }

        // The next section to translate: from a book someone is reading (most recently read first, from the chapter they are in, then
        // the chapters before it), then the rest of the library one book after the other (a book is finished before the next starts); into each language a profile uses that is not the book's own.
        // A scan's Smart Pages are units of their own (from 100000 on): first where someone reads the book's pages, from the page they are on.
        // Done means a cache from this model in this version with every block filled in. A section only an earlier version translated is stale: it
        // stays readable and waits for the missing sections of the books being read, or of the rest of the library.
        private static SqlStatement NextSection(string modelKey, string digest)
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
                        lower(split_part(split_part(coalesce(document."Language", work."OriginalLanguage"), '-', 1), '_', 1)) AS "SourceLanguage",
                        jsonb_array_length(document."Structure" -> 'Sections') AS "Sections",
                        coalesce(jsonb_array_length(document."Structure" -> 'SmartPages'), 0) AS "Pages",
                        reading."ReadAt",
                        coalesce(reading."FromSection", 0) AS "FromSection",
                        coalesce(reading."FromPage", 0) AS "FromPage",
                        coalesce(reading."ReadsPages", FALSE) AS "ReadsPages"
                    FROM
                        "ReadingDocuments" AS document
                        JOIN "StoredFiles" AS file ON file."Id" = document."StoredFileId"
                        JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                        JOIN "Works" AS work ON work."Id" = asset."WorkId"
                        LEFT JOIN LATERAL (
                            SELECT
                                max(progress."UpdatedAt") AS "ReadAt",
                                min((substring(progress."Locator" FROM '^s([0-9]+)/'))::integer) AS "FromSection",
                                min((substring(progress."Locator" FROM '^p([0-9]+)$'))::integer) AS "FromPage",
                                bool_or(progress."Locator" ~ '^p[0-9]+$') AS "ReadsPages"
                            FROM "MediaProgress" AS progress
                            WHERE progress."WorkId" = asset."WorkId" AND NOT progress."IsCompleted" AND progress."Locator" ~ '^(s[0-9]+/|p[0-9]+$)'
                        ) AS reading ON TRUE
                    WHERE
                        coalesce(document."Language", work."OriginalLanguage") IS NOT NULL
                        AND jsonb_typeof(document."Structure" -> 'Sections') = 'array'
                )
                SELECT
                    book."Id" AS "DocumentId",
                    section."Number" AS "SectionIndex",
                    target."Language",
                    book."SourceLanguage",
                    convert_from(text."Bytes", 'UTF8') AS "Html",
                    section."Blocks",
                    stored.value::text AS "Cache"
                FROM
                    books AS book
                    JOIN "ReadingDocuments" AS document ON document."Id" = book."Id"
                    CROSS JOIN targets AS target
                    CROSS JOIN LATERAL (
                        SELECT
                            number AS "Number",
                            ':section/' || lpad(number::text, 5, '0') AS "Path",
                            lpad(number::text, 5, '0') AS "Key",
                            coalesce((document."Structure" -> 'Sections' -> number ->> 'Blocks')::integer, 0) AS "Blocks",
                            FALSE AS "IsPage",
                            0 AS "Page"
                        FROM generate_series(0, book."Sections" - 1) AS number
                        UNION ALL
                        SELECT
                            100000 + number,
                            ':smartpage/' || lpad(number::text, 5, '0'),
                            (100000 + number)::text,
                            coalesce((document."Structure" -> 'SmartPages' -> number ->> 'Blocks')::integer, 0),
                            TRUE,
                            coalesce((document."Structure" -> 'SmartPages' -> number ->> 'PageFrom')::integer, 0)
                        FROM generate_series(0, book."Pages" - 1) AS number
                    ) AS section
                    JOIN "ReadingResources" AS text ON text."ReadingDocumentId" = book."Id"
                        AND text."Path" = section."Path"
                    LEFT JOIN "ReadingResources" AS cache ON cache."ReadingDocumentId" = book."Id"
                        AND cache."Path" = ':localtranslation/v4/' || @Model || '/' || target."Language" || '/' || section."Key"
                    -- Parsed once (OFFSET 0), not again for every condition and sort key on it.
                    CROSS JOIN LATERAL (
                        SELECT convert_from(cache."Bytes", 'UTF8')::jsonb AS value
                        OFFSET 0
                    ) AS stored
                    CROSS JOIN LATERAL (
                        SELECT
                            stored.value IS NOT NULL
                            AND (stored.value ->> 'ModelDigest' IS DISTINCT FROM @Digest OR jsonb_typeof(stored.value -> 'Stale') = 'object') AS "IsStale"
                    ) AS state
                WHERE
                    length(target."Language") BETWEEN 2 AND 3
                    AND length(book."SourceLanguage") BETWEEN 2 AND 3
                    AND target."Language" <> book."SourceLanguage"
                    AND (
                        stored.value IS NULL
                        OR state."IsStale"
                        OR EXISTS (
                            SELECT 1 FROM jsonb_array_elements(stored.value -> 'Texts') AS block (value)
                            WHERE block.value = 'null'::jsonb
                        )
                    )
                ORDER BY
                    book."ReadAt" IS NULL,
                    state."IsStale",
                    book."ReadAt" DESC,
                    book."Id",
                    section."IsPage" <> book."ReadsPages",
                    CASE WHEN section."IsPage" THEN section."Page" < book."FromPage" ELSE section."Number" < book."FromSection" END,
                    section."Number",
                    target."Language"
                LIMIT 1;
                """;
            sql.Parameters.Add(new BookTranslationModelData(modelKey, digest));
            return sql;
        }
    }
}
