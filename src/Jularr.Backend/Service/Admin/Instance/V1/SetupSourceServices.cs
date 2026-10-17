using System.Text;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Instance;
using Jularr.Backend.Logic.Provider;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Backend.Service.Core;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Instance.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/setup/sources")]
    public sealed class ReadSetupSourcesV1 : AdminService<SetupProgressParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SetupSourcesDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(SetupProgressParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(SetupProgressParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    EXISTS (
                        SELECT 1
                        FROM "Providers" AS provider
                        WHERE provider."Key" = 'tmdb' AND provider."ProtectedSecret" IS NOT NULL
                    ) AS "HasTmdbKey";
                """;
            return await Context.ReadSql.ReadRequiredAsync<SetupSourcesDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/setup/sources")]
    public sealed class SaveSetupSourcesV1 : AdminService<SetupProgressParametersV1, SaveSetupSourcesDataV1>
    {
        private readonly IDataProtector _protector;

        internal SaveSetupSourcesV1(IDataProtectionProvider protection)
        {
            _protector = protection.CreateProtector(SaveProviderSecretV1.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(SetupProgressParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(SetupProgressParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.RequireSetupVersionAsync(new SetupVersionData(Data.Version), CancellationToken);
            if (Data.TmdbKey is { } key)
            {
                var provider = await Context.ReadProviderCredentialAsync(new ProviderParameters("tmdb"), CancellationToken)
                    ?? throw new KeyNotFoundException("The provider was not found.");
                await Context.SaveProviderSecretAsync(new ProviderSecretData(provider.Id, _protector.Protect(Encoding.UTF8.GetBytes(key)), null), CancellationToken);
                await Context.EnableProviderAsync("tmdb", CancellationToken);
            }

            // Saving a credential stays in this step; finishing still commits the key and final checks together.
            if (Data.CompleteSetup)
            {
                await Context.CompleteSetupAsync(new SetupVersionData(Data.Version), CancellationToken);
            }
            return new UpdateResult(Data.CompleteSetup || Data.TmdbKey is not null);
        }
    }
}
