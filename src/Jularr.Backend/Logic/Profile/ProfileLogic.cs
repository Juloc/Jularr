using Jularr.Data.Common;
using Jularr.Data.Logic.Profile;
using Jularr.Infrastructure.Sql;
using Npgsql;

namespace Jularr.Logic.Profile;

public static class ProfileLogic
{
    public static Task<ProfileUpdateData> ReadProfileForUpdateAsync(
        this LogicContext context,
        long profileId,
        CancellationToken cancellationToken)
    {
        var sql = new SqlStatement();
        sql.Statement =
            """
            SELECT
                profile."Id",
                profile."DisplayName",
                profile."UiLocaleId"
            FROM
                "Profiles" AS profile
            WHERE
                profile."Id" = @ProfileId
            FOR UPDATE OF
                profile;
            """;
        sql.Parameters.Add(new ProfileParameters(profileId));

        return context.ReadForUpdateAsync<ProfileUpdateData>(sql, cancellationToken);
    }

    public static async Task UpdateProfileAsync(
        this LogicContext context,
        ProfileUpdateData updateData,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(updateData);

        updateData.Validate();

        var sql = new SqlStatement();
        sql.Statement =
            """
            UPDATE
                "Profiles" AS profile
            SET
                "DisplayName" = @DisplayName,
                "UiLocaleId" = @UiLocaleId,
                "UpdatedAt" = now()
            WHERE
                profile."Id" = @Id;
            """;
        sql.Parameters.Add(updateData);

        int affected;
        try
        {
            affected = await context.ExecuteAsyncForAffectedRows(sql, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation
            && exception.ConstraintName == "FK_Profiles_UiLocales")
        {
            throw new DataValidationException(nameof(updateData.UiLocaleId), "unknown_reference");
        }

        if (affected != 1)
        {
            throw new KeyNotFoundException("The authorized profile was not found.");
        }
    }
}
