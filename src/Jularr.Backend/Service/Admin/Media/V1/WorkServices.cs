using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/works")]
    public sealed class ReadWorksV1 : AdminService<ReadWorksParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<WorkListItemV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadWorksParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadWorksParametersV1 parameters, Type resultType) => new("media.read");
        protected override PageRequest GetPageRequest(ReadWorksParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // One bounded read: the titles are filtered and ordered first, only the page gets its cover. A title is monitored while one of its targets is not paused;
            // it is filed when it has a file and no monitored aired unit still lacks one, and downloading while one of its releases is on its way.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH listed AS (
                    SELECT
                        listed."Id",
                        listed."MediaTypeId",
                        listed."MediaSubtypeId",
                        listed."CanonicalTitle",
                        listed."ReleaseYear",
                        listed."CreatedAt",
                        listed."WantedItemId",
                        listed."TargetCount",
                        listed."IsMonitored",
                        listed."AcquisitionProfileId",
                        listed."LibraryRootId",
                        listed."ProfileName",
                        listed."LibraryName",
                        listed."FileCount",
                        listed."SizeBytes",
                        listed."WorkListStatusId"
                    FROM
                        "WorkListEntries" AS listed
                    WHERE
                        (@MediaTypeId::smallint IS NULL OR listed."MediaTypeId" = @MediaTypeId)
                        AND (@Query::text IS NULL OR strpos(lower(listed."CanonicalTitle"), lower(@Query)) > 0)
                        AND (@AcquisitionProfileId::bigint IS NULL OR listed."AcquisitionProfileId" = @AcquisitionProfileId)
                        AND (
                            @LibraryRootId::bigint IS NULL OR listed."LibraryRootId" = @LibraryRootId
                            OR EXISTS (
                                SELECT 1 FROM "MediaAssets" AS asset
                                    JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."MissingSince" IS NULL AND NOT file."IsSubtitle"
                                WHERE asset."WorkId" = listed."Id" AND file."LibraryRootId" = @LibraryRootId
                            )
                        )
                ), ordered AS (
                    SELECT
                        listed."Id",
                        listed."MediaTypeId",
                        listed."MediaSubtypeId",
                        listed."CanonicalTitle",
                        listed."ReleaseYear",
                        listed."CreatedAt",
                        listed."WantedItemId",
                        listed."TargetCount",
                        listed."IsMonitored",
                        listed."AcquisitionProfileId",
                        listed."LibraryRootId",
                        listed."ProfileName",
                        listed."LibraryName",
                        listed."FileCount",
                        listed."SizeBytes",
                        listed."WorkListStatusId",
                        row_number() OVER (
                            ORDER BY
                                CASE WHEN @WorkSortTypeId = 0 AND NOT @IsDescending THEN lower(listed."CanonicalTitle") END ASC,
                                CASE WHEN @WorkSortTypeId = 0 AND @IsDescending THEN lower(listed."CanonicalTitle") END DESC,
                                CASE WHEN @WorkSortTypeId = 1 AND NOT @IsDescending THEN listed."MediaTypeId" END ASC,
                                CASE WHEN @WorkSortTypeId = 1 AND @IsDescending THEN listed."MediaTypeId" END DESC,
                                CASE WHEN @WorkSortTypeId = 2 AND NOT @IsDescending THEN listed."ReleaseYear" END ASC NULLS LAST,
                                CASE WHEN @WorkSortTypeId = 2 AND @IsDescending THEN listed."ReleaseYear" END DESC NULLS LAST,
                                CASE WHEN @WorkSortTypeId = 3 AND NOT @IsDescending THEN listed."CreatedAt" END ASC,
                                CASE WHEN @WorkSortTypeId = 3 AND @IsDescending THEN listed."CreatedAt" END DESC,
                                CASE WHEN @WorkSortTypeId = 4 AND NOT @IsDescending THEN listed."SizeBytes" END ASC,
                                CASE WHEN @WorkSortTypeId = 4 AND @IsDescending THEN listed."SizeBytes" END DESC,
                                CASE WHEN @WorkSortTypeId = 5 AND NOT @IsDescending THEN listed."FileCount" END ASC,
                                CASE WHEN @WorkSortTypeId = 5 AND @IsDescending THEN listed."FileCount" END DESC,
                                lower(listed."CanonicalTitle"),
                                listed."Id"
                        ) AS "Position"
                    FROM
                        listed
                    WHERE
                        (@IsMonitored::boolean IS NULL OR listed."IsMonitored" = @IsMonitored)
                        AND (@WorkListStatusId::smallint IS NULL OR listed."WorkListStatusId" = @WorkListStatusId)
                )
                SELECT
                    ordered."Id",
                    ordered."MediaTypeId",
                    ordered."MediaSubtypeId",
                    ordered."CanonicalTitle",
                    ordered."ReleaseYear",
                    ordered."CreatedAt",
                    facts."PosterUrl",
                    facts."Overview",
                    ordered."WantedItemId",
                    ordered."TargetCount",
                    ordered."IsMonitored",
                    ordered."ProfileName",
                    ordered."LibraryName",
                    ordered."FileCount",
                    ordered."SizeBytes",
                    ordered."WorkListStatusId"
                FROM
                    ordered
                    LEFT JOIN LATERAL (
                        SELECT
                            (array_agg(fact."Value" ORDER BY fact."IsManual" DESC) FILTER (WHERE fact."FactKey" = 'Poster'))[1] AS "PosterUrl",
                            left((array_agg(fact."Value" ORDER BY fact."IsManual" DESC) FILTER (WHERE fact."FactKey" = 'Overview'))[1], 600) AS "Overview"
                        FROM
                            "WorkMetadataFacts" AS fact
                        WHERE
                            fact."WorkId" = ordered."Id"
                            AND fact."FactKey" IN ('Poster', 'Overview')
                    ) AS facts ON TRUE
                WHERE
                    ordered."Position" > @Offset
                    AND ordered."Position" <= @Offset + @PageSize
                ORDER BY
                    ordered."Position";
                """;
            sql.Parameters.Add(new AdminWorkListFilterData(Parameters.MediaTypeId, Parameters.IsMonitored, Parameters.WorkListStatusId, Parameters.LibraryRootId, Parameters.AcquisitionProfileId, Parameters.Query,
                Parameters.WorkSortTypeId, Parameters.IsDescending));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<WorkListItemV1>(sql, CancellationToken);
        }
    }

    // The same filters as the list (its status, monitoring, order and page do not count): how many titles are in each state.
    [ServiceEndpoint("GET", "/api/v1/admin/works/summary")]
    public sealed class ReadWorksSummaryV1 : AdminService<ReadWorksParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorksSummaryV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadWorksParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadWorksParametersV1 parameters, Type resultType) => new("media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH listed AS (
                    SELECT listed."WorkListStatusId", listed."IsMonitored"
                    FROM
                        "WorkListEntries" AS listed
                    WHERE
                        (@MediaTypeId::smallint IS NULL OR listed."MediaTypeId" = @MediaTypeId)
                        AND (@Query::text IS NULL OR strpos(lower(listed."CanonicalTitle"), lower(@Query)) > 0)
                        AND (@AcquisitionProfileId::bigint IS NULL OR listed."AcquisitionProfileId" = @AcquisitionProfileId)
                        AND (
                            @LibraryRootId::bigint IS NULL OR listed."LibraryRootId" = @LibraryRootId
                            OR EXISTS (
                                SELECT 1 FROM "MediaAssets" AS asset
                                    JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."MissingSince" IS NULL AND NOT file."IsSubtitle"
                                WHERE asset."WorkId" = listed."Id" AND file."LibraryRootId" = @LibraryRootId
                            )
                        )
                )
                SELECT
                    count(*)::integer AS "TotalCount",
                    count(*) FILTER (WHERE listed."WorkListStatusId" = 1)::integer AS "AvailableCount",
                    count(*) FILTER (WHERE listed."WorkListStatusId" = 2)::integer AS "MissingCount",
                    count(*) FILTER (WHERE listed."WorkListStatusId" = 3)::integer AS "DownloadingCount",
                    count(*) FILTER (WHERE listed."WorkListStatusId" = 4)::integer AS "UnmonitoredCount",
                    count(*) FILTER (WHERE listed."IsMonitored")::integer AS "MonitoredCount"
                FROM
                    listed;
                """;
            sql.Parameters.Add(new AdminWorkListFilterData(Parameters.MediaTypeId, null, null, Parameters.LibraryRootId, Parameters.AcquisitionProfileId, Parameters.Query, WorkSortType.Title, false));
            return await Context.ReadSql.ReadRequiredAsync<WorksSummaryV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/works/{workId:long}")]
    public sealed class ReadWorkV1 : AdminService<WorkParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorkDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id",
                    work."MediaTypeId",
                    work."MediaSubtypeId",
                    work."IsMediaSubtypeManual",
                    work."CanonicalTitle",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    work."Version",
                    (SELECT wanted."Id" FROM "WantedItems" AS wanted WHERE wanted."WorkId" = work."Id" ORDER BY wanted."Id" LIMIT 1) AS "WantedItemId",
                    franchise."Name" AS "FranchiseName",
                    work."FranchisePosition",
                    work."IsFranchiseManual"
                FROM
                    "Works" AS work
                    LEFT JOIN "Franchises" AS franchise ON franchise."Id" = work."FranchiseId"
                WHERE
                    work."Id" = @WorkId;
                """;
            sql.Parameters.Add(Parameters);
            return await Context.ReadSql.ReadRequiredAsync<WorkDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/works/{workId:long}/franchise")]
    public sealed class UpdateWorkFranchiseV1 : AdminService<WorkParametersV1, UpdateWorkFranchiseDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library", "Franchises");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.ChooseWorkFranchiseAsync(new WorkFranchiseChoiceData(Parameters.WorkId, Data.FranchiseName, Data.FranchisePosition), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/works")]
    public sealed class CreateWorkV1 : AdminService<CreateWorkParametersV1, CreateWorkDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorkDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(CreateWorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(CreateWorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnCreate()
        {
            var work = await Context.CreateWorkAsync(new WorkCreationData(Data.MediaTypeId, Data.MediaSubtypeId, true, Data.CanonicalTitle, Data.ReleaseYear), CancellationToken);
            return new WorkDetailsV1(work.Id, work.MediaTypeId, work.MediaSubtypeId, work.IsMediaSubtypeManual, work.CanonicalTitle, work.ReleaseYear, work.Version);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/works/{workId:long}")]
    public sealed class UpdateWorkV1 : AdminService<WorkParametersV1, UpdateWorkDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // Canonical writer checks version.
            await Context.UpdateWorkAsync(new WorkUpdateData(Parameters.WorkId, Data.CanonicalTitle, Data.Version, Data.ReleaseYear, Data.MediaSubtypeId), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
