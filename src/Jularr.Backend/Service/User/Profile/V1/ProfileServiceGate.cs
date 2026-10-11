using System.Security;
using System.Globalization;
using Jularr.Data.Profile;
using Jularr.Infrastructure.Sql;
using Jularr.Logic.Profile;
using Jularr.Service.Core;
using Microsoft.AspNetCore.Authentication.Cookies;
using Npgsql;

namespace Jularr.Service.User.Profile.V1;

public sealed class ProfileServiceGate(
    NpgsqlDataSource database,
    IHttpContextAccessor accessor) : IServiceGate
{
    public const string SessionClaimType = "jularr.session_id";
    public const string SessionRevisionClaimType = "jularr.session_revision";

    private ServiceCaller? _caller;

    public Task RequireModulesAsync(ModuleRequirement requirement, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requirement.Mode != ModuleRequirementMode.None)
        {
            throw new UnauthorizedAccessException("No instance modules are configured in the profile backend.");
        }

        return Task.CompletedTask;
    }

    public async Task<ServiceCaller> RequireCallerAsync(ServiceArea area, CancellationToken cancellationToken)
    {
        _caller = null;
        var principal = accessor.HttpContext?.User;
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

        var session = await ReadSessionAsync(new ProfileSessionParameters(sessionId, revision), cancellationToken);
        _caller = new ServiceCaller(session.AccountId, session.ProfileId, area, sessionId.ToString("D"), sessionId, revision, session.ProfilePublicId, session.OwnerAccountId);
        return _caller;
    }

    public async Task RequireAccessAsync(ServiceCaller caller, ServicePermission permission, IReadOnlyList<ResourceTarget> targets, CancellationToken cancellationToken)
    {
        RequireOwnProfilePermission(caller, permission, targets);
        var current = await ReadSessionAsync(new ProfileSessionParameters(caller.SessionPublicId!.Value, caller.SessionRevision!.Value), cancellationToken);
        if (current.AccountId != caller.AccountId || current.ProfileId != caller.ProfileId
            || current.ProfilePublicId != caller.ProfilePublicId || current.OwnerAccountId != caller.ProfileOwnerAccountId)
        {
            throw new UnauthorizedAccessException("The profile session changed.");
        }

        if (permission.Key == "profile.self.update" && current.OwnerAccountId != caller.AccountId)
        {
            throw new SecurityException("Profile management requires ownership.");
        }
    }

    public async Task RequireTransactionalAccessAsync(ServiceCaller caller, ServicePermission permission, IReadOnlyList<ResourceTarget> targets, LogicContext context, CancellationToken cancellationToken)
    {
        RequireOwnProfilePermission(caller, permission, targets);
        if (permission.Key != "profile.self.update")
        {
            throw new SecurityException("The requested profile mutation is unavailable.");
        }

        await context.RequireProfileSessionForUpdateAsync(new ProfileSessionParameters(caller.SessionPublicId!.Value, caller.SessionRevision!.Value), cancellationToken);
    }

    private void RequireOwnProfilePermission(ServiceCaller caller, ServicePermission permission, IReadOnlyList<ResourceTarget> targets)
    {
        if (_caller is null || caller != _caller || permission.Key is not ("profile.self.read" or "profile.self.update")
            || targets.Any(target => target.Kind != "profile" || target.Id != caller.ProfileId))
        {
            throw new SecurityException("The requested profile permission is unavailable.");
        }
    }

    private async Task<ProfileSession> ReadSessionAsync(ProfileSessionParameters parameters, CancellationToken cancellationToken)
    {
        var sql = new SqlStatement();
        sql.Statement =
            """
            SELECT
                account."Id" AS "AccountId",
                profile."Id" AS "ProfileId",
                profile."OwnerAccountId",
                profile."PublicId" AS "ProfilePublicId"
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
        await using var context = new SqlContext(database);
        await context.BeginAsync(SqlAccessMode.ReadOnly, cancellationToken: cancellationToken);
        var session = await context.ReadSql.ReadOptionalAsync<ProfileSession>(sql, cancellationToken);
        return session ?? throw new UnauthorizedAccessException("The profile session is unavailable.");
    }
}
