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
            sql.Statement =
                """
                WITH wanted_page AS (
                    SELECT
                        wanted."Id",
                        wanted."WorkId",
                        work."CanonicalTitle",
                        wanted."IsPaused",
                        wanted."Version"
                    FROM
                        "WantedItems" AS wanted
                    INNER JOIN
                        "Works" AS work ON work."Id" = wanted."WorkId"
                    ORDER BY
                        wanted."Id" DESC
                    LIMIT @PageSize
                    OFFSET @Offset
                )
                SELECT
                    wanted_page."Id",
                    wanted_page."WorkId",
                    wanted_page."CanonicalTitle",
                    wanted_page."IsPaused",
                    wanted_page."Version",
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
                ORDER BY
                    wanted_page."Id" DESC;
                """;
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<WantedDetailsV1>(sql, CancellationToken);
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
}
