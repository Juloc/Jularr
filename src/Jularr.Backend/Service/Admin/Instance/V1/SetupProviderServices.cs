using System.Text;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Logic.Instance;
using Jularr.Backend.Logic.Provider;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Backend.Service.Core;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Instance.V1
{
    [ServiceEndpoint("PUT", "/api/v1/admin/setup/providers")]
    public sealed class SaveSetupProvidersV1 : AdminService<SetupProgressParametersV1, SaveSetupProvidersDataV1>
    {
        private readonly IDataProtector _protector;

        internal SaveSetupProvidersV1(IDataProtectionProvider protection)
        {
            _protector = protection.CreateProtector(SaveProviderSecretV1.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(SetupProgressParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(SetupProgressParametersV1 parameters, Type resultType) => new("providers.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.RequireSetupVersionAsync(new SetupVersionData(Data.Version), CancellationToken);
            foreach (var input in Data.Providers)
            {
                var provider = await Context.ReadProviderCredentialAsync(new ProviderParameters(input.Key), CancellationToken) ?? throw new KeyNotFoundException("The provider was not found.");

                // A typed key is kept protected and switches the provider on; the stored one stays where nothing was typed.
                if (input.Secret is { } secret)
                {
                    await Context.SaveProviderSecretAsync(new ProviderSecretData(provider.Id, _protector.Protect(Encoding.UTF8.GetBytes(secret)), input.ClientId), CancellationToken);
                }
                else if (input.ClientId is not null)
                {
                    await Context.SaveProviderSecretAsync(new ProviderSecretData(provider.Id, provider.ProtectedSecret, input.ClientId), CancellationToken);
                }

                var enabled = input.IsEnabled || input.Secret is not null;
                if (enabled != provider.IsEnabled) await Context.UpdateProviderAsync(new ProviderUpdateData(provider.Id, enabled, provider.IsEnabled), CancellationToken);
            }

            await Context.UpdateSetupStepAsync(new SetupStepData(SetupStepType.Overview), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
