using Jularr.Backend.Data.Admin.Account.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Logic.Account;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Account.V1
{
    // An administrator (the Owner for their own account, too) creates a link, valid for a day, with which a person sets a new password.
    [ServiceEndpoint("POST", "/api/v1/admin/accounts/{accountId:long}/password-link")]
    public sealed class CreateAccountPasswordLinkV1 : AdminService<AccountParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AccountPasswordLinkResultV1>()];
        protected override ModuleRequirement GetInstanceModules(AccountParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(AccountParametersV1 parameters, Type resultType) => new("accounts.update");

        protected override async Task<IServiceOutput> OnCreate()
        {
            var (token, expiresAt) = await Context.CreatePasswordResetAsync(Parameters.AccountId, CancellationToken);
            return new AccountPasswordLinkResultV1(token, expiresAt);
        }
    }
}
