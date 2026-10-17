using System.Text.Json;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/quality-definitions")]
    public sealed class ReadQualityDefinitionsV1 : AdminService<QualityDefinitionsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<QualityDefinitionV1>>()];
        protected override ModuleRequirement GetInstanceModules(QualityDefinitionsParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(QualityDefinitionsParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    quality."Quality",
                    quality."MinimumMbPerMinute",
                    quality."PreferredMbPerMinute",
                    quality."MaximumMbPerMinute",
                    quality."Version"
                FROM
                    "QualityDefinitions" AS quality
                WHERE
                    quality."MediaTypeId" = @MediaTypeId
                ORDER BY
                    quality."Quality";
                """;
            sql.Parameters.Add(Parameters);
            return new BaseDataList<QualityDefinitionV1>(await Context.ReadSql.ReadListAsync<QualityDefinitionV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/acquisition/quality-definitions")]
    public sealed class SaveQualityDefinitionsV1 : AdminService<QualityDefinitionsParametersV1, SaveQualityDefinitionsDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(QualityDefinitionsParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(QualityDefinitionsParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SaveQualityDefinitionsAsync(new QualityDefinitionsWriteData(Parameters.MediaTypeId, JsonSerializer.Serialize(Data.Rows)), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
