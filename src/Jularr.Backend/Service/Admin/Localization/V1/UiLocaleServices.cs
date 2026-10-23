using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Localization.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Localization;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Localization;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Localization.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/localization/locales")]
    public sealed class ReadUiLocalesV1 : AdminService<ReadUiLocalesParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<UiLocaleDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadUiLocalesParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadUiLocalesParametersV1 parameters, Type resultType) => new("localization.read");
        protected override PageRequest GetPageRequest(ReadUiLocalesParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Include disabled languages.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    locale."Id",
                    locale."Locale",
                    locale."Name",
                    locale."IsEnabled",
                    locale."IsRightToLeft",
                    locale."CatalogVersion"
                FROM
                    "UiLocales" AS locale
                ORDER BY
                    locale."Locale",
                    locale."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<UiLocaleDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/localization/locales")]
    public sealed class CreateUiLocaleV1 : AdminService<CreateUiLocaleParametersV1, CreateUiLocaleDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateUiLocaleResultV1>()];
        protected override ModuleRequirement GetInstanceModules(CreateUiLocaleParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(CreateUiLocaleParametersV1 parameters, Type resultType) => new("localization.update");

        protected override async Task<IServiceOutput> OnCreate()
        {
            var locale = await Context.CreateUiLocaleAsync(new UiLocaleCreationData(Data.Locale, Data.Name, Data.IsRightToLeft), CancellationToken);
            return new CreateUiLocaleResultV1(locale.Id);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/localization/locales/{uiLocaleId:long}")]
    public sealed class UpdateUiLocaleV1 : AdminService<UpdateUiLocaleParametersV1, UpdateUiLocaleDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(UpdateUiLocaleParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(UpdateUiLocaleParametersV1 parameters, Type resultType) => new("localization.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.UpdateUiLocaleAsync(new UiLocaleUpdateData(Parameters.UiLocaleId, Data.Name, Data.IsEnabled, Data.IsRightToLeft, Data.CatalogVersion), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
