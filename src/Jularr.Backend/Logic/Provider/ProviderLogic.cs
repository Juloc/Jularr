using System.Security;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Provider
{
    internal static class ProviderLogic
    {
        public static async Task UpdateProviderAsync(this LogicContext ctx, ProviderUpdateData data, CancellationToken ct)
        {
            // Change the registry flag only.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Providers" AS provider
                SET
                    "IsEnabled" = @IsEnabled
                WHERE
                    provider."Id" = @Id
                    AND provider."IsEnabled" = @ExpectedIsEnabled;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.IsEnabled), "invalid_transition");
            }
        }

        public static async Task<ProviderData> ReadEnabledProviderAsync(this LogicContext ctx, ProviderParameters parameters, CancellationToken ct)
        {
            // Hold configuration through completion.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    provider."Id"
                FROM
                    "Providers" AS provider
                WHERE
                    provider."Key" = @ProviderKey
                    AND provider."IsEnabled" = TRUE
                FOR SHARE OF
                    provider;
                """;
            sql.Parameters.Add(parameters);

            var provider = await ctx.ReadOptionalAsync<ProviderData>(sql, ct);
            return provider ?? throw new SecurityException("The provider is unavailable.");
        }

        public static async Task SaveProviderSecretAsync(this LogicContext ctx, ProviderSecretData data, CancellationToken ct)
        {
            // An empty secret removes the credential.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Providers" AS provider
                SET
                    "ProtectedSecret" = @ProtectedSecret
                WHERE
                    provider."Id" = @Id;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The provider was not found.");
            }
        }

        public static Task<ProviderCredentialData?> ReadProviderCredentialAsync(this LogicContext ctx, ProviderParameters parameters, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    provider."Id",
                    provider."IsEnabled",
                    provider."ProtectedSecret"
                FROM
                    "Providers" AS provider
                WHERE
                    provider."Key" = @ProviderKey;
                """;
            sql.Parameters.Add(parameters);
            return ctx.ReadSql.ReadOptionalAsync<ProviderCredentialData>(sql, ct);
        }
    }
}
