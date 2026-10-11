using System.Globalization;
using System.Security;
using Jularr.Data.Enums.Service;
using Jularr.Data.Enums.Sql;
using Jularr.Data.Logic.Session;
using Jularr.Infrastructure.Sql;
using Jularr.Logic.Session;
using Microsoft.AspNetCore.Authentication.Cookies;
using Npgsql;

namespace Jularr.Service.Core;

public sealed class ServiceGate : IServiceGate
{
    public const string SessionClaimType = "jularr.session_id";
    public const string SessionRevisionClaimType = "jularr.session_revision";

    private readonly NpgsqlDataSource _database;
    private readonly IHttpContextAccessor _accessor;
    private ServiceCaller? _caller;

    public ServiceGate(NpgsqlDataSource database, IHttpContextAccessor accessor)
    {
        _database = database;
        _accessor = accessor;
    }
    public Task RequireModulesAsync(ModuleRequirement requirement, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requirement.Mode != ModuleRequirementMode.None)
        {
            throw new UnauthorizedAccessException("No instance modules are configured.");
        }

        return Task.CompletedTask;
    }

    public async Task<ServiceCaller> RequireCallerAsync(ServiceArea area, CancellationToken cancellationToken)
    {
        _caller = null;
        if (area == ServiceArea.Public)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _caller = new ServiceCaller(null, null, area, "anonymous");
            return _caller;
        }

        var principal = _accessor.HttpContext?.User;
        var identities = principal?.Identities.Where(identity => identity.IsAuthenticated
            && identity.AuthenticationType == CookieAuthenticationDefaults.AuthenticationScheme).ToArray();
        var claims = identities?.SelectMany(identity => identity.FindAll(SessionClaimType)).ToArray();
        var revisions = identities?.SelectMany(identity => identity.FindAll(SessionRevisionClaimType)).ToArray();

        if (area != ServiceArea.User || identities?.Length != 1 || claims?.Length != 1
            || !Guid.TryParseExact(claims[0].Value, "D", out var sessionId) || sessionId == Guid.Empty
            || revisions?.Length != 1 || !long.TryParse(revisions[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision <= 0)
        {
            throw new UnauthorizedAccessException("A verified profile session is required.");
        }

        // Resolve the protected cookie against the current database state.
        var session = await ReadSessionAsync(new SessionParameters(sessionId, revision), cancellationToken);
        _caller = new ServiceCaller(session.AccountId, session.ProfileId, area, sessionId.ToString("D"), sessionId, revision, session.ProfileOwnerAccountId);
        return _caller;
    }

    public Task RequireAccessAsync(
        ServiceCaller caller,
        ServicePermission permission,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequirePermission(caller, permission);

        return Task.CompletedTask;
    }

    public async Task RequireTransactionalAccessAsync(
        ServiceCaller caller,
        ServicePermission permission,
        LogicContext context,
        CancellationToken cancellationToken)
    {
        RequirePermission(caller, permission);

        if (caller.Area == ServiceArea.Public)
        {
            return;
        }

        // Mutable session state is locked in the same transaction as the write.
        var parameters = new SessionParameters(caller.SessionPublicId!.Value, caller.SessionRevision!.Value);
        var current = await context.ReadSessionForUpdateAsync(parameters, cancellationToken);

        if (current.AccountId != caller.AccountId || current.ProfileId != caller.ProfileId
            || current.ProfileOwnerAccountId != caller.ProfileOwnerAccountId)
        {
            throw new UnauthorizedAccessException("The profile session changed.");
        }
    }

    private void RequirePermission(ServiceCaller caller, ServicePermission permission)
    {
        if (_caller != caller)
        {
            throw new SecurityException("The caller has not been verified.");
        }

        if (caller.Area == ServiceArea.Public && permission == ServicePermission.Public)
        {
            return;
        }

        // Only the currently implemented protected grants are available.
        if (caller.Area == ServiceArea.User && permission.Key is "profile.self.read" or "profile.self.update")
        {
            return;
        }

        throw new SecurityException("The requested permission is unavailable.");
    }

    private async Task<SessionAuthenticationData> ReadSessionAsync(SessionParameters parameters, CancellationToken cancellationToken)
    {
        var sql = new SqlStatement();
        sql.Statement =
            """
            SELECT
                account."Id" AS "AccountId",
                profile."Id" AS "ProfileId",
                profile."OwnerAccountId" AS "ProfileOwnerAccountId"
            FROM
                "AccountSessions" AS session
            INNER JOIN
                "Accounts" AS account ON account."Id" = session."AccountId"
            INNER JOIN
                "AccountProfiles" AS membership ON membership."AccountId" = account."Id"
                    AND membership."ProfileId" = session."ActiveProfileId"
            INNER JOIN
                "Profiles" AS profile ON profile."Id" = membership."ProfileId"
            WHERE
                session."PublicId" = @SessionPublicId
                AND session."RotationRevision" = @SessionRevision
                AND session."RevokedAt" IS NULL
                AND session."ExpiresAt" > statement_timestamp()
                AND session."CredentialRevision" = account."CredentialRevision"
                AND account."IsEnabled" = TRUE;
            """;
        sql.Parameters.Add(parameters);

        await using var context = new SqlContext(_database);
        await context.BeginAsync(SqlAccessMode.ReadOnly, cancellationToken: cancellationToken);
        var session = await context.ReadSql.ReadOptionalAsync<SessionAuthenticationData>(sql, cancellationToken);
        return session ?? throw new UnauthorizedAccessException("The profile session is unavailable.");
    }
}
