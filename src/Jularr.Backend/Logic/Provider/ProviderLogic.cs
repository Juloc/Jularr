using System.Security;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Provider
{
    internal static class ProviderLogic
    {
        public static async Task UpdateProviderAsync(this LogicContext context, ProviderUpdateData data, CancellationToken cancellationToken)
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
            if (await context.ExecuteAsyncForAffectedRows(sql, cancellationToken) != 1)
            {
                throw new DataValidationException(nameof(data.IsEnabled), "invalid_transition");
            }
        }

        public static async Task<ProviderData> ReadEnabledProviderAsync(this LogicContext context, ProviderParameters parameters, CancellationToken cancellationToken)
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

            var provider = await context.ReadOptionalAsync<ProviderData>(sql, cancellationToken);
            return provider ?? throw new SecurityException("The provider is unavailable.");
        }
    }
}
