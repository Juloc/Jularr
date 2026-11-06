using System.Globalization;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Translation.V1;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;
using Jularr.Integrations.Logic.Translation;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.User.Reading.V1
{
    [ServiceEndpoint("POST", "/api/v1/reading/assets/{mediaAssetId:long}/open")]
    public sealed class OpenReadingV1 : UserService<ReadingAssetParametersV1, NoData>
    {
        private readonly ReadingContent _content;
        private readonly ReadingActivity _activity;

        internal OpenReadingV1(ReadingContent content, ReadingActivity activity)
        {
            _content = content;
            _activity = activity;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReadingDocumentV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadingAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingAssetParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // The newest readable EPUB or comic of the asset, else a ready reading copy of its PDF, else a book read through a reading copy made on opening (FB2, MOBI, ...), else its newest PDF,
            // else its DjVu pages; a matching prepared document is reused.
            var source = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        work."Id" AS "WorkId",
                        visible."Title" AS "Title",
                        file."Id" AS "StoredFileId",
                        file."RelativePath",
                        file."SizeBytes",
                        file."ModifiedAt",
                        file."MissingSince" IS NOT NULL AS "IsMissing",
                        root."RelativePath" AS "RootRelativePath",
                        mount."Id" AS "StorageMountId",
                        document."Id" AS "DocumentId",
                        coalesce(document."CacheCompletedAt" IS NOT NULL, FALSE) AS "IsCached"
                    FROM
                        "MediaAssets" AS asset
                        -- This Work alone: joined directly, the view reads the catalog up to it.
                        JOIN LATERAL (SELECT visible."Title" FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId OFFSET 0) AS visible ON TRUE
                        JOIN "Works" AS work ON work."Id" = asset."WorkId"
                        JOIN LATERAL (
                            SELECT
                                candidate.*
                            FROM
                                "ReadableStoredFiles" AS candidate
                            WHERE
                                candidate."MediaAssetId" = asset."Id"
                                AND candidate."RelativePath" ~* '\.(epub|cbz|cbr|cb7|pdf|fb2|fb2\.zip|txt|rtf|docx|mobi|azw|azw3|djvu|djv)$'
                                AND (NOT candidate."IsDerived" OR candidate."DerivedAnalyzerVersion" = @PdfAnalyzerVersion)
                            ORDER BY
                                candidate."MissingSince" IS NULL DESC,
                                CASE WHEN candidate."RelativePath" ~* '\.(djvu|djv)$' THEN 4 WHEN candidate."RelativePath" ~* '\.pdf$' THEN 3
                                    WHEN candidate."RelativePath" ~* '\.(fb2|fb2\.zip|txt|rtf|docx|mobi|azw|azw3)$' THEN 2 WHEN candidate."IsDerived" THEN 1 ELSE 0 END,
                                candidate."ModifiedAt" DESC,
                                candidate."Id"
                            LIMIT 1
                        ) AS file ON TRUE
                        JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                        JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                        LEFT JOIN LATERAL (
                            SELECT
                                prepared."Id",
                                prepared."CacheCompletedAt"
                            FROM
                                "ReadingDocuments" AS prepared
                            WHERE
                                prepared."StoredFileId" = file."Id"
                                AND prepared."SourceSizeBytes" = file."SizeBytes"
                                AND prepared."SourceModifiedAt" = file."ModifiedAt"
                                AND prepared."AnalyzerVersion" = CASE prepared."ReadingFormatTypeId" WHEN 4 THEN @ComicAnalyzerVersion WHEN 2 THEN @PdfAnalyzerVersion ELSE @AnalyzerVersion END
                            ORDER BY
                                prepared."CacheCompletedAt" IS NOT NULL DESC,
                                prepared."Id" DESC
                            LIMIT 1
                        ) AS document ON TRUE
                    WHERE
                        asset."Id" = @MediaAssetId
                        AND asset."MediaAssetTypeId" IN (3, 4);
                    """;
                sql.Parameters.Add(new ReadingSourceLookupData(Parameters.MediaAssetId, ReadingHtml.AnalyzerVersion, ComicPages.AnalyzerVersion, PdfBook.AnalyzerVersion));
                sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
                return ctx.ReadSql.ReadOptionalAsync<ReadingSourceData>(sql, ct);
            }, CancellationToken) ?? throw new KeyNotFoundException("The book was not found.");

            // Background conversion waits while a person opens a book.
            long documentId;
            using (_activity.Begin())
            {
                documentId = await _content.OpenAsync(Context, source, CancellationToken);
            }

            return await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        document."Id" AS "DocumentId",
                        (encode(document."ContentHash", 'hex') || ':' || coalesce(document."CacheCompletedAt"::text, 'pending')) AS "Revision",
                        asset."WorkId",
                        coalesce(visible."Title", document."Title") AS "Title",
                        document."ReadingFormatTypeId",
                        jsonb_array_length(document."Structure" -> 'Sections') AS "SectionCount",
                        document."CacheCompletedAt" IS NOT NULL AS "IsCached",
                        progress."Locator",
                        progress."Progression",
                        coalesce(visible."MediaSubtypeId" = 2, FALSE) AS "IsRightToLeft",
                        document."Language",
                        chosen."Language" AS "ReadingLanguage",
                        work."OriginalLanguage",
                        coalesce((document."Structure" ->> 'Recommended')::boolean, TRUE) AS "IsRecommended",
                        coalesce(jsonb_array_length(document."Structure" -> 'OriginalPages'), 0) AS "OriginalPages",
                        coalesce((document."Structure" ->> 'RendersPages')::boolean, FALSE) AS "RendersPages",
                        ARRAY(
                            SELECT
                                (page.value ->> 'PageFrom')::integer
                            FROM
                                jsonb_array_elements(coalesce(document."Structure" -> 'SmartPages', '[]'::jsonb)) WITH ORDINALITY AS page (value, position)
                            ORDER BY
                                page.position
                        ) AS "SmartPages",
                        ARRAY(
                            -- A language every block with text has a server translation in, from any model (this version's text or an earlier one's).
                            SELECT
                                section."Language"
                            FROM (
                                SELECT
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
                                    cache."ReadingDocumentId" = document."Id"
                                    AND cache."Path" ~ '^:localtranslation/v4/[^/]+/[a-z]{2,3}/[0-9]{5}$'
                                GROUP BY
                                    split_part(cache."Path", '/', 4),
                                    split_part(cache."Path", '/', 5)
                            ) AS section
                            WHERE
                                section."Visible" >= section."Blocks"
                            GROUP BY
                                section."Language"
                            HAVING
                                count(*) >= (SELECT count(*) FROM jsonb_array_elements(document."Structure" -> 'Sections') AS part WHERE (part ->> 'Blocks')::int > 0)
                            ORDER BY
                                1
                        ) AS "TranslatedLanguages"
                    FROM
                        "ReadingDocuments" AS document
                        JOIN "MediaAssets" AS asset ON asset."Id" = @MediaAssetId
                        -- This Work alone: joined directly, the view reads the catalog up to it.
                        JOIN LATERAL (SELECT visible."Title", visible."MediaSubtypeId" FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId OFFSET 0) AS visible ON TRUE
                        JOIN "Works" AS work ON work."Id" = asset."WorkId"
                        LEFT JOIN "MediaProgress" AS progress ON progress."ProfileId" = @ProfileId
                            AND progress."WorkId" = asset."WorkId"
                            AND progress."WorkUnitId" IS NOT DISTINCT FROM asset."WorkUnitId"
                            AND progress."ProgressPositionTypeId" = 2
                        LEFT JOIN "ReadingPreferences" AS chosen ON chosen."ProfileId" = @ProfileId AND chosen."WorkId" = asset."WorkId"
                    WHERE
                        document."Id" = @DocumentId;
                    """;
                sql.Parameters.Add(new ReadingOpenedData(documentId, Parameters.MediaAssetId, Auth.ProfileId!.Value));
                return ctx.ReadSql.ReadRequiredAsync<ReadingDocumentV1>(sql, ct);
            }, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/reading/assets/{mediaAssetId:long}/source")]
    public sealed class ReadReadingSourceV1 : UserService<ReadingAssetParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReadingSourceV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadingAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingAssetParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // EPUB and comics are prepared; a book whose only present file is a PDF opens as one. A reading copy counts while it is ready and its PDF unchanged.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id" AS "WorkId",
                    visible."Title" AS "Title",
                    CASE
                        WHEN bool_or(file."RelativePath" ~* '\.pdf$' AND file."MissingSince" IS NULL)
                            AND NOT bool_or(file."RelativePath" ~* '\.(epub|cbz|cbr|cb7|fb2|fb2\.zip|txt|rtf|docx|mobi|azw|azw3)$' AND file."MissingSince" IS NULL) THEN 2
                        WHEN bool_or(file."RelativePath" ~* '\.(cbz|cbr|cb7)$') AND NOT bool_or(file."RelativePath" ~* '\.epub$') THEN 4
                        WHEN bool_or(file."RelativePath" ~* '\.(djvu|djv)$') AND NOT bool_or(file."RelativePath" ~* '\.(epub|pdf|fb2|fb2\.zip|txt|rtf|docx|mobi|azw|azw3)$') THEN 4
                        ELSE 1
                    END::smallint AS "ReadingFormatTypeId",
                    bool_or(file."RelativePath" ~* '\.pdf$' AND file."MissingSince" IS NULL) AS "HasPdf",
                    progress."Status" AS "ReadingDerivationStatusTypeId",
                    progress."Reason",
                    chosen."ReadingViewTypeId",
                    stopped."Locator"
                FROM
                    "MediaAssets" AS asset
                    -- This Work alone: joined directly, the view reads the catalog up to it.
                    JOIN LATERAL (SELECT visible."Title" FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId OFFSET 0) AS visible ON TRUE
                    JOIN "Works" AS work ON work."Id" = asset."WorkId"
                    LEFT JOIN "ReadingPreferences" AS chosen ON chosen."ProfileId" = @ProfileId AND chosen."WorkId" = asset."WorkId"
                    LEFT JOIN "MediaProgress" AS stopped ON stopped."ProfileId" = @ProfileId
                        AND stopped."WorkId" = asset."WorkId"
                        AND stopped."WorkUnitId" IS NOT DISTINCT FROM asset."WorkUnitId"
                        AND stopped."ProgressPositionTypeId" = 2
                    JOIN "ReadableStoredFiles" AS file ON file."MediaAssetId" = asset."Id"
                        AND file."RelativePath" ~* '\.(epub|cbz|cbr|cb7|pdf|fb2|fb2\.zip|txt|rtf|docx|mobi|azw|azw3|djvu|djv)$'
                        AND (NOT file."IsDerived" OR file."DerivedAnalyzerVersion" = @PdfAnalyzerVersion)
                    LEFT JOIN LATERAL (
                        SELECT
                            CASE
                                WHEN derivation."SourceSizeBytes" = pdf."SizeBytes" AND derivation."SourceModifiedAt" = pdf."ModifiedAt" AND derivation."AnalyzerVersion" = @PdfAnalyzerVersion
                                    THEN derivation."ReadingDerivationStatusTypeId"
                                ELSE 1
                            END::smallint AS "Status",
                            CASE
                                WHEN derivation."SourceSizeBytes" = pdf."SizeBytes" AND derivation."SourceModifiedAt" = pdf."ModifiedAt" AND derivation."AnalyzerVersion" = @PdfAnalyzerVersion
                                    THEN derivation."Reason"
                            END AS "Reason"
                        FROM
                            "StoredFiles" AS pdf
                            LEFT JOIN "ReadingDerivations" AS derivation ON derivation."StoredFileId" = pdf."Id"
                        WHERE
                            pdf."MediaAssetId" = asset."Id"
                            AND pdf."RelativePath" ~* '\.pdf$'
                            AND pdf."MissingSince" IS NULL
                            AND EXISTS (SELECT 1 FROM "InstanceModules" AS module WHERE module."Key" = 'SmartPdf' AND module."IsEnabled")
                            AND NOT EXISTS (
                                SELECT
                                    1
                                FROM
                                    "StoredFiles" AS other
                                WHERE
                                    other."MediaAssetId" = asset."Id"
                                    AND other."RelativePath" ~* '\.epub$'
                                    AND other."MissingSince" IS NULL
                                    AND NOT EXISTS (SELECT 1 FROM "ReadingDerivations" AS derived WHERE derived."EbookStoredFileId" = other."Id")
                            )
                        ORDER BY
                            pdf."ModifiedAt" DESC,
                            pdf."Id"
                        LIMIT 1
                    ) AS progress ON TRUE
                WHERE
                    asset."Id" = @MediaAssetId
                    AND asset."MediaAssetTypeId" IN (3, 4)
                GROUP BY
                    work."Id",
                    visible."Title",
                    progress."Status",
                    progress."Reason",
                    chosen."ReadingViewTypeId",
                    stopped."Locator";
                """;
            sql.Parameters.Add(new ReadingSourceLookupData(Parameters.MediaAssetId, ReadingHtml.AnalyzerVersion, ComicPages.AnalyzerVersion, PdfBook.AnalyzerVersion));
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return await Context.ReadSql.ReadOptionalAsync<ReadingSourceV1>(sql, CancellationToken) ?? throw new KeyNotFoundException("The book was not found.");
        }
    }

    [ServiceEndpoint("GET", "/api/v1/reading/assets/{mediaAssetId:long}/file")]
    public sealed class ReadReadingFileV1 : UserService<ReadingAssetParametersV1>
    {
        private readonly StorageAccess _storage;

        internal ReadReadingFileV1(StorageAccess storage)
        {
            _storage = storage;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReadingFileV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadingAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingAssetParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The newest present PDF of the book, straight from its storage.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."RelativePath",
                    root."RelativePath" AS "RootRelativePath",
                    mount."Id" AS "StorageMountId"
                FROM
                    "MediaAssets" AS asset
                    JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id"
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                WHERE
                    asset."Id" = @MediaAssetId
                    AND asset."MediaAssetTypeId" IN (3, 4)
                    AND file."RelativePath" ~* '\.pdf$'
                    AND file."MissingSince" IS NULL
                    AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId)
                ORDER BY
                    file."ModifiedAt" DESC,
                    file."Id"
                LIMIT 1;
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            var source = await Context.ReadSql.ReadOptionalAsync<PlaybackStreamData>(sql, CancellationToken) ?? throw new KeyNotFoundException("The book was not found.");
            try
            {
                var files = _storage.Open(await Context.ReadStorageMountAsync(source.StorageMountId, CancellationToken));
                return new ReadingFileV1(await files.OpenReadAsync(StoragePath.Combine(source.RootRelativePath, source.RelativePath), CancellationToken));
            }
            catch (NasMountException)
            {
                // Offline storage is not a missing book.
                throw new ServiceUnavailableException("reading_unavailable");
            }
        }
    }

    [ServiceEndpoint("GET", "/api/v1/reading/documents/{documentId:long}/toc")]
    public sealed class ReadReadingTocV1 : UserService<ReadingDocumentParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<ReadingTocEntryV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadingDocumentParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingDocumentParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The structure stays even when the content left the cache.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    entry ->> 'Title' AS "Title",
                    (entry ->> 'Section')::integer AS "SectionIndex",
                    (entry ->> 'Level')::integer AS "Level",
                    entry ->> 'Anchor' AS "Anchor"
                FROM
                    "ReadingDocuments" AS document
                    CROSS JOIN LATERAL jsonb_array_elements(document."Structure" -> 'Toc') WITH ORDINALITY AS toc (entry, position)
                WHERE
                    document."Id" = @DocumentId
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "StoredFiles" AS file
                            JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                        WHERE
                            file."Id" = document."StoredFileId"
                            AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId)
                    )
                ORDER BY
                    toc.position
                LIMIT 2000;
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return new BaseDataList<ReadingTocEntryV1>(await Context.ReadSql.ReadListAsync<ReadingTocEntryV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/reading/documents/{documentId:long}/sections/{sectionIndex:int}")]
    public sealed class ReadReadingSectionV1 : UserService<ReadingSectionParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReadingSectionV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadingSectionParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingSectionParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Served from the cache; an evicted section needs the storage again. From 100000 on, the index names a Smart Page (a scanned page set anew),
            // which is read and translated like a section but is not part of the reading text.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    @SectionIndex AS "SectionIndex",
                    unit.value ->> 'Title' AS "Title",
                    convert_from(resource."Bytes", 'UTF8') AS "Html",
                    (unit.value ->> 'Characters')::bigint AS "Characters",
                    (
                        SELECT
                            coalesce(sum((section.value ->> 'Characters')::bigint), 0)
                        FROM
                            jsonb_array_elements(document."Structure" -> 'Sections') WITH ORDINALITY AS section (value, position)
                        WHERE
                            section.position <= @SectionIndex
                            AND @SectionIndex < 100000
                    )::bigint AS "CharactersBefore",
                    document."TotalCharacters",
                    (unit.value ->> 'Blocks')::integer AS "Blocks",
                    coalesce((unit.value ->> 'PageFrom')::integer, 0) AS "PageFrom"
                FROM
                    "ReadingDocuments" AS document
                    CROSS JOIN LATERAL (
                        SELECT
                            CASE WHEN @SectionIndex >= 100000 THEN document."Structure" -> 'SmartPages' -> (@SectionIndex - 100000)
                                ELSE document."Structure" -> 'Sections' -> @SectionIndex END AS value,
                            CASE WHEN @SectionIndex >= 100000 THEN ':smartpage/' || lpad((@SectionIndex - 100000)::text, 5, '0')
                                ELSE ':section/' || lpad(@SectionIndex::text, 5, '0') END AS "Path"
                    ) AS unit
                    JOIN "ReadingResources" AS resource ON resource."ReadingDocumentId" = document."Id"
                        AND resource."Path" = unit."Path"
                WHERE
                    unit.value IS NOT NULL AND
                    document."Id" = @DocumentId
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "StoredFiles" AS file
                            JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                        WHERE
                            file."Id" = document."StoredFileId"
                            AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId)
                    );
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return await Context.ReadSql.ReadOptionalAsync<ReadingSectionV1>(sql, CancellationToken) ?? throw new ServiceUnavailableException("reading_unavailable");
        }
    }

    [ServiceEndpoint("GET", "/api/v1/reading/documents/{documentId:long}/search")]
    public sealed class SearchReadingV1 : UserService<ReadingSearchParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<ReadingSearchHitV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadingSearchParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingSearchParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The cached sections of one book.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    substring(resource."Path" FROM 10)::integer AS "SectionIndex",
                    document."Structure" -> 'Sections' -> substring(resource."Path" FROM 10)::integer ->> 'Title' AS "Title",
                    convert_from(resource."Bytes", 'UTF8') AS "Html"
                FROM
                    "ReadingDocuments" AS document
                    JOIN "ReadingResources" AS resource ON resource."ReadingDocumentId" = document."Id"
                WHERE
                    document."Id" = @DocumentId
                    AND resource."Path" LIKE ':section/%'
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "StoredFiles" AS file
                            JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                        WHERE
                            file."Id" = document."StoredFileId"
                            AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId)
                    )
                ORDER BY
                    resource."Path";
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            var sections = await Context.ReadSql.ReadListAsync<ReadingSectionTextData>(sql, CancellationToken);

            // Words as read, entities decoded; the first 50 sections that contain them.
            var query = string.Join(' ', Parameters.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var hits = new List<ReadingSearchHitV1>();
            foreach (var section in sections)
            {
                var text = ReadingHtml.Text(section.Html);
                var first = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (first < 0)
                {
                    continue;
                }

                var matches = 0;
                for (var index = first; index >= 0; index = text.IndexOf(query, index + query.Length, StringComparison.OrdinalIgnoreCase))
                {
                    matches++;
                }

                // A converted PDF's hit names the page the match stands on.
                var page = section.Html.Contains(ReadingHtml.PdfPageAttribute, StringComparison.Ordinal) ? ReadingHtml.PageOfText(section.Html, query) : null;
                var from = Math.Max(0, first - 120);
                hits.Add(new ReadingSearchHitV1(section.SectionIndex, section.Title, text.Substring(from, Math.Min(300, text.Length - from)).Trim(), matches, page));
                if (hits.Count == 50)
                {
                    break;
                }
            }

            return new BaseDataList<ReadingSearchHitV1>(hits);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/reading/documents/{documentId:long}/resource")]
    public sealed class ReadReadingResourceV1 : UserService<ReadingResourceParametersV1>
    {
        private readonly ReadingContent _content;

        internal ReadReadingResourceV1(ReadingContent content)
        {
            _content = content;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReadingResourceFileV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadingResourceParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingResourceParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Pictures and pages only, from the disk or the book's file; sections go through the reader.
            if (Parameters.Path.StartsWith(':') && !Parameters.Path.StartsWith(":page/", StringComparison.Ordinal))
            {
                throw new KeyNotFoundException("The resource was not found.");
            }

            var source = await ReadingContent.ReadFileSourceAsync(Context, Parameters.DocumentId, Auth.ProfileId!.Value, Parameters.Path, CancellationToken)
                ?? throw new KeyNotFoundException("The resource was not found.");
            var file = await _content.ReadFileAsync(Context, source, Parameters.Path, CancellationToken) ?? throw new KeyNotFoundException("The resource was not found.");
            if (!file.MediaType.StartsWith("image/", StringComparison.Ordinal))
            {
                throw new KeyNotFoundException("The resource was not found.");
            }

            var content = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            return new ReadingResourceFileV1(content, file.MediaType, "\"" + file.ETag + "\"", file.LastModified, source.ReadingFormatTypeId != ReadingFormatType.Pdf);
        }
    }

    // A page of a book whose pages are drawn when first shown (DjVu): from the disk, else drawn now and kept there. The Reader asks for it through its own page.
    public sealed class RenderReadingPageV1 : UserService<ReadingSectionParametersV1, NoData>
    {
        private readonly ReadingContent _content;

        internal RenderReadingPageV1(ReadingContent content)
        {
            _content = content;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReadingResourceV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadingSectionParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingSectionParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // The DjVu file of a document the profile may read, whether the document still matches it, and the page if it is drawn already.
            var source = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        document."Id" AS "DocumentId",
                        file."RelativePath",
                        root."RelativePath" AS "RootRelativePath",
                        mount."Id" AS "StorageMountId",
                        jsonb_array_length(document."Structure" -> 'Sections') AS "SectionCount",
                        (document."CacheCompletedAt" IS NOT NULL
                            AND file."MissingSince" IS NULL
                            AND file."SizeBytes" = document."SourceSizeBytes"
                            AND file."ModifiedAt" = document."SourceModifiedAt") AS "IsCurrent",
                        coalesce((SELECT settings."ReadingCacheBytes" FROM "InstanceSettings" AS settings), 2147483648) AS "CacheBudgetBytes"
                    FROM
                        "ReadingDocuments" AS document
                        JOIN "StoredFiles" AS file ON file."Id" = document."StoredFileId"
                        JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                        JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                        JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                    WHERE
                        document."Id" = @DocumentId
                        AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId)
                        AND document."ReadingFormatTypeId" = 4
                        AND coalesce((document."Structure" ->> 'RendersPages')::boolean, FALSE)
                        AND file."RelativePath" ~* '\.(djvu|djv)$'
                        AND @SectionIndex < jsonb_array_length(document."Structure" -> 'Sections');
                    """;
                sql.Parameters.Add(Parameters);
                sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
                return ctx.ReadSql.ReadOptionalAsync<ReadingPageSourceData>(sql, ct);
            }, CancellationToken) ?? throw new KeyNotFoundException("The page was not found.");

            return await _content.RenderPageAsync(Context, source, Parameters.SectionIndex, CancellationToken);
        }
    }

    // A book's language for the profile on all its devices; empty follows the profile's language again.
    [ServiceEndpoint("PUT", "/api/v1/reading/assets/{mediaAssetId:long}/language")]
    public sealed class SaveReadingLanguageV1 : UserService<ReadingAssetParametersV1, SaveReadingLanguageDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReadingAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingAssetParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SaveReadingLanguageAsync(new ReadingLanguageData(Auth.ProfileId!.Value, Parameters.MediaAssetId, Data.ReadingLanguage), CancellationToken);
            return new UpdateResult(true);
        }
    }

    // A prepared book as EPUB: a converted PDF beside its original, or any book once the server's translation covers every section.
    [ServiceEndpoint("GET", "/api/v1/reading/documents/{documentId:long}/ebook")]
    public sealed class ReadReadingEbookV1 : UserService<ReadingEbookParametersV1>
    {
        private readonly BookTranslationProgress _progress;
        private readonly ReadingContent _content;

        internal ReadReadingEbookV1(BookTranslationProgress progress, ReadingContent content)
        {
            _progress = progress;
            _content = content;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReadingEbookV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadingEbookParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingEbookParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    document."Id" AS "DocumentId",
                    coalesce(visible."Title", document."Title") AS "Title",
                    document."Structure",
                    coalesce(document."Language", work."OriginalLanguage") AS "Language",
                    document."CacheCompletedAt",
                    coalesce(visible."MediaSubtypeId" = 2, FALSE) AS "IsRightToLeft"
                FROM
                    "ReadingDocuments" AS document
                    JOIN "StoredFiles" AS file ON file."Id" = document."StoredFileId"
                    JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                    -- This Work alone: joined directly, the view reads the catalog up to it.
                    JOIN LATERAL (SELECT visible."Title", visible."MediaSubtypeId" FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId OFFSET 0) AS visible ON TRUE
                    JOIN "Works" AS work ON work."Id" = asset."WorkId"
                WHERE
                    document."Id" = @DocumentId
                    AND document."CacheCompletedAt" IS NOT NULL
                    AND (document."ReadingFormatTypeId" = 2 OR (document."ReadingFormatTypeId" = 1 AND @Language IS NOT NULL));
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            var document = await Context.ReadSql.ReadOptionalAsync<ReadingEbookDocumentData>(sql, CancellationToken) ?? throw new KeyNotFoundException("The book was not found.");

            var resources = new SqlStatement();
            resources.Statement =
                """
                SELECT
                    resource."Path",
                    resource."MediaType",
                    resource."Bytes"
                FROM
                    "ReadingResources" AS resource
                WHERE
                    resource."ReadingDocumentId" = @DocumentId
                    AND (
                        resource."Path" LIKE ':section/%'
                        OR (resource."Path" ~ '^:localtranslation/v4/[^/]+/[a-z]{2,3}/[0-9]{5}$' AND split_part(resource."Path", '/', 4) = @Language)
                    );
                """;
            resources.Parameters.Add(Parameters);
            var stored = (await Context.ReadSql.ReadListAsync<ReadingEbookResourceData>(resources, CancellationToken)).ToDictionary(resource => resource.Path, StringComparer.Ordinal);
            var structure = JsonSerializer.Deserialize<ReadingStructure>(document.Structure)!;
            var sections = structure.Sections.Select((_, index) => stored.TryGetValue(ReadingContent.SectionPath(index), out var section) ? Encoding.UTF8.GetString(section.Bytes) : "").ToArray();

            ReadingEbookTranslation? translation = null;
            if (Parameters.Language is not null)
            {
                var choices = new SqlStatement();
                choices.Statement =
                    """
                    SELECT
                        choice."SectionIndex",
                        choice."BlockIndex",
                        choice."Model"
                    FROM
                        "ReadingTranslationChoices" AS choice
                    WHERE
                        choice."ReadingDocumentId" = @DocumentId
                        AND choice."Language" = @Language;
                    """;
                choices.Parameters.Add(Parameters);
                var chosen = await Context.ReadSql.ReadListAsync<ReadingEbookChoiceData>(choices, CancellationToken);
                var setup = await TranslationProviders.SetupAsync(Context, CancellationToken);
                translation = Translation(structure, sections, stored, chosen, document.Language ?? "und", setup.Model, _progress.Snapshot().ModelKey)
                    ?? throw new KeyNotFoundException("The book is not translated into that language in every section.");
            }

            // The pictures the sections show, from the disk or the book's file.
            var images = new List<ReadingEbookResourceData>();
            if (await ReadingContent.ReadFileSourceAsync(Context, document.DocumentId, Auth.ProfileId!.Value, null, CancellationToken) is { } source)
            {
                foreach (var path in sections.SelectMany(section => ReadingHtml.ResourcePaths(section, document.DocumentId)).Distinct(StringComparer.Ordinal))
                {
                    if (await _content.ReadFileAsync(Context, source, path, CancellationToken) is { } file && file.MediaType.StartsWith("image/", StringComparison.Ordinal))
                    {
                        images.Add(new ReadingEbookResourceData(path, file.MediaType, await File.ReadAllBytesAsync(file.FullPath, CancellationToken)));
                    }
                }
            }

            var original = document.Language ?? "und";
            var bytes = ReadingEbook.Build(new ReadingEbookContent(document.DocumentId, "urn:jularr:reading:" + document.DocumentId.ToString(CultureInfo.InvariantCulture) + (Parameters.Language is null ? "" : ":" + Parameters.Language),
                document.Title, Parameters.Language ?? original, document.CacheCompletedAt, document.IsRightToLeft,
                sections, [.. structure.Sections.Select(section => section.Title)], translation, structure.Toc, images));
            return new ReadingEbookV1(bytes, FileName(document.Title, Parameters.Language));
        }

        // The server's translation, block by block: a block someone re-translated with another model in that model's text, every other block in the text of
        // the first model of the default order that has one (the Owner's model, the model the book job translates with, the TranslateGemma models from the
        // largest, then any other); earlier versions of a model stand in until they are replaced. None while a block has no server text at all (the Mozilla
        // browser translation is never part of it).
        private static ReadingEbookTranslation? Translation(ReadingStructure structure, string[] sections, IReadOnlyDictionary<string, ReadingEbookResourceData> stored,
            IReadOnlyList<ReadingEbookChoiceData> chosen, string from, string? ownerModel, string? jobModelKey)
        {
            // Every model's cache of every section, read once.
            var caches = new Dictionary<(string Key, int Section), LocalTranslationCache>();
            foreach (var resource in stored.Values.Where(resource => resource.Path.StartsWith(":localtranslation/", StringComparison.Ordinal)))
            {
                var parts = resource.Path.Split('/');
                var section = int.Parse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture);
                if (section < sections.Length
                    && LocalReadingPaths.Parse(Encoding.UTF8.GetString(resource.Bytes), sections[section], structure.Sections[section].Blocks) is { } cache)
                    caches[(parts[2], section)] = cache;
            }

            string? Visible(string key, int section, int block) =>
                caches.TryGetValue((key, section), out var cache) ? LocalReadingPaths.Visible(cache, block).Text : null;

            var choices = chosen.ToDictionary(choice => (choice.SectionIndex, choice.BlockIndex), choice => LocalReadingPaths.ModelKey(choice.Model));
            var owner = ownerModel is null ? null : LocalReadingPaths.ModelKey(ownerModel);
            int Rank(string key)
            {
                if (key == owner) return -2;
                if (key == jobModelKey) return -1;
                var size = Array.IndexOf(OllamaLocalModels.Candidates, key);
                return size >= 0 ? OllamaLocalModels.Candidates.Length - size : int.MaxValue;
            }

            string[] order = [.. caches.Keys.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).OrderBy(Rank).ThenBy(key => key, StringComparer.Ordinal)];
            var blocks = new IReadOnlyList<string>[sections.Length];
            var used = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var section = 0; section < sections.Length; section++)
            {
                var texts = new string[structure.Sections[section].Blocks];
                for (var block = 0; block < texts.Length; block++)
                {
                    var model = choices.TryGetValue((section, block), out var choice) && Visible(choice, section, block) is not null
                        ? choice : order.FirstOrDefault(key => Visible(key, section, block) is not null);
                    if (model is null || Visible(model, section, block) is not { } text) return null;
                    texts[block] = text;
                    if (text.Length > 0) used[model] = used.GetValueOrDefault(model) + 1;
                }
                blocks[section] = texts;
            }

            return new ReadingEbookTranslation(from, blocks, [.. used.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => LocalReadingPaths.ModelId(entry.Key))]);
        }

        // The title without characters file systems refuse, and the language of a translation.
        private static string FileName(string title, string? language)
        {
            var refused = Path.GetInvalidFileNameChars().Concat(['"', '\\', '/', ':', '*', '?', '<', '>', '|']).ToHashSet();
            var name = string.Concat(title.Select(character => refused.Contains(character) || char.IsControl(character) ? '_' : character)).Trim();
            name = name.Length > 120 ? name[..120].Trim() : name;
            return (name.Length == 0 ? "book" : name) + (language is null ? "" : " (" + language + ")") + ".epub";
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/reading/assets/{mediaAssetId:long}/progress")]
    public sealed class SaveReadingProgressV1 : UserService<ReadingAssetParametersV1, SaveReadingProgressDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReadingAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingAssetParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SaveReadingProgressAsync(new ReadingProgressData(Auth.ProfileId!.Value, Parameters.MediaAssetId, Data.Locator, Data.Progression, Data.IsCompleted), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
