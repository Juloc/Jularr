using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Account.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Session;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Account.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/accounts/{accountId:long}/sessions")]
    public sealed class ReadAccountSessionsV1 : AdminService<ReadAccountSessionsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<AccountSessionDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadAccountSessionsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadAccountSessionsParametersV1 parameters, Type resultType) => new("accounts.read");
        protected override PageRequest GetPageRequest(ReadAccountSessionsParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Read valid sessions, never secrets.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    session."PublicId" AS "Id",
                    session."CreatedAt",
                    session."ExpiresAt",
                    (session."PublicId" = @SessionPublicId) AS "IsCurrent",
                    session."DeviceName",
                    session."DevicePlatform"
                FROM
                    "AccountSessions" AS session
                INNER JOIN
                    "Accounts" AS account ON account."Id" = session."AccountId"
                WHERE
                    session."AccountId" = @AccountId
                    AND account."IsEnabled" = TRUE
                    AND session."RevokedAt" IS NULL
                    AND session."ExpiresAt" > statement_timestamp()
                    AND session."CredentialRevision" = account."CredentialRevision"
                ORDER BY
                    session."CreatedAt" DESC,
                    session."PublicId"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            var parameters = new SessionRevocationData(Auth.SessionPublicId!.Value, Parameters.AccountId);
            sql.Parameters.Add(parameters);
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<AccountSessionDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/accounts/{accountId:long}/sessions/{sessionPublicId:guid}")]
    public sealed class RevokeAccountSessionV1 : AdminService<RevokeAccountSessionParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<RevokeAccountSessionResultV1>()];
        protected override ModuleRequirement GetInstanceModules(RevokeAccountSessionParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(RevokeAccountSessionParametersV1 parameters, Type resultType) => new("accounts.update");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.RevokeSessionAsync(new SessionRevocationData(Parameters.SessionPublicId, Parameters.AccountId), CancellationToken);
            return new RevokeAccountSessionResultV1(Parameters.AccountId == Auth.AccountId && Parameters.SessionPublicId == Auth.SessionPublicId);
        }
    }
}
