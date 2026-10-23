using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Logic.Account;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Public.Authentication.V1
{
    // The person who holds an administrator's link sets a new password; the link works once.
    [ServiceEndpoint("POST", "/api/v1/auth/password-reset")]
    public sealed class ResetPasswordV1 : PublicService<AuthenticationParametersV1, ResetPasswordDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ResetPasswordResultV1>()];
        protected override ModuleRequirement GetInstanceModules(AuthenticationParametersV1 parameters) => ModuleRequirement.None;

        protected override async Task<IServiceOutput> OnCreate()
        {
            await Context.ResetPasswordAsync(Data.Token, Data.NewPassword, CancellationToken);
            return new ResetPasswordResultV1();
        }
    }
}
