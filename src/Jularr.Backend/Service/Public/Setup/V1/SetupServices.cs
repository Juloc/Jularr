using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.Public.Setup.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Account;
using Jularr.Backend.Logic.Session;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Public.Setup.V1
{
    [ServiceEndpoint("GET", "/api/v1/setup")]
    public sealed class ReadSetupV1 : PublicService<SetupParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReadSetupResultV1>()];
        protected override ModuleRequirement GetInstanceModules(SetupParametersV1 parameters) => ModuleRequirement.None;

        protected override async Task<IServiceOutput> OnRead()
        {
            // Read bootstrap availability only.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    setup."OwnerCreatedAt" IS NULL
                        AND NOT EXISTS (SELECT 1 FROM "Accounts")
                FROM
                    "InstanceSetupState" AS setup
                WHERE
                    setup."Id" = 1;
                """;
            var requiresOwner = (bool)(await Context.ReadSql.ExecuteScalarAsync(sql, CancellationToken))!;
            return new ReadSetupResultV1(requiresOwner);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/setup/owner")]
    public sealed class CreateOwnerV1 : PublicService<SetupParametersV1, CreateOwnerDataV1>
    {
        private readonly AuthenticationOptions _options;

        internal CreateOwnerV1(IOptions<AuthenticationOptions> options)
        {
            _options = options.Value;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LoginResultV1>()];
        protected override ModuleRequirement GetInstanceModules(SetupParametersV1 parameters) => ModuleRequirement.None;

        protected override async Task<IServiceOutput> OnCreate()
        {
            // Create and sign in atomically.
            var account = await Context.CreateFirstOwnerAsync(Data.Email, Data.DisplayName, Data.Password, Data.UiLocaleId, CancellationToken);
            var session = await Context.CreateSessionAsync(account, TimeSpan.FromHours(_options.SessionLifetimeHours), CancellationToken);
            return new LoginResultV1(session);
        }
    }
}
