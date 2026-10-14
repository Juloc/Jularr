using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Reading;
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
            // The newest readable EPUB or comic of the asset; a matching prepared document is reused.
            var source = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        work."Id" AS "WorkId",
                        work."CanonicalTitle" AS "Title",
                        file."Id" AS "StoredFileId",
                        file."RelativePath",
                        file."SizeBytes",
                        file."ModifiedAt",
                        file."MissingSince" IS NOT NULL AS "IsMissing",
                        root."RelativePath" AS "RootRelativePath",
                        mount."Id" AS "StorageMountId",
                        mount."StorageProtocolTypeId",
                        mount."SharePath",
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
                                AND candidate."RelativePath" ~* '\.(epub|cbz|cbr|cb7)$'
                            ORDER BY
                                candidate."MissingSince" IS NULL DESC,
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
                                AND prepared."AnalyzerVersion" = CASE WHEN prepared."ReadingFormatTypeId" = 4 THEN @ComicAnalyzerVersion ELSE @AnalyzerVersion END
                            ORDER BY
                                prepared."CacheCompletedAt" IS NOT NULL DESC,
                                prepared."Id" DESC
                            LIMIT 1
                        ) AS document ON TRUE
                    WHERE
                        asset."Id" = @MediaAssetId
                        AND asset."MediaAssetTypeId" IN (3, 4);
                    """;
                sql.Parameters.Add(new ReadingSourceLookupData(Parameters.MediaAssetId, ReadingHtml.AnalyzerVersion, ComicPages.AnalyzerVersion));
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
                        coalesce(work."CanonicalTitle", document."Title") AS "Title",
                        document."ReadingFormatTypeId",
                        jsonb_array_length(document."Structure" -> 'Sections') AS "SectionCount",
                        document."CacheCompletedAt" IS NOT NULL AS "IsCached",
                        progress."Locator",
                        progress."Progression",
                        coalesce(visible."MediaSubtypeId" = 2, FALSE) AS "IsRightToLeft"
                    FROM
                        "ReadingDocuments" AS document
                        JOIN "MediaAssets" AS asset ON asset."Id" = @MediaAssetId
                        JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                        JOIN "Works" AS work ON work."Id" = asset."WorkId"
                        LEFT JOIN "MediaProgress" AS progress ON progress."ProfileId" = @ProfileId
                            AND progress."WorkId" = asset."WorkId"
                            AND progress."WorkUnitId" IS NOT DISTINCT FROM asset."WorkUnitId"
                            AND progress."ProgressPositionTypeId" = 2
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
                    work."CanonicalTitle" AS "Title",
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
                    work."CanonicalTitle";
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return await Context.ReadSql.ReadOptionalAsync<ReadingSourceV1>(sql, CancellationToken) ?? throw new KeyNotFoundException("The book was not found.");
        }
    }

    [ServiceEndpoint("GET", "/api/v1/reading/assets/{mediaAssetId:long}/file")]
    public sealed class ReadReadingFileV1 : UserService<ReadingAssetParametersV1>
    {
        private readonly StorageOptions _options;

        internal ReadReadingFileV1(IOptions<StorageOptions> options)
        {
            _options = options.Value;
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
                    mount."Id" AS "StorageMountId",
                    mount."StorageProtocolTypeId",
                    mount."SharePath"
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
                var directory = StorageFileSystem.ResolveDirectory(_options.GetConnectionPath(source.StorageProtocolTypeId, source.StorageMountId, source.SharePath), source.RootRelativePath);
                return new ReadingFileV1(StorageFileSystem.ResolveFile(directory, source.RelativePath));
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
