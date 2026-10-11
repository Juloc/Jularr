using Jularr.Data.Profile;
using Jularr.Infrastructure.Sql;

namespace Jularr.Logic.Profile;

public static class ProfileLogic
{
    private const string SessionForUpdateSql =
        """
        SELECT
            account."Id" AS "AccountId",
            profile."Id" AS "ProfileId",
            profile."OwnerAccountId"
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
            AND profile."OwnerAccountId" = @ActorAccountId
            AND session."RevokedAt" IS NULL
            AND session."ExpiresAt" > statement_timestamp()
            AND session."CredentialRevision" = account."CredentialRevision"
            AND account."IsEnabled" = TRUE
        FOR UPDATE OF
            account, membership, profile, session;
        """;

    private const string RenameSql =
        """
        UPDATE
            "Profiles" AS profile
        SET
            "DisplayName" = @DisplayName,
            "UpdatedAt" = now()
        WHERE
            profile."PublicId" = @PublicId
            AND profile."Id" = @ActiveProfileId
            AND profile."OwnerAccountId" = @ActorAccountId
            AND EXISTS (
                SELECT
                    1
                FROM
                    "AccountProfiles" AS membership
                INNER JOIN
                    "Accounts" AS account ON account."Id" = membership."AccountId"
                WHERE
                    membership."ProfileId" = profile."Id"
                    AND membership."AccountId" = @ActorAccountId
                    AND account."IsEnabled" = TRUE
            );
        """;

    public static async Task RequireProfileSessionForUpdateAsync(this LogicContext context, ProfileSessionParameters parameters, CancellationToken cancellationToken)
    {
        try
        {
            await context.Sql.ReadForUpdateAsync<ProfileSession>(SessionForUpdateSql, parameters, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            throw new UnauthorizedAccessException("The profile session is unavailable.");
        }
    }

    public static async Task RenameProfileAsync(
        this LogicContext context,
        Guid publicId,
        string displayName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (publicId == Guid.Empty)
        {
            throw new ArgumentException("Profile public ID is required.", nameof(publicId));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Profile display name is required.", nameof(displayName));
        }

        var change = new ProfileNameChange(publicId, displayName.Trim());
        var affected = await context.Sql.ExecuteAsync(
            RenameSql, change, cancellationToken);

        if (affected != 1)
        {
            throw new KeyNotFoundException("The authorized profile was not found.");
        }
    }
}
