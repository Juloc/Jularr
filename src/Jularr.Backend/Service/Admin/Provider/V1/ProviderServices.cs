using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Provider;
using Jularr.Backend.Service.Core;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Provider.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/providers")]
    public sealed class ReadProvidersV1 : AdminService<ReadProvidersParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<ProviderDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadProvidersParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadProvidersParametersV1 parameters, Type resultType) => new("providers.read");
        protected override PageRequest GetPageRequest(ReadProvidersParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Read registered providers only.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    provider."Id",
                    provider."Key",
                    provider."IsEnabled",
                    provider."ProtectedSecret" IS NOT NULL AS "HasSecret"
                FROM
                    "Providers" AS provider
                ORDER BY
                    provider."Key",
                    provider."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<ProviderDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/providers/{providerId:long}")]
    public sealed class UpdateProviderV1 : AdminService<UpdateProviderParametersV1, UpdateProviderDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(UpdateProviderParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(UpdateProviderParametersV1 parameters, Type resultType) => new("providers.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.UpdateProviderAsync(new ProviderUpdateData(Parameters.ProviderId, Data.IsEnabled, Data.ExpectedIsEnabled), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/providers/{providerId:long}/secret")]
    public sealed class SaveProviderSecretV1 : AdminService<UpdateProviderParametersV1, SaveProviderSecretDataV1>
    {
        public const string ProtectorPurpose = "Jularr.Provider.Secret.V1";
        private readonly IDataProtector _protector;

        internal SaveProviderSecretV1(IDataProtectionProvider protection)
        {
            _protector = protection.CreateProtector(ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(UpdateProviderParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(UpdateProviderParametersV1 parameters, Type resultType) => new("providers.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // Protect before persistence.
            var secret = Data.Secret is { Length: > 0 } value ? _protector.Protect(Encoding.UTF8.GetBytes(value)) : null;
            await Context.SaveProviderSecretAsync(new ProviderSecretData(Parameters.ProviderId, secret, Data.ClientId is { Length: > 0 } clientId ? clientId.Trim() : null), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
