using Jularr.Data.Profile;
using Jularr.Infrastructure.Sql;

namespace Jularr.Logic.Profile;

public static class ProfileLogic
{
    public static async Task RequireProfileSessionForUpdateAsync(this LogicContext context, ProfileSessionParameters parameters, CancellationToken cancellationToken)
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
        sql.Parameters.Add(parameters);
        try
        {
            await context.ReadForUpdateAsync<ProfileSession>(sql, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            throw new UnauthorizedAccessException("The profile session is unavailable.");
        }
    }

    public static Task<ProfileUpdateData> ReadProfileForUpdateAsync(
        this LogicContext context,
        Guid publicId,
        CancellationToken cancellationToken)
    {
        var sql = new SqlStatement();
        sql.Statement =
            """
            SELECT
                profile."Id",
                profile."PublicId",
                profile."DisplayName"
            FROM
                "Profiles" AS profile
            INNER JOIN
                "AccountProfiles" AS membership ON membership."ProfileId" = profile."Id"
            INNER JOIN
                "Accounts" AS account ON account."Id" = membership."AccountId"
            WHERE
                profile."PublicId" = @PublicId
                AND profile."Id" = @ActiveProfileId
                AND profile."OwnerAccountId" = @ActorAccountId
                AND membership."AccountId" = @ActorAccountId
                AND account."IsEnabled" = TRUE
            FOR UPDATE OF
                profile;
            """;
        sql.Parameters.Add(new ProfileLookupParameters(publicId));
        return context.ReadForUpdateAsync<ProfileUpdateData>(sql, cancellationToken);
    }

    public static void SetData(this ProfileUpdateData updateData, ProfileNameChange change)
    {
        ArgumentNullException.ThrowIfNull(updateData);
        ArgumentNullException.ThrowIfNull(change);

        if (change.PublicId != updateData.PublicId || change.PublicId == Guid.Empty)
        {
            throw new ArgumentException("Profile changes must target the loaded profile.", nameof(change));
        }

        if (string.IsNullOrWhiteSpace(change.DisplayName))
        {
            throw new ArgumentException("Profile display name is required.", nameof(change));
        }

        updateData.DisplayName = change.DisplayName.Trim();
    }

    public static async Task UpdateProfileAsync(this LogicContext context, ProfileUpdateData updateData, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(updateData);

        if (updateData.Id <= 0 || updateData.PublicId == Guid.Empty || string.IsNullOrWhiteSpace(updateData.DisplayName))
        {
            throw new ArgumentException("A valid loaded profile is required.", nameof(updateData));
        }

        var sql = new SqlStatement();
        sql.Statement =
            """
            UPDATE
                "Profiles" AS profile
            SET
                "DisplayName" = @DisplayName,
                "UpdatedAt" = now()
            WHERE
                profile."PublicId" = @PublicId
                AND profile."Id" = @Id
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
        sql.Parameters.Add(updateData);
        var affected = await context.ExecuteAsyncForAffectedRows(sql, cancellationToken);

        if (affected != 1)
        {
            throw new KeyNotFoundException("The authorized profile was not found.");
        }
    }
}
