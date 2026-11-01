using System.Net.Http;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Translation.V1;
using Jularr.Integrations.Logic.Translation;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.System.Reading.V1
{
    // Translates the library ahead of reading with the local TranslateGemma model, into every language a profile uses (its
    // interface language and the languages chosen for books), into the same cache the Reader fills while reading. Books someone
    // is reading come first, from the chapter they are in. A few blocks per call; returns whether there was work.
    // A reader's own request comes first: while the model is busy for one, the round ends and the job looks again later.
    public sealed class TranslateReadingBooksV1 : SystemService<BookTranslationParametersV1, NoData>
    {
        private const int BlocksPerCall = 6;
        private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(2);
        private readonly OllamaLocalModels _models;
        private readonly LocalTranslationMachines _machines;
        private readonly LocalTranslationGate _gate;
        private readonly BookTranslationProgress _progress;

        internal TranslateReadingBooksV1(OllamaLocalModels models, LocalTranslationMachines machines, LocalTranslationGate gate, BookTranslationProgress progress)
        {
            _models = models;
            _machines = machines;
            _gate = gate;
            _progress = progress;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(BookTranslationParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(BookTranslationParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // The first machine of the order that answers; one that fails rests, and the next round goes to the next one.
            var saved = await Context.ExecuteBatchAsync(LocalTranslationMachines.SavedAsync, CancellationToken);
            if (await _machines.ChooseAsync(saved, CancellationToken) is not var (server, model))
            {
                _progress.Idle("noMachine");
                return new UpdateResult(false);
            }
            string digest;
            try
            {
                digest = await _models.DigestAsync(server, model, CancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException && !CancellationToken.IsCancellationRequested)
            {
                _machines.Fail(server);
                _progress.Idle("machineFailed");
                return new UpdateResult(false);
            }

            var unit = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadOptionalAsync<BookTranslationUnitData>(NextSection(model, digest), ct), CancellationToken);
            if (unit is null)
            {
                _progress.Idle(null);
                return new UpdateResult(false);
            }

            _progress.Picked(unit.DocumentId, unit.SectionIndex, unit.Language);

            var nodes = new HtmlParser().ParseDocument(unit.Html).QuerySelectorAll("[data-b]").ToArray();
            var path = LocalReadingPaths.Cache(unit.SectionIndex, unit.Language, model);
            var texts = (unit.Cache is null ? null : LocalReadingPaths.Parse(unit.Cache, unit.Html, unit.SourceLanguage, digest, nodes.Length))?.Texts ?? new string[nodes.Length];
            if (nodes.Length == 0)
            {
                // A page of one picture has nothing to translate; it is done once its empty cache exists.
                await Context.ExecuteBatchAsync((ctx, ct) => LocalReadingPaths.StoreAsync(ctx, unit.DocumentId, path, unit.Html, unit.SourceLanguage, digest, 0, new Dictionary<int, string>(), ct), CancellationToken);
                _progress.SectionDone();
                return new UpdateResult(true);
            }

            var translated = 0;
            for (var index = 0; index < nodes.Length && translated < BlocksPerCall; index++)
            {
                if (!string.IsNullOrWhiteSpace(texts[index])) continue;
                var node = nodes[index];
                var source = ReadingHtml.TranslationText(node);
                string value;
                // A block holding others, or one without text, has nothing of its own to translate; an oversized one keeps its text.
                if (node.QuerySelector("[data-b]") is not null || source.Length == 0) value = "";
                else if (source.Length > 20_000) value = source;
                else
                {
                    if (!await _gate.HeavyWork.WaitAsync(GateWait, CancellationToken)) break;
                    try
                    {
                        var context = ReadingHtml.TranslationContext(nodes, index);
                        // A failed block keeps its readable original rather than caching a nonsensical translation.
                        value = await _models.TranslateBlockAsync(server, model, unit.SourceLanguage, unit.Language, source, context, CancellationToken) ?? source;
                        _progress.BlockDone();
                    }
                    catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException && !CancellationToken.IsCancellationRequested)
                    {
                        _machines.Fail(server);
                        _progress.Idle("machineFailed");
                        break;
                    }
                    finally { _gate.HeavyWork.Release(); }
                    translated++;
                }

                texts[index] = value;
                var block = index;
                texts = await Context.ExecuteBatchAsync((ctx, ct) => LocalReadingPaths.StoreAsync(ctx, unit.DocumentId, path, unit.Html, unit.SourceLanguage, digest, nodes.Length,
                    new Dictionary<int, string> { [block] = value }, ct), CancellationToken);
            }

            if (texts.All(text => text is not null)) _progress.SectionDone();
            return new UpdateResult(translated > 0 || texts.All(text => text is not null));
        }

        // The next section to translate: from a book someone is reading (most recently read first, from the chapter they are in, then
        // the chapters before it), then the rest of the library; into each language a profile uses that is not the book's own.
        // A scan's Smart Pages are units of their own (from 100000 on): first where someone reads the book's pages, from the page they are on.
        // Done means a cache from this model with every block filled in.
        private static SqlStatement NextSection(string model, string digest)
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
                    convert_from(cache."Bytes", 'UTF8') AS "Cache"
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
                WHERE
                    length(target."Language") BETWEEN 2 AND 3
                    AND length(book."SourceLanguage") BETWEEN 2 AND 3
                    AND target."Language" <> book."SourceLanguage"
                    AND (
                        cache."ReadingDocumentId" IS NULL
                        OR convert_from(cache."Bytes", 'UTF8')::jsonb ->> 'ModelDigest' IS DISTINCT FROM @Digest
                        OR EXISTS (
                            SELECT 1 FROM jsonb_array_elements(convert_from(cache."Bytes", 'UTF8')::jsonb -> 'Texts') AS block (value)
                            WHERE block.value = 'null'::jsonb
                        )
                    )
                ORDER BY
                    book."ReadAt" IS NULL,
                    book."ReadAt" DESC,
                    section."IsPage" <> book."ReadsPages",
                    CASE WHEN section."IsPage" THEN section."Page" < book."FromPage" ELSE section."Number" < book."FromSection" END,
                    section."Number",
                    book."Id",
                    target."Language"
                LIMIT 1;
                """;
            sql.Parameters.Add(new BookTranslationModelData(model, digest));
            return sql;
        }
    }
}
