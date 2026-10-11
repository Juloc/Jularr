using Jularr.Data.Profile;
using Jularr.Infrastructure.Sql;

namespace Jularr.Logic.Profile;

// One canonical writer; Service and API code never modify Profiles directly.
public sealed class ProfileLogic
{
    private const string RenameSql =
        """
        UPDATE "Profiles" AS profile
        SET "DisplayName" = @DisplayName,
            "UpdatedAt" = now()
        WHERE profile."PublicId" = @PublicId
          AND profile."Id" = @ActiveProfileId
          AND EXISTS (
              SELECT 1
              FROM "AccountProfiles" AS membership
              JOIN "Accounts" AS account
                ON account."Id" = membership."AccountId"
              WHERE membership."ProfileId" = profile."Id"
                AND membership."AccountId" = @ActorAccountId
                AND account."IsEnabled" = true
          );
        """;

    public async Task RenameAsync(
        LogicContext context,
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

        // Validate and normalize exactly once inside the domain writer.
        var change = new ProfileNameChange(publicId, displayName.Trim());
        var affected = await context.Sql.ExecuteAsync(
            RenameSql, change, cancellationToken);

        if (affected != 1)
        {
            // The same outcome is used for missing, disabled, revoked and foreign profiles.
            throw new KeyNotFoundException("The authorized profile was not found.");
        }
    }
}
