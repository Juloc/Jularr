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
            // Enrich only the bounded page.
            var sql = new SqlStatement();
            sql.Statement = WantedQuery.Build(
                """
                    ORDER BY
                        wanted."Id" DESC
                    LIMIT @PageSize
                    OFFSET @Offset
                """);
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
            sql.Statement = WantedQuery.Build(
                """
                    WHERE
                        wanted."Id" = @WantedItemId
                """);
            sql.Parameters.Add(Parameters);
            return await Context.ReadSql.ReadRequiredAsync<WantedDetailsV1>(sql, CancellationToken);
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
            await Context.UpdateWantedItemAsync(new WantedItemUpdateData(Parameters.WantedItemId, Data.IsPaused, Data.Version), CancellationToken);
            return new UpdateResult(true);
        }
    }

    internal static class WantedQuery
    {
        // One projection for the worklist and a single target.
        public static string Build(string selection) =>
            $"""
            WITH wanted_page AS (
                SELECT
                    wanted."Id",
                    wanted."WorkId",
                    work."MediaTypeId",
                    work."CanonicalTitle",
                    wanted."IsPaused",
                    wanted."LastSearchedAt",
                    wanted."LastOutcomeKey",
                    wanted."Version"
                FROM
                    "WantedItems" AS wanted
                INNER JOIN
                    "Works" AS work ON work."Id" = wanted."WorkId"
            {selection}
            )
            SELECT
                wanted_page."Id",
                wanted_page."WorkId",
                wanted_page."MediaTypeId",
                wanted_page."CanonicalTitle",
                wanted_page."IsPaused",
                wanted_page."Version",
                EXISTS (
                    SELECT
                        1
                    FROM
                        "MediaAssets" AS asset
                    WHERE
                        asset."WorkId" = wanted_page."WorkId"
                ) AND NOT {DownloadLogic.MissingEpisode("wanted_page.\"WorkId\"")} AS "HasMedia",
                download."Id" IS NOT NULL AS "IsDownloading",
                download."ProgressPercent"::integer AS "ProgressPercent",
                wanted_page."LastSearchedAt",
                wanted_page."LastOutcomeKey" AS "OutcomeKey",
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
    }
}
