using System.Security;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Provider
{
    public static class ProviderLogic
    {
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
