using Jularr.Data.Logic.Session;
using Jularr.Infrastructure.Sql;

namespace Jularr.Logic.Session;

public static class SessionLogic
{
    public static async Task<SessionAuthenticationData> ReadSessionForUpdateAsync(
        this LogicContext context,
        SessionParameters parameters,
        CancellationToken cancellationToken)
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
                AND account."Id" = @ActorAccountId
                AND profile."Id" = @ActiveProfileId
                AND session."RevokedAt" IS NULL
                AND session."ExpiresAt" > statement_timestamp()
                AND session."CredentialRevision" = account."CredentialRevision"
                AND account."IsEnabled" = TRUE
            FOR UPDATE OF
                account, membership, profile, session;
            """;
        sql.Parameters.Add(parameters);

        try
        {
            return await context.ReadForUpdateAsync<SessionAuthenticationData>(sql, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            throw new UnauthorizedAccessException("The profile session is unavailable.");
        }
    }
}
