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
        // Every library title (catalog titles are not listed) with what the list shows of it, as the view WorkListEntries gives it, but worked out once per table
        // instead of once per title: its first target, its present files, whether one of its releases is on its way and whether a monitored aired unit still lacks a file.
        // A title is monitored while one of its targets is not paused; it is filed when it has a file and no monitored aired unit still lacks one.
        internal const string ListedWorks =
            """
                managed AS MATERIALIZED (
                    SELECT DISTINCT
                        tie."WorkId"
                    FROM
                        "WorkManagedRelations" AS tie
                ), targets AS MATERIALIZED (
                    SELECT
                        wanted."WorkId",
                        (array_agg(wanted."Id" ORDER BY wanted."Id"))[1] AS "Id",
                        (array_agg(wanted."AcquisitionProfileId" ORDER BY wanted."Id"))[1] AS "AcquisitionProfileId",
                        (array_agg(wanted."LibraryRootId" ORDER BY wanted."Id"))[1] AS "LibraryRootId",
                        count(*)::integer AS "Targets",
                        bool_or(NOT wanted."IsPaused") AS "IsMonitored"
                    FROM
                        "WantedItems" AS wanted
                    GROUP BY
                        wanted."WorkId"
                ), files AS MATERIALIZED (
                    SELECT
                        asset."WorkId",
                        count(*)::integer AS "FileCount",
                        sum(file."SizeBytes")::bigint AS "SizeBytes",
                        (array_agg(file."LibraryRootId" ORDER BY file."Id"))[1] AS "LibraryRootId"
                    FROM
                        "MediaAssets" AS asset
                        JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."MissingSince" IS NULL AND NOT file."IsSubtitle"
                    GROUP BY
                        asset."WorkId"
                ), downloading AS MATERIALIZED (
                    SELECT DISTINCT
                        wanted."WorkId"
                    FROM
                        "WantedItems" AS wanted
                        JOIN "AcquisitionReleases" AS release ON release."WantedItemId" = wanted."Id"
                        JOIN "Operations" AS operation ON operation."AcquisitionReleaseId" = release."Id"
                    WHERE
                        operation."OperationStatusTypeId" IN (1, 2)
                ), lacking AS MATERIALIZED (
                    -- Only a title with files can be filed, so only those are looked at.
                    SELECT DISTINCT
                        unit."WorkId"
                    FROM
                        files
                        JOIN "Works" AS work ON work."Id" = files."WorkId"
                        JOIN "WorkUnits" AS unit ON unit."WorkId" = files."WorkId"
                    WHERE
                        unit."IsMonitored" AND (unit."WorkUnitTypeId" = 2 OR unit."WorkUnitTypeId" = 3 AND work."MediaSubtypeId" = 2)
                        AND (unit."ReleasedOn" <= current_date OR unit."WorkUnitTypeId" = 3 AND unit."ReleasedOn" IS NULL)
                        AND unit."Id" NOT IN (
                            -- The units a present file covers, once as a set rather than probed unit by unit.
                            SELECT
                                asset."WorkUnitId"
                            FROM
                                "MediaAssets" AS asset
                                JOIN "StoredFiles" AS present ON present."MediaAssetId" = asset."Id" AND present."MissingSince" IS NULL
                            WHERE
                                asset."WorkUnitId" IS NOT NULL
                        )
                ), listed AS MATERIALIZED (
                    SELECT
                        work."Id",
                        work."MediaTypeId",
                        work."MediaSubtypeId",
                        work."CanonicalTitle",
                        work."ReleaseYear"::integer AS "ReleaseYear",
                        work."CreatedAt",
                        target."Id" AS "WantedItemId",
                        coalesce(target."Targets", 0) AS "TargetCount",
                        coalesce(target."IsMonitored", FALSE) AS "IsMonitored",
                        target."AcquisitionProfileId",
                        target."LibraryRootId",
                        profile."Name" AS "ProfileName",
                        coalesce(targetRoot."Name", fileRoot."Name") AS "LibraryName",
                        coalesce(files."FileCount", 0) AS "FileCount",
                        coalesce(files."SizeBytes", 0) AS "SizeBytes",
                        CASE
                            WHEN downloading."WorkId" IS NOT NULL THEN 3
                            WHEN files."FileCount" > 0 AND lacking."WorkId" IS NULL THEN 1
                            WHEN coalesce(target."IsMonitored", FALSE) THEN 2
                            ELSE 4
                        END::smallint AS "WorkListStatusId"
                    FROM
                        managed
                        JOIN "Works" AS work ON work."Id" = managed."WorkId"
                        LEFT JOIN targets AS target ON target."WorkId" = work."Id"
                        LEFT JOIN "AcquisitionProfiles" AS profile ON profile."Id" = target."AcquisitionProfileId"
                        LEFT JOIN "LibraryRoots" AS targetRoot ON targetRoot."Id" = target."LibraryRootId"
                        LEFT JOIN files ON files."WorkId" = work."Id"
                        LEFT JOIN "LibraryRoots" AS fileRoot ON fileRoot."Id" = files."LibraryRootId"
                        LEFT JOIN downloading ON downloading."WorkId" = work."Id"
                        LEFT JOIN lacking ON lacking."WorkId" = work."Id"
            """;

        // The same titles with only what ordering and the other filters read; the page's rows then read their facts from the view, title by title.
        internal const string LeanListedWorks =
            """
                managed AS MATERIALIZED (
                    SELECT DISTINCT
                        tie."WorkId"
                    FROM
                        "WorkManagedRelations" AS tie
                ), targets AS MATERIALIZED (
                    SELECT
                        wanted."WorkId",
                        (array_agg(wanted."AcquisitionProfileId" ORDER BY wanted."Id"))[1] AS "AcquisitionProfileId",
                        (array_agg(wanted."LibraryRootId" ORDER BY wanted."Id"))[1] AS "LibraryRootId",
                        bool_or(NOT wanted."IsPaused") AS "IsMonitored"
                    FROM
                        "WantedItems" AS wanted
                    GROUP BY
                        wanted."WorkId"
                ), listed AS MATERIALIZED (
                    SELECT
                        work."Id",
                        work."MediaTypeId",
                        work."MediaSubtypeId",
                        work."CanonicalTitle",
                        work."ReleaseYear"::integer AS "ReleaseYear",
                        work."CreatedAt",
                        coalesce(target."IsMonitored", FALSE) AS "IsMonitored",
                        target."AcquisitionProfileId",
                        target."LibraryRootId",
                        0 AS "FileCount",
                        0::bigint AS "SizeBytes",
                        NULL::smallint AS "WorkListStatusId"
                    FROM
                        managed
                        JOIN "Works" AS work ON work."Id" = managed."WorkId"
                        LEFT JOIN targets AS target ON target."WorkId" = work."Id"
            """;

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<WorkListItemV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadWorksParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadWorksParametersV1 parameters, Type resultType) => new("media.read");
        protected override PageRequest GetPageRequest(ReadWorksParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // One bounded read: the library's titles are filtered and ordered first, and only the page reads its cover and description.
            // Only a status filter or an order by size or files needs every title's files and state.
            var full = Parameters.WorkListStatusId is not null || Parameters.WorkSortTypeId is WorkSortType.Size or WorkSortType.Files;
            var sql = new SqlStatement { DisableJit = true };
            sql.Statement = "WITH " + (full ? ListedWorks : LeanListedWorks) +
                """
                ), ordered AS (
                    SELECT
                        listed."Id",
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
                        (cardinality(@MediaTypeIds::smallint[]) + cardinality(@MediaSubtypeIds::smallint[]) = 0
                            OR listed."MediaSubtypeId" IS NULL AND listed."MediaTypeId" = ANY(@MediaTypeIds::smallint[])
                            OR listed."MediaSubtypeId" = ANY(@MediaSubtypeIds::smallint[]))
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
                        AND (@IsMonitored::boolean IS NULL OR listed."IsMonitored" = @IsMonitored)
                        AND (@WorkListStatusId::smallint IS NULL OR listed."WorkListStatusId" = @WorkListStatusId)
                )
                SELECT
                    listed."Id",
                    listed."MediaTypeId",
                    listed."MediaSubtypeId",
                    listed."CanonicalTitle",
                    listed."ReleaseYear",
                    listed."CreatedAt",
                    facts."PosterUrl",
                    facts."Overview",
                    listed."WantedItemId",
                    listed."TargetCount",
                    listed."IsMonitored",
                    listed."ProfileName",
                    listed."LibraryName",
                    listed."FileCount",
                    listed."SizeBytes",
                    listed."WorkListStatusId"
                FROM
                    ordered
                    JOIN PAGE_SOURCE AS listed ON listed."Id" = ordered."Id"
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
                """.Replace("PAGE_SOURCE", full ? "listed" : "\"WorkListEntries\"", StringComparison.Ordinal);
            sql.Parameters.Add(new AdminWorkListFilterData(Parameters.MediaTypeIds, Parameters.MediaSubtypeIds, Parameters.IsMonitored, Parameters.WorkListStatusId, Parameters.LibraryRootId, Parameters.AcquisitionProfileId, Parameters.Query,
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
            // Each title's state is worked out once (the materialized set), not again for every count that reads it.
            var sql = new SqlStatement { DisableJit = true };
            sql.Statement = "WITH " + ReadWorksV1.ListedWorks +
                """
                ), filtered AS (
                    SELECT
                        listed."WorkListStatusId",
                        listed."IsMonitored",
                        listed."FileCount",
                        listed."SizeBytes"
                    FROM
                        listed
                    WHERE
                        (cardinality(@MediaTypeIds::smallint[]) + cardinality(@MediaSubtypeIds::smallint[]) = 0
                            OR listed."MediaSubtypeId" IS NULL AND listed."MediaTypeId" = ANY(@MediaTypeIds::smallint[])
                            OR listed."MediaSubtypeId" = ANY(@MediaSubtypeIds::smallint[]))
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
                    count(*) FILTER (WHERE filtered."WorkListStatusId" = 1)::integer AS "AvailableCount",
                    count(*) FILTER (WHERE filtered."WorkListStatusId" = 2)::integer AS "MissingCount",
                    count(*) FILTER (WHERE filtered."WorkListStatusId" = 3)::integer AS "DownloadingCount",
                    count(*) FILTER (WHERE filtered."WorkListStatusId" = 4)::integer AS "UnmonitoredCount",
                    count(*) FILTER (WHERE filtered."IsMonitored")::integer AS "MonitoredCount",
                    coalesce(sum(filtered."FileCount"), 0)::integer AS "FileCount",
                    coalesce(sum(filtered."SizeBytes"), 0)::bigint AS "SizeBytes"
                FROM
                    filtered;
                """;
            sql.Parameters.Add(new AdminWorkListFilterData(Parameters.MediaTypeIds, Parameters.MediaSubtypeIds, null, null, Parameters.LibraryRootId, Parameters.AcquisitionProfileId, Parameters.Query, WorkSortType.Title, false));
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
