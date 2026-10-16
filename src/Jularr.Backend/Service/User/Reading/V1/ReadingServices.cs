using System.Globalization;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.User.Reading.V1
{
    [ServiceEndpoint("POST", "/api/v1/reading/assets/{mediaAssetId:long}/open")]
    public sealed class OpenReadingV1 : UserService<ReadingAssetParametersV1, NoData>
    {
        private readonly ReadingContent _content;

        internal OpenReadingV1(ReadingContent content)
        {
            _content = content;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReadingDocumentV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadingAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingAssetParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // The newest readable EPUB or comic of the asset, else its newest PDF; a matching prepared document is reused.
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
                        JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                        JOIN "Works" AS work ON work."Id" = asset."WorkId"
                        JOIN LATERAL (
                            SELECT
                                candidate.*
                            FROM
                                "StoredFiles" AS candidate
                            WHERE
                                candidate."MediaAssetId" = asset."Id"
                                AND candidate."RelativePath" ~* '\.(epub|cbz|cbr|cb7|pdf)$'
                            ORDER BY
                                candidate."MissingSince" IS NULL DESC,
                                candidate."RelativePath" ~* '\.pdf$',
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

            var documentId = await _content.OpenAsync(Context, source, CancellationToken);
            return await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        document."Id" AS "DocumentId",
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
                        ARRAY(
                            SELECT
                                split_part(translation."Path", '/', 2)
                            FROM
                                "ReadingResources" AS translation
                            WHERE
                                translation."ReadingDocumentId" = document."Id"
                                AND translation."Path" LIKE ':translation/%'
                            GROUP BY
                                1
                            HAVING
                                count(*) >= (SELECT count(*) FROM jsonb_array_elements(document."Structure" -> 'Sections') AS section WHERE (section ->> 'Blocks')::int > 0)
                            ORDER BY
                                1
                        ) AS "TranslatedLanguages"
                    FROM
                        "ReadingDocuments" AS document
                        JOIN "MediaAssets" AS asset ON asset."Id" = @MediaAssetId
                        JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                        JOIN "Works" AS work ON work."Id" = asset."WorkId"
                        LEFT JOIN "MediaProgress" AS progress ON progress."ProfileId" = @ProfileId
                            AND progress."WorkId" = asset."WorkId"
                            AND progress."WorkUnitId" IS NOT DISTINCT FROM asset."WorkUnitId"
                            AND progress."ProgressPositionTypeId" = 2
                        LEFT JOIN "ReadingLanguages" AS chosen ON chosen."ProfileId" = @ProfileId AND chosen."WorkId" = asset."WorkId"
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
            // EPUB and comics are prepared; a book whose only present file is a PDF opens as one.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id" AS "WorkId",
                    visible."Title" AS "Title",
                    CASE
                        WHEN bool_or(file."RelativePath" ~* '\.pdf$' AND file."MissingSince" IS NULL)
                            AND NOT bool_or(file."RelativePath" ~* '\.(epub|cbz|cbr|cb7)$' AND file."MissingSince" IS NULL) THEN 2
                        WHEN bool_or(file."RelativePath" ~* '\.(cbz|cbr|cb7)$') AND NOT bool_or(file."RelativePath" ~* '\.epub$') THEN 4
                        ELSE 1
                    END::smallint AS "ReadingFormatTypeId"
                FROM
                    "MediaAssets" AS asset
                    JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                    JOIN "Works" AS work ON work."Id" = asset."WorkId"
                    JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."RelativePath" ~* '\.(epub|cbz|cbr|cb7|pdf)$'
                WHERE
                    asset."Id" = @MediaAssetId
                    AND asset."MediaAssetTypeId" IN (3, 4)
                GROUP BY
                    work."Id",
                    visible."Title";
                """;
            sql.Parameters.Add(Parameters);
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
                            JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                        WHERE
                            file."Id" = document."StoredFileId"
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
            // Served from the cache; an evicted section needs the storage again.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    @SectionIndex AS "SectionIndex",
                    document."Structure" -> 'Sections' -> @SectionIndex ->> 'Title' AS "Title",
                    convert_from(resource."Bytes", 'UTF8') AS "Html",
                    (document."Structure" -> 'Sections' -> @SectionIndex ->> 'Characters')::bigint AS "Characters",
                    (
                        SELECT
                            coalesce(sum((section.value ->> 'Characters')::bigint), 0)
                        FROM
                            jsonb_array_elements(document."Structure" -> 'Sections') WITH ORDINALITY AS section (value, position)
                        WHERE
                            section.position <= @SectionIndex
                    )::bigint AS "CharactersBefore",
                    document."TotalCharacters",
                    (document."Structure" -> 'Sections' -> @SectionIndex ->> 'Blocks')::integer AS "Blocks"
                FROM
                    "ReadingDocuments" AS document
                    JOIN "ReadingResources" AS resource ON resource."ReadingDocumentId" = document."Id"
                        AND resource."Path" = ':section/' || lpad(@SectionIndex::text, 5, '0')
                WHERE
                    document."Id" = @DocumentId
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "StoredFiles" AS file
                            JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                            JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                        WHERE
                            file."Id" = document."StoredFileId"
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
                            JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                        WHERE
                            file."Id" = document."StoredFileId"
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

                var from = Math.Max(0, first - 120);
                hits.Add(new ReadingSearchHitV1(section.SectionIndex, section.Title, text.Substring(from, Math.Min(300, text.Length - from)).Trim(), matches));
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
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReadingResourceV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadingResourceParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingResourceParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Images only; sections go through the reader.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    resource."Bytes",
                    resource."MediaType" AS "ContentType"
                FROM
                    "ReadingResources" AS resource
                    JOIN "ReadingDocuments" AS document ON document."Id" = resource."ReadingDocumentId"
                WHERE
                    resource."ReadingDocumentId" = @DocumentId
                    AND resource."Path" = @Path
                    AND resource."MediaType" LIKE 'image/%'
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "StoredFiles" AS file
                            JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                            JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                        WHERE
                            file."Id" = document."StoredFileId"
                    );
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return await Context.ReadSql.ReadOptionalAsync<ReadingResourceV1>(sql, CancellationToken) ?? throw new KeyNotFoundException("The resource was not found.");
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

    // A prepared book as EPUB: a converted PDF beside its original, or any book once its shared translation covers every section.
    [ServiceEndpoint("GET", "/api/v1/reading/documents/{documentId:long}/ebook")]
    public sealed class ReadReadingEbookV1 : UserService<ReadingEbookParametersV1>
    {
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
                    JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
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
                    AND (resource."Path" LIKE ':section/%' OR resource."MediaType" LIKE 'image/%' OR resource."Path" LIKE ':translation/' || @Language || '/%');
                """;
            resources.Parameters.Add(Parameters);
            var stored = (await Context.ReadSql.ReadListAsync<ReadingEbookResourceData>(resources, CancellationToken)).ToDictionary(resource => resource.Path, StringComparer.Ordinal);
            var structure = JsonSerializer.Deserialize<ReadingStructure>(document.Structure)!;
            var sections = structure.Sections.Select((_, index) => stored.TryGetValue(ReadingContent.SectionPath(index), out var section) ? Encoding.UTF8.GetString(section.Bytes) : "").ToArray();

            // Only a finished translation downloads; a section without blocks has nothing to translate.
            IReadOnlyList<IReadOnlyList<string>>? translations = null;
            if (Parameters.Language is { } language)
            {
                var translated = structure.Sections.Select((section, index) => stored.TryGetValue(ReadingTranslations.Path(index, language), out var translation)
                    ? ReadingTranslations.Parse(Encoding.UTF8.GetString(translation.Bytes))
                    : section.Blocks == 0 ? [] : null).ToArray();
                if (translated.Any(blocks => blocks is null))
                {
                    throw new ServiceUnavailableException("reading_translation_incomplete");
                }

                translations = translated!;
            }

            var original = document.Language ?? "und";
            var bytes = ReadingEbook.Build(new ReadingEbookContent(document.DocumentId, "urn:jularr:reading:" + document.DocumentId.ToString(CultureInfo.InvariantCulture) + (Parameters.Language is null ? "" : ":" + Parameters.Language),
                document.Title, Parameters.Language ?? original, Parameters.Language is null ? null : original, document.CacheCompletedAt, document.IsRightToLeft,
                sections, [.. structure.Sections.Select(section => section.Title)], translations, structure.Toc, [.. stored.Values.Where(resource => resource.MediaType.StartsWith("image/", StringComparison.Ordinal))]));
            return new ReadingEbookV1(bytes, FileName(document.Title, Parameters.Language));
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
