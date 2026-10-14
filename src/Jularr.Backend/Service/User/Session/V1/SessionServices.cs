using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Data.User.Session.V1;
using Jularr.Backend.Logic.Session;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Data;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Service.User.Session.V1
{
    [ServiceEndpoint("GET", "/api/v1/sessions")]
    public sealed class ReadSessionsV1 : UserService<ReadSessionsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<SessionDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadSessionsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadSessionsParametersV1 parameters, Type resultType) => new("session.self.read");
        protected override PageRequest GetPageRequest(ReadSessionsParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // List live account sessions only.
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
                    AND session."RevokedAt" IS NULL
                    AND session."ExpiresAt" > statement_timestamp()
                    AND session."CredentialRevision" = account."CredentialRevision"
                ORDER BY
                    session."CreatedAt" DESC,
                    session."PublicId"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new SessionRevocationData(Auth.SessionPublicId!.Value, Auth.AccountId!.Value));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<SessionDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/auth/session")]
    public sealed class LogoutV1 : UserService<SessionParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LogoutResultV1>()];
        protected override ModuleRequirement GetInstanceModules(SessionParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(SessionParametersV1 parameters, Type resultType) => new("session.self.logout");

        protected override async Task<IServiceOutput> OnDelete()
        {
            // Revoke the verified account session.
            await Context.RevokeSessionAsync(new SessionRevocationData(Auth.SessionPublicId!.Value, Auth.AccountId!.Value), CancellationToken);
            return new LogoutResultV1();
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/sessions/{sessionPublicId:guid}")]
    public sealed class RevokeSessionV1 : UserService<RevokeSessionParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<RevokeSessionResultV1>()];
        protected override ModuleRequirement GetInstanceModules(RevokeSessionParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(RevokeSessionParametersV1 parameters, Type resultType) => new("session.self.revoke");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.RevokeSessionAsync(new SessionRevocationData(Parameters.SessionPublicId, Auth.AccountId!.Value), CancellationToken);
            return new RevokeSessionResultV1(Parameters.SessionPublicId == Auth.SessionPublicId);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/sessions/{sessionPublicId:guid}/device")]
    public sealed class RenameSessionDeviceV1 : UserService<RevokeSessionParametersV1, RenameSessionDeviceDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(RevokeSessionParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(RevokeSessionParametersV1 parameters, Type resultType) => new("session.self.revoke");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.RenameSessionDeviceAsync(new SessionRevocationData(Parameters.SessionPublicId, Auth.AccountId!.Value), new SessionDeviceData(Data.DeviceName, null),
                CancellationToken);
            return new UpdateResult(true);
        }
    }
}
