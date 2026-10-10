using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Jularr.Tests;

[TestClass]
public sealed class SqlParamsPostgresTests
{
    private enum ExampleState : byte
    {
        Ready = 2
    }

    [TestMethod]
    public async Task Parameters_ExecuteWithCorrectTypesAgainstPostgres()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        await db.Database.OpenConnectionAsync();

        const string sql = """
            SELECT
                @TextValue AS "TextValue",
                @NullNumber AS "NullNumber",
                @State AS "State",
                @JsonValue ->> 'name' AS "JsonName",
                CARDINALITY(@Languages) AS "LanguageCount",
                @Offset AS "Offset",
                @PageSize AS "PageSize"
            """;

        var data = new
        {
            TextValue = "O'Brien'; SELECT 1; --",
            NullNumber = (long?)null,
            State = (ExampleState?)ExampleState.Ready,
            JsonValue = "{\"name\":\"Jularr\"}",
            Languages = new[] { "de", "en" }
        };

        var parameters = SqlParams.From(data)
            .Add(nameof(data.TextValue))
            .Add(nameof(data.NullNumber))
            .Add(nameof(data.State))
            .Add(nameof(data.JsonValue), NpgsqlDbType.Jsonb)
            .Add(nameof(data.Languages), NpgsqlDbType.Array | NpgsqlDbType.Text)
            .ToArray();

        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddRange(parameters.Concat(new PageRequest(3, 25).ToSqlParameters()).ToArray());

        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(data.TextValue, reader.GetString(0));
        Assert.IsTrue(reader.IsDBNull(1));
        Assert.AreEqual((short)2, reader.GetInt16(2));
        Assert.AreEqual("Jularr", reader.GetString(3));
        Assert.AreEqual(2, reader.GetInt32(4));
        Assert.AreEqual(50L, reader.GetInt64(5));
        Assert.AreEqual(25, reader.GetInt32(6));
        Assert.IsFalse(await reader.ReadAsync());
    }

    [TestMethod]
    public async Task Parameters_BindBooleanGuidUtcAndDateAgainstPostgres()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        await db.Database.OpenConnectionAsync();

        const string sql = """
            SELECT
                @Enabled AS "Enabled",
                @Identifier AS "Identifier",
                @CreatedAt AS "CreatedAt",
                @Date AS "Date"
            """;

        var id = Guid.NewGuid();
        var utc = new DateTime(2026, 10, 10, 12, 30, 0, DateTimeKind.Utc);
        var date = new DateOnly(2026, 10, 10);
        var parameters = SqlParams.Create()
            .Add("Enabled", true)
            .Add("Identifier", id)
            .Add("CreatedAt", utc)
            .Add("Date", date)
            .ToArray();

        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddRange(parameters);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.IsTrue(reader.GetBoolean(0));
        Assert.AreEqual(id, reader.GetGuid(1));
        Assert.AreEqual(utc, reader.GetDateTime(2));
        Assert.AreEqual(date, reader.GetFieldValue<DateOnly>(3));
    }
}
