using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.User.Account.V1;
using Jularr.Backend.Logic.Account;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Account.V1
{
    [ServiceEndpoint("PUT", "/api/v1/account/password")]
    public sealed class ChangeAccountPasswordV1 : UserService<ChangeAccountPasswordParametersV1, ChangeAccountPasswordDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ChangeAccountPasswordParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ChangeAccountPasswordParametersV1 parameters, Type resultType) => new("profile.self.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // Only the signed-in account's own password.
            await Context.ChangePasswordAsync(Auth.AccountId ?? throw new UnauthorizedAccessException("No signed-in account."), Data.CurrentPassword, Data.NewPassword, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
