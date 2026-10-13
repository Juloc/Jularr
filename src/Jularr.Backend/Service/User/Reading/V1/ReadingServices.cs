using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Service.Core;

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
