using System.Security.Claims;
using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Jularr.Web.Features.Auth;

public sealed class AdminAccountService(AppDbContext db)
{
    private const string ReadUsersSql = """
        SELECT
            "Id",
            "UserName",
            "Role",
            "IsEnabled",
            "CreatedAt"
        FROM
            "OwnerAccounts"
        WHERE
            STRPOS("NormalizedUserName", @Query) > 0
        ORDER BY
            "Role" ASC,
            "UserName" ASC,
            "Id" ASC
        LIMIT
            @PageSize
        OFFSET
            @Offset
        """;

    private const string CountUsersSql = """
        SELECT
            COUNT(*)
        FROM
            "OwnerAccounts"
        WHERE
            STRPOS("NormalizedUserName", @Query) > 0
        """;

    public async Task<PageResult<LocalAccountSummary>> ReadUsersV1(ClaimsPrincipal actor, PageRequest paging, CancellationToken cancellationToken = default, string? query = null)
    {
        if (!JularrPolicies.Allows(actor, JularrPolicies.AdminSystem))
        {
            throw new UnauthorizedAccessException();
        }

        ArgumentNullException.ThrowIfNull(paging);
        var normalizedQuery = (query ?? string.Empty).Trim().ToUpperInvariant();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(normalizedQuery.Length, 80, nameof(query));

        await db.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var items = new List<LocalAccountSummary>(paging.PageSize);

            await using (var command = new NpgsqlCommand(ReadUsersSql, connection))
            {
                command.Parameters.AddRange(paging.ToSqlParameters());
                command.Parameters.Add(new NpgsqlParameter("Query", NpgsqlDbType.Text) { Value = normalizedQuery });

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    items.Add(new LocalAccountSummary(reader.GetString(0), reader.GetString(1), (AccountRole)reader.GetInt32(2), reader.GetBoolean(3), reader.GetDateTime(4)));
                }
            }

            await using var countCommand = new NpgsqlCommand(CountUsersSql, connection);
            countCommand.Parameters.Add(new NpgsqlParameter("Query", NpgsqlDbType.Text) { Value = normalizedQuery });
            var count = (long)(await countCommand.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Account count was unavailable."));

            return PageResult<LocalAccountSummary>.From(items, paging, totalCount: count, hasMore: paging.Offset + items.Count < count);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
