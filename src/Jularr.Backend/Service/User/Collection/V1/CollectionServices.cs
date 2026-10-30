using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Collection.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Service.User.Library.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Collection.V1
{
    [ServiceEndpoint("GET", "/api/v1/collections")]
    public sealed class ReadCollectionsV1 : UserService<CollectionsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<CollectionV1>>()];
        protected override ModuleRequirement GetInstanceModules(CollectionsParametersV1 parameters) => ModuleRequirement.All("Library", "Collections");
        protected override ServicePermission GetPermission(CollectionsParametersV1 parameters, Type resultType) => new("profile.collections");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    collection."Id",
                    collection."Name",
                    collection."IsSmart",
                    collection."MediaTypeId",
                    collection."MediaSubtypeId",
                    collection."ReleaseYearFrom"::integer AS "ReleaseYearFrom",
                    collection."ReleaseYearTo"::integer AS "ReleaseYearTo",
                    collection."OnlyAvailable",
                    collection."OnlyUnfinished",
                    CASE WHEN NOT collection."IsSmart" THEN (SELECT count(*)::integer FROM "CollectionWorks" AS item WHERE item."CollectionId" = collection."Id") END AS "WorkCount",
                    ARRAY(
                        SELECT
                            poster."Value"
                        FROM
                            "CollectionWorks" AS item
                            JOIN "ProfileWorks" AS visible ON visible."ProfileId" = collection."ProfileId" AND visible."WorkId" = item."WorkId" AND NOT visible."IsHidden"
                            CROSS JOIN LATERAL (
                                SELECT
                                    fact."Value"
                                FROM
                                    "WorkMetadataFacts" AS fact
                                WHERE
                                    fact."WorkId" = item."WorkId"
                                    AND fact."FactKey" = 'Poster'
                                ORDER BY
                                    fact."IsManual" DESC
                                LIMIT 1
                            ) AS poster
                        WHERE
                            item."CollectionId" = collection."Id"
                            -- The profile's view is read for the collection's ids alone, never for the catalog.
                            AND visible."WorkId" = ANY(ARRAY(SELECT picked."WorkId" FROM "CollectionWorks" AS picked WHERE picked."CollectionId" = collection."Id"))
                        ORDER BY
                            item."Position" DESC
                        LIMIT 4
                    ) AS "PosterUrls",
                    collection."Version"
                FROM
                    "Collections" AS collection
                WHERE
                    collection."ProfileId" = @ProfileId
                ORDER BY
                    collection."Name",
                    collection."Id"
                LIMIT 100;
                """;
            sql.DisableJit = true;
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return new BaseDataList<CollectionV1>(await Context.ReadSql.ReadListAsync<CollectionV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/collections/{collectionId:long}")]
    public sealed class ReadCollectionV1 : UserService<CollectionParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CollectionV1>()];
        protected override ModuleRequirement GetInstanceModules(CollectionParametersV1 parameters) => ModuleRequirement.All("Library", "Collections");
        protected override ServicePermission GetPermission(CollectionParametersV1 parameters, Type resultType) => new("profile.collections");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    collection."Id",
                    collection."Name",
                    collection."IsSmart",
                    collection."MediaTypeId",
                    collection."MediaSubtypeId",
                    collection."ReleaseYearFrom"::integer AS "ReleaseYearFrom",
                    collection."ReleaseYearTo"::integer AS "ReleaseYearTo",
                    collection."OnlyAvailable",
                    collection."OnlyUnfinished",
                    NULL::integer AS "WorkCount",
                    ARRAY[]::text[] AS "PosterUrls",
                    collection."Version"
                FROM
                    "Collections" AS collection
                WHERE
                    collection."Id" = @CollectionId
                    AND collection."ProfileId" = @ProfileId;
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return await Context.ReadSql.ReadRequiredAsync<CollectionV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/collections/{collectionId:long}/works")]
    public sealed class ReadCollectionWorksV1 : UserService<CollectionWorksParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<LibraryItemV1>>()];
        protected override ModuleRequirement GetInstanceModules(CollectionWorksParametersV1 parameters) => ModuleRequirement.All("Library", "Collections");
        protected override ServicePermission GetPermission(CollectionWorksParametersV1 parameters, Type resultType) => new("profile.collections");
        protected override PageRequest GetPageRequest(CollectionWorksParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Hand-picked Works in their order, or everything visible that passes the saved filter.
            var sql = new SqlStatement();
            sql.Statement =
                $$"""
                SELECT
                    {{WorkCardProjection.Columns}},
                    EXISTS (SELECT 1 FROM "WatchlistEntries" AS entry WHERE entry."ProfileId" = collection."ProfileId" AND entry."WorkId" = work."Id") AS "IsWatchlisted"
                FROM
                    "Collections" AS collection
                    JOIN "ProfileWorks" AS visible ON visible."ProfileId" = collection."ProfileId" AND NOT visible."IsHidden"
                    JOIN "Works" AS work ON work."Id" = visible."WorkId"
                    {{WorkCardProjection.Joins}}
                    LEFT JOIN "CollectionWorks" AS item ON item."CollectionId" = collection."Id" AND item."WorkId" = work."Id"
                WHERE
                    collection."Id" = @CollectionId
                    AND collection."ProfileId" = @ProfileId
                    AND (collection."IsSmart" OR item."WorkId" IS NOT NULL)
                    AND (collection."MediaTypeId" IS NULL OR visible."MediaTypeId" = collection."MediaTypeId")
                    AND (collection."MediaSubtypeId" IS NULL OR visible."MediaSubtypeId" = collection."MediaSubtypeId")
                    AND (
                        cardinality(@MediaTypeIds::smallint[]) + cardinality(@MediaSubtypeIds::smallint[]) = 0
                        OR visible."MediaSubtypeId" IS NULL AND visible."MediaTypeId" = ANY(@MediaTypeIds::smallint[])
                        OR visible."MediaSubtypeId" = ANY(@MediaSubtypeIds::smallint[])
                    )
                    AND (@ReleaseYearFrom::integer IS NULL OR work."ReleaseYear" >= @ReleaseYearFrom)
                    AND (@ReleaseYearTo::integer IS NULL OR work."ReleaseYear" <= @ReleaseYearTo)
                    AND (@ProgressStateId::smallint IS NULL OR workProgress."ProgressStateId" = @ProgressStateId)
                    AND (collection."ReleaseYearFrom" IS NULL OR work."ReleaseYear" >= collection."ReleaseYearFrom")
                    AND (collection."ReleaseYearTo" IS NULL OR work."ReleaseYear" <= collection."ReleaseYearTo")
                    AND (
                        NOT collection."OnlyAvailable"
                        OR EXISTS (
                            SELECT
                                1
                            FROM
                                "MediaAssets" AS asset
                                JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                            WHERE
                                asset."WorkId" = work."Id"
                        )
                    )
                    AND (
                        NOT collection."OnlyUnfinished"
                        OR NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "MediaProgress" AS progress
                            WHERE
                                progress."ProfileId" = collection."ProfileId"
                                AND progress."WorkId" = work."Id"
                                AND progress."WorkUnitId" IS NULL
                                AND progress."IsCompleted"
                        )
                        AND (
                            NOT EXISTS (SELECT 1 FROM "WorkUnits" AS unit WHERE unit."WorkId" = work."Id")
                            OR EXISTS (
                                SELECT
                                    1
                                FROM
                                    "WorkUnits" AS unit
                                WHERE
                                    unit."WorkId" = work."Id"
                                    AND NOT EXISTS (SELECT 1 FROM "WorkUnits" AS child WHERE child."ParentWorkUnitId" = unit."Id")
                                    AND NOT EXISTS (
                                        SELECT
                                            1
                                        FROM
                                            "MediaProgress" AS progress
                                        WHERE
                                            progress."ProfileId" = collection."ProfileId"
                                            AND progress."WorkUnitId" = unit."Id"
                                            AND progress."IsCompleted"
                                    )
                            )
                        )
                    )
                ORDER BY
                    CASE WHEN @LibrarySortTypeId = 1 THEN item."Position" END DESC NULLS LAST,
                    CASE WHEN @LibrarySortTypeId = 3 THEN work."ReleaseYear" END DESC NULLS LAST,
                    CASE WHEN @LibrarySortTypeId = 4 THEN workProgress."ProgressPercent" END DESC NULLS LAST,
                    CASE WHEN @LibrarySortTypeId = 2 THEN visible."Title" END,
                    item."Position" NULLS LAST,
                    visible."Title",
                    work."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new CollectionOwnerData(Auth.ProfileId!.Value, Parameters.CollectionId));
            sql.Parameters.Add(new WorkListFilterData(Parameters.MediaTypeIds, Parameters.MediaSubtypeIds, Parameters.ReleaseYearFrom, Parameters.ReleaseYearTo, Parameters.ProgressStateId, Parameters.LibrarySortTypeId));
            sql.Parameters.Add(LanguageCodes.Aliases);
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<LibraryItemV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/works/{workId:long}/collections")]
    public sealed class ReadWorkCollectionsV1 : UserService<WorkParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WorkCollectionV1>>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library", "Collections");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("profile.collections");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Hand-picked collections only; filters decide for themselves.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    collection."Id" AS "CollectionId",
                    collection."Name",
                    EXISTS (SELECT 1 FROM "CollectionWorks" AS item WHERE item."CollectionId" = collection."Id" AND item."WorkId" = @WorkId) AS "ContainsWork"
                FROM
                    "Collections" AS collection
                WHERE
                    collection."ProfileId" = @ProfileId
                    AND NOT collection."IsSmart"
                ORDER BY
                    collection."Name",
                    collection."Id"
                LIMIT 100;
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return new BaseDataList<WorkCollectionV1>(await Context.ReadSql.ReadListAsync<WorkCollectionV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/collections/membership")]
    public sealed class ReadCollectionMembershipV1 : UserService<CollectionMembershipParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<CollectionMemberV1>>()];
        protected override ModuleRequirement GetInstanceModules(CollectionMembershipParametersV1 parameters) => ModuleRequirement.All("Library", "Collections");
        protected override ServicePermission GetPermission(CollectionMembershipParametersV1 parameters, Type resultType) => new("profile.collections");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The hand-picked collections of the profile that hold any of the given titles, in one read.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    item."WorkId",
                    item."CollectionId"
                FROM
                    "CollectionWorks" AS item
                    JOIN "Collections" AS collection ON collection."Id" = item."CollectionId"
                WHERE
                    collection."ProfileId" = @ProfileId
                    AND NOT collection."IsSmart"
                    AND item."WorkId" = ANY(@WorkIds)
                ORDER BY
                    item."WorkId",
                    item."CollectionId";
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return new BaseDataList<CollectionMemberV1>(await Context.ReadSql.ReadListAsync<CollectionMemberV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/collections")]
    public sealed class CreateCollectionV1 : UserService<CollectionsParametersV1, CreateCollectionDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateCollectionResultV1>()];
        protected override ModuleRequirement GetInstanceModules(CollectionsParametersV1 parameters) => ModuleRequirement.All("Library", "Collections");
        protected override ServicePermission GetPermission(CollectionsParametersV1 parameters, Type resultType) => new("profile.collections");

        protected override async Task<IServiceOutput> OnCreate() =>
            new CreateCollectionResultV1(await Context.CreateCollectionAsync(new CollectionSaveData(Auth.ProfileId!.Value, null, Data.Name, Data.IsSmart, null, null, null, null, false, false, null), CancellationToken));
    }

    [ServiceEndpoint("PUT", "/api/v1/collections/{collectionId:long}")]
    public sealed class UpdateCollectionV1 : UserService<CollectionParametersV1, UpdateCollectionDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CollectionParametersV1 parameters) => ModuleRequirement.All("Library", "Collections");
        protected override ServicePermission GetPermission(CollectionParametersV1 parameters, Type resultType) => new("profile.collections");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.UpdateCollectionAsync(new CollectionSaveData(Auth.ProfileId!.Value, Parameters.CollectionId, Data.Name, false, Data.MediaTypeId, Data.MediaSubtypeId,
                Data.ReleaseYearFrom, Data.ReleaseYearTo, Data.OnlyAvailable, Data.OnlyUnfinished, Data.Version), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/collections/{collectionId:long}")]
    public sealed class DeleteCollectionV1 : UserService<CollectionParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CollectionParametersV1 parameters) => ModuleRequirement.All("Library", "Collections");
        protected override ServicePermission GetPermission(CollectionParametersV1 parameters, Type resultType) => new("profile.collections");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.DeleteCollectionAsync(new CollectionOwnerData(Auth.ProfileId!.Value, Parameters.CollectionId), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/collections/{collectionId:long}/works/{workId:long}")]
    public sealed class AddCollectionWorkV1 : UserService<CollectionWorkParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CollectionWorkParametersV1 parameters) => ModuleRequirement.All("Library", "Collections");
        protected override ServicePermission GetPermission(CollectionWorkParametersV1 parameters, Type resultType) => new("profile.collections");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.AddCollectionWorkAsync(new CollectionWorkData(Auth.ProfileId!.Value, Parameters.CollectionId, Parameters.WorkId), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/collections/{collectionId:long}/works/{workId:long}")]
    public sealed class RemoveCollectionWorkV1 : UserService<CollectionWorkParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CollectionWorkParametersV1 parameters) => ModuleRequirement.All("Library", "Collections");
        protected override ServicePermission GetPermission(CollectionWorkParametersV1 parameters, Type resultType) => new("profile.collections");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.RemoveCollectionWorkAsync(new CollectionWorkData(Auth.ProfileId!.Value, Parameters.CollectionId, Parameters.WorkId), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
