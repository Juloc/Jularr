using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.Public.Setup.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Account;
using Jularr.Backend.Logic.Session;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.Public.Localization.V1;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Public.Setup.V1
{
    [ServiceEndpoint("GET", "/api/v1/setup/locales")]
    public sealed class ReadSetupLocalesV1 : PublicService<SetupParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<SetupLocaleV1>>()];
        protected override ModuleRequirement GetInstanceModules(SetupParametersV1 parameters) => ModuleRequirement.None;

        protected override async Task<IServiceOutput> OnRead()
        {
            // First-run language choices include the shipped, not yet enabled languages.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    locale."Id",
                    locale."Locale",
                    locale."Name",
                    EXISTS (
                        SELECT 1
                        FROM "UiTranslations" AS translation
                        WHERE translation."UiLocaleId" = locale."Id"
                            AND translation."UiTranslationStatusTypeId" = 2
                    ) AS "IsMachineTranslated"
                FROM
                    "UiLocales" AS locale
                WHERE
                    EXISTS (SELECT 1 FROM "InstanceSetupState" AS setup WHERE setup."Id" = 1 AND setup."OwnerCreatedAt" IS NULL)
                ORDER BY
                    locale."Locale";
                """;
            return new BaseDataList<SetupLocaleV1>(await Context.ReadSql.ReadListAsync<SetupLocaleV1>(sql, CancellationToken));
        }
    }

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
        private readonly UiLocalizationCache _localization;

        internal CreateOwnerV1(IOptions<AuthenticationOptions> options, UiLocalizationCache localization)
        {
            _options = options.Value;
            _localization = localization;
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

        protected override void OnCommitted() => _localization.Clear();
    }
}
