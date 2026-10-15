using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Wanted.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/wanted")]
    public sealed class ReadWantedV1 : AdminService<ReadWantedParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<WantedDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadWantedParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(ReadWantedParametersV1 parameters, Type resultType) => new("wanted.read");
        protected override PageRequest GetPageRequest(ReadWantedParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Enrich only the bounded page. Missing: nothing or a monitored aired unit without media; cutoff unmet as UpgradePolicy.Assess
            // decides it, per unit for the best known quality of the target's profile.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH wanted_state AS (
                    SELECT
                        wanted."Id",
                        wanted."WorkId",
                        work."MediaTypeId",
                        work."MediaSubtypeId",
                        work."CanonicalTitle",
                        wanted."IsPaused",
                        wanted."MonitorsNewUnits",
                        wanted."LastSearchedAt",
                        wanted."LastOutcomeKey",
                        wanted."AcquisitionProfileId",
                        wanted."LibraryRootId",
                        wanted."GamePlatformId",
                        wanted."Version",
                        EXISTS (
                            SELECT
                                1
                            FROM
                                "MediaAssets" AS asset
                                JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                            WHERE
                                asset."WorkId" = wanted."WorkId"
                                AND (
                                    wanted."GamePlatformId" IS NULL
                                    OR EXISTS (
                                        SELECT
                                            1
                                        FROM
                                            "GameReleases" AS game
                                        WHERE
                                            game."WorkVersionId" = asset."WorkVersionId"
                                            AND game."GamePlatformId" = wanted."GamePlatformId"
                                    )
                                )
                        ) AND NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "WorkUnits" AS episode
                            WHERE
                                episode."WorkId" = wanted."WorkId"
                                AND episode."WorkUnitTypeId" = 2
                                AND episode."IsMonitored"
                                AND episode."ReleasedOn" <= current_date
                                AND NOT EXISTS (
                                    SELECT
                                        1
                                    FROM
                                        "MediaAssets" AS asset
                                        JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                                    WHERE
                                        asset."WorkUnitId" = episode."Id"
                                )
                        ) AS "HasMedia",
                        COALESCE((
                            SELECT
                                (profile."Definition" ->> 'UpgradeAllowed')::boolean
                                AND EXISTS (
                                    SELECT
                                        1
                                    FROM
                                        "MediaAssets" AS asset
                                        JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                                        JOIN jsonb_array_elements_text(profile."Definition" -> 'QualityOrder') WITH ORDINALITY AS quality ("Key", "Position")
                                            ON lower(quality."Key") = lower(available."QualityKey")
                                        LEFT JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
                                    WHERE
                                        asset."WorkId" = wanted."WorkId"
                                        AND (unit."Id" IS NULL OR unit."IsMonitored")
                                        AND (
                                            wanted."GamePlatformId" IS NULL
                                            OR EXISTS (
                                                SELECT
                                                    1
                                                FROM
                                                    "GameReleases" AS game
                                                WHERE
                                                    game."WorkVersionId" = asset."WorkVersionId"
                                                    AND game."GamePlatformId" = wanted."GamePlatformId"
                                            )
                                        )
                                    GROUP BY
                                        asset."WorkUnitId"
                                    HAVING
                                        min(quality."Position") > COALESCE((
                                            SELECT
                                                cutoff."Position"
                                            FROM
                                                jsonb_array_elements_text(profile."Definition" -> 'QualityOrder') WITH ORDINALITY AS cutoff ("Key", "Position")
                                            WHERE
                                                lower(cutoff."Key") = lower(profile."Definition" ->> 'UpgradeCutoffQuality')
                                            LIMIT 1
                                        ), 1)
                                )
                            FROM
                                "AcquisitionProfiles" AS profile
                            WHERE
                                profile."MediaTypeId" = work."MediaTypeId"
                                AND (profile."Id" = wanted."AcquisitionProfileId"
                                    OR wanted."AcquisitionProfileId" IS NULL AND profile."IsDefault" AND (profile."MediaSubtypeId" IS NULL OR profile."MediaSubtypeId" = work."MediaSubtypeId"))
                            ORDER BY
                                profile."MediaSubtypeId" IS NULL
                            LIMIT 1
                        ), FALSE) AS "IsCutoffUnmet"
                    FROM
                        "WantedItems" AS wanted
                    INNER JOIN
                        "Works" AS work ON work."Id" = wanted."WorkId"
                ), wanted_page AS (
                    SELECT
                        *
                    FROM
                        wanted_state
                    WHERE
                        @WantedViewTypeId = 0
                        OR @WantedViewTypeId = 1 AND NOT wanted_state."IsPaused" AND NOT wanted_state."HasMedia"
                        OR @WantedViewTypeId = 2 AND NOT wanted_state."IsPaused" AND wanted_state."IsCutoffUnmet"
                    ORDER BY
                        wanted_state."Id" DESC
                    LIMIT @PageSize
                    OFFSET @Offset
                )
                SELECT
                    wanted_page."Id",
                    wanted_page."WorkId",
                    wanted_page."MediaTypeId",
                    wanted_page."MediaSubtypeId",
                    wanted_page."CanonicalTitle",
                    wanted_page."IsPaused",
                    wanted_page."MonitorsNewUnits",
                    wanted_page."Version",
                    wanted_page."HasMedia",
                    download."Id" IS NOT NULL AS "IsDownloading",
                    download."ProgressPercent"::integer AS "ProgressPercent",
                    wanted_page."LastSearchedAt",
                    wanted_page."LastOutcomeKey" AS "OutcomeKey",
                    wanted_page."AcquisitionProfileId",
                    wanted_page."LibraryRootId",
                    (SELECT platform."Name" FROM "GamePlatforms" AS platform WHERE platform."Id" = wanted_page."GamePlatformId") AS "PlatformName",
                    ARRAY(
                        SELECT DISTINCT
                            locale."Name"
                        FROM
                            "RequestTargets" AS target
                        INNER JOIN
                            "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId"
                        INNER JOIN
                            "UiLocales" AS locale ON locale."Id" = request."UiLocaleId"
                        WHERE
                            target."WantedItemId" = wanted_page."Id"
                        ORDER BY
                            locale."Name"
                    ) AS "RequestedLanguages"
                FROM
                    wanted_page
                    LEFT JOIN LATERAL (
                        SELECT
                            operation."Id",
                            operation."ProgressPercent"
                        FROM
                            "AcquisitionReleases" AS release
                            JOIN "Operations" AS operation ON operation."AcquisitionReleaseId" = release."Id"
                        WHERE
                            release."WantedItemId" = wanted_page."Id"
                            AND operation."OperationStatusTypeId" IN (1, 2)
                        LIMIT 1
                    ) AS download ON TRUE
                ORDER BY
                    wanted_page."Id" DESC;
                """;
            sql.Parameters.Add(new WantedViewFilterData(Parameters.WantedViewTypeId));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<WantedDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/wanted/{wantedItemId:long}")]
    public sealed class ReadWantedItemV1 : AdminService<WantedItemParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WantedDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(WantedItemParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(WantedItemParametersV1 parameters, Type resultType) => new("wanted.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH wanted_page AS (
                    SELECT
                        wanted."Id",
                        wanted."WorkId",
                        work."MediaTypeId",
                        work."MediaSubtypeId",
                        work."CanonicalTitle",
                        wanted."IsPaused",
                        wanted."MonitorsNewUnits",
                        wanted."LastSearchedAt",
                        wanted."LastOutcomeKey",
                        wanted."AcquisitionProfileId",
                        wanted."LibraryRootId",
                        wanted."GamePlatformId",
                        wanted."Version"
                    FROM
                        "WantedItems" AS wanted
                    INNER JOIN
                        "Works" AS work ON work."Id" = wanted."WorkId"
                    WHERE
                        wanted."Id" = @WantedItemId
                )
                SELECT
                    wanted_page."Id",
                    wanted_page."WorkId",
                    wanted_page."MediaTypeId",
                    wanted_page."MediaSubtypeId",
                    wanted_page."CanonicalTitle",
                    wanted_page."IsPaused",
                    wanted_page."MonitorsNewUnits",
                    wanted_page."Version",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "MediaAssets" AS asset
                            JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                        WHERE
                            asset."WorkId" = wanted_page."WorkId"
                            AND (
                                wanted_page."GamePlatformId" IS NULL
                                OR EXISTS (
                                    SELECT
                                        1
                                    FROM
                                        "GameReleases" AS game
                                    WHERE
                                        game."WorkVersionId" = asset."WorkVersionId"
                                        AND game."GamePlatformId" = wanted_page."GamePlatformId"
                                )
                            )
                    ) AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "WorkUnits" AS episode
                        WHERE
                            episode."WorkId" = wanted_page."WorkId"
                            AND episode."WorkUnitTypeId" = 2
                            AND episode."IsMonitored"
                            AND episode."ReleasedOn" <= current_date
                            AND NOT EXISTS (
                                SELECT
                                    1
                                FROM
                                    "MediaAssets" AS asset
                                    JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                                WHERE
                                    asset."WorkUnitId" = episode."Id"
                            )
                    ) AS "HasMedia",
                    download."Id" IS NOT NULL AS "IsDownloading",
                    download."ProgressPercent"::integer AS "ProgressPercent",
                    wanted_page."LastSearchedAt",
                    wanted_page."LastOutcomeKey" AS "OutcomeKey",
                    wanted_page."AcquisitionProfileId",
                    wanted_page."LibraryRootId",
                    (SELECT platform."Name" FROM "GamePlatforms" AS platform WHERE platform."Id" = wanted_page."GamePlatformId") AS "PlatformName",
                    ARRAY(
                        SELECT DISTINCT
                            locale."Name"
                        FROM
                            "RequestTargets" AS target
                        INNER JOIN
                            "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId"
                        INNER JOIN
                            "UiLocales" AS locale ON locale."Id" = request."UiLocaleId"
                        WHERE
                            target."WantedItemId" = wanted_page."Id"
                        ORDER BY
                            locale."Name"
                    ) AS "RequestedLanguages"
                FROM
                    wanted_page
                    LEFT JOIN LATERAL (
                        SELECT
                            operation."Id",
                            operation."ProgressPercent"
                        FROM
                            "AcquisitionReleases" AS release
                            JOIN "Operations" AS operation ON operation."AcquisitionReleaseId" = release."Id"
                        WHERE
                            release."WantedItemId" = wanted_page."Id"
                            AND operation."OperationStatusTypeId" IN (1, 2)
                        LIMIT 1
                    ) AS download ON TRUE
                ORDER BY
                    wanted_page."Id" DESC;
                """;
            sql.Parameters.Add(Parameters);
            return await Context.ReadSql.ReadRequiredAsync<WantedDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/wanted/{wantedItemId:long}/libraries")]
    public sealed class ReadWantedLibrariesV1 : AdminService<WantedItemParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WantedLibraryV1>>()];
        protected override ModuleRequirement GetInstanceModules(WantedItemParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(WantedItemParametersV1 parameters, Type resultType) => new("wanted.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Libraries of the Work's type that accept its subtype.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    root."Id",
                    root."Name"
                FROM
                    "WantedItems" AS wanted
                    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                    JOIN "LibraryRoots" AS root ON root."MediaTypeId" = work."MediaTypeId"
                WHERE
                    wanted."Id" = @WantedItemId
                    AND (root."MediaSubtypeId" IS NULL OR root."MediaSubtypeId" = work."MediaSubtypeId")
                ORDER BY
                    root."Name",
                    root."Id";
                """;
            sql.Parameters.Add(Parameters);
            return new BaseDataList<WantedLibraryV1>(await Context.ReadSql.ReadListAsync<WantedLibraryV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/wanted/{wantedItemId:long}")]
    public sealed class UpdateWantedV1 : AdminService<UpdateWantedParametersV1, UpdateWantedDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(UpdateWantedParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(UpdateWantedParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.UpdateWantedItemAsync(new WantedItemUpdateData(Parameters.WantedItemId, Data.IsPaused, Data.AcquisitionProfileId, Data.LibraryRootId, Data.MonitorsNewUnits, Data.Version), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/wanted/{wantedItemId:long}/units")]
    public sealed class ReadWantedUnitsV1 : AdminService<ReadWantedUnitsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WantedUnitV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadWantedUnitsParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(ReadWantedUnitsParametersV1 parameters, Type resultType) => new("wanted.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Seasons and volumes in their order, one season's episodes when asked; tracks follow their album.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    unit."Id",
                    unit."ParentWorkUnitId",
                    unit."WorkUnitTypeId",
                    unit."Number",
                    unit."Title",
                    unit."ReleasedOn",
                    unit."IsMonitored",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "MediaAssets" AS asset
                            JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                        WHERE
                            asset."WorkUnitId" = unit."Id"
                    ) AS "HasMedia",
                    episodes."Episodes",
                    episodes."MonitoredEpisodes"
                FROM
                    "WantedItems" AS wanted
                    JOIN "WorkUnits" AS unit ON unit."WorkId" = wanted."WorkId"
                    LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                    CROSS JOIN LATERAL (
                        SELECT
                            count(*)::integer AS "Episodes",
                            (count(*) FILTER (WHERE episode."IsMonitored"))::integer AS "MonitoredEpisodes"
                        FROM
                            "WorkUnits" AS episode
                        WHERE
                            episode."ParentWorkUnitId" = unit."Id"
                            AND episode."WorkUnitTypeId" = 2
                    ) AS episodes
                WHERE
                    wanted."Id" = @WantedItemId
                    AND unit."WorkUnitTypeId" IN (1, 2, 3)
                    AND (unit."ParentWorkUnitId" IS NULL OR unit."ParentWorkUnitId" = @SeasonId)
                ORDER BY
                    coalesce(parent."Number", unit."Number"),
                    unit."ParentWorkUnitId" NULLS FIRST,
                    unit."Number";
                """;
            sql.Parameters.Add(Parameters);
            return new BaseDataList<WantedUnitV1>(await Context.ReadSql.ReadListAsync<WantedUnitV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/wanted/{wantedItemId:long}/units/{workUnitId:long}")]
    public sealed class UpdateWantedUnitV1 : AdminService<WantedUnitParametersV1, UpdateWantedUnitDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WantedUnitParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(WantedUnitParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SetUnitMonitoringAsync(new WantedUnitMonitoringData(Parameters.WantedItemId, Parameters.WorkUnitId, Data.IsMonitored), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
