using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/custom-formats")]
    public sealed class ReadCustomFormatsV1 : AdminService<ReadCustomFormatsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<CustomFormatDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadCustomFormatsParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ReadCustomFormatsParametersV1 parameters, Type resultType) => new("acquisition.manage");
        protected override PageRequest GetPageRequest(ReadCustomFormatsParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    format."Id",
                    format."MediaTypeId",
                    format."Name",
                    format."IncludeWhenRenaming",
                    format."Conditions"::text AS "Conditions",
                    format."Version"
                FROM
                    "CustomFormats" AS format
                WHERE
                    format."MediaTypeId" = @MediaTypeId
                ORDER BY
                    format."Name",
                    format."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Page = Page;
            sql.Parameters.Add(new AcquisitionMediaScopeData(Parameters.MediaTypeId));
            var rows = await Context.ReadSql.ReadListAsync<CustomFormatRowData>(sql, CancellationToken);
            return new PageResult<CustomFormatDetailsV1>(rows.Select(CustomFormatContracts.Read).ToArray(), Page!);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/custom-formats/{customFormatId:long}")]
    public sealed class ReadCustomFormatV1 : AdminService<CustomFormatParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CustomFormatDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(CustomFormatParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(CustomFormatParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    format."Id",
                    format."MediaTypeId",
                    format."Name",
                    format."IncludeWhenRenaming",
                    format."Conditions"::text AS "Conditions",
                    format."Version"
                FROM
                    "CustomFormats" AS format
                WHERE
                    format."Id" = @CustomFormatId;
                """;
            sql.Parameters.Add(Parameters);
            return CustomFormatContracts.Read(await Context.ReadSql.ReadRequiredAsync<CustomFormatRowData>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/acquisition/custom-formats")]
    public sealed class SaveCustomFormatV1 : AdminService<SaveCustomFormatParametersV1, SaveCustomFormatDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SavedCustomFormatV1>()];
        protected override ModuleRequirement GetInstanceModules(SaveCustomFormatParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(SaveCustomFormatParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var conditions = Data.Conditions.Select(item => new FormatCondition(item.Name, item.Kind, item.Negate, item.Required)
            {
                Value = item.Value, ExceptLanguage = item.ExceptLanguage, Resolution = item.Resolution, Minimum = item.Minimum, Maximum = item.Maximum
            }).ToArray();
            var key = await Context.SaveCustomFormatAsync(new CustomFormatWriteData(Data.Id, Data.MediaTypeId, Data.Name, Data.IncludeWhenRenaming,
                JsonSerializer.Serialize(conditions, AcquisitionSelectionLogic.DefinitionOptions), Data.Version), CancellationToken);
            return new SavedCustomFormatV1(key.Id, key.Version);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/acquisition/custom-formats/{customFormatId:long}")]
    public sealed class DeleteCustomFormatV1 : AdminService<DeleteCustomFormatParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(DeleteCustomFormatParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(DeleteCustomFormatParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.DeleteCustomFormatAsync(new CustomFormatKeyData(Parameters.CustomFormatId, Parameters.Version), CancellationToken);
            return new UpdateResult(true);
        }
    }

    internal static class CustomFormatContracts
    {
        public static CustomFormatDetailsV1 Read(CustomFormatRowData row)
        {
            var conditions = JsonSerializer.Deserialize<FormatCondition[]>(row.Conditions, AcquisitionSelectionLogic.DefinitionOptions)
                ?? throw new InvalidOperationException("The custom format is invalid.");
            return new CustomFormatDetailsV1(row.Id, row.MediaTypeId, row.Name, row.IncludeWhenRenaming,
                new BaseDataList<FormatConditionV1>(conditions.Select(item => new FormatConditionV1(item.Name, item.Kind, item.Negate, item.Required, item.Value,
                    item.ExceptLanguage, item.Resolution, item.Minimum, item.Maximum))), row.Version);
        }
    }
}
