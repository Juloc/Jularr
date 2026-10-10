using Jularr.Infrastructure.Sql;
using Jularr.Tests.Infrastructure;
using Npgsql;

namespace Jularr.Tests;

[TestClass]
public sealed class SqlExecutorTests
{
    private enum Status : byte
    {
        Pending = 1,
        Completed = 2
    }

    private sealed record Inputs(long Id, string Name, Status State, int Page = 1, int PageSize = 25);

    private sealed record UpdateFields(string Name);

    private sealed record Result(long Id, string Name, Status State);

    private sealed record JsonResult(long Id, IReadOnlyList<Child> Children);

    private sealed record Child(long Id);

    [TestMethod]
    public async Task ReadRequired_BindsFullDtoWithoutAddingUnusedProperties()
    {
        await using var database = CreateDataSource();
        await using var context = new SqlContext(database);
        await context.BeginAsync(SqlAccessMode.ReadOnly);

        const string sql = """
            SELECT @Id::bigint AS "Id", @Name::text AS "Name", @State::smallint AS "State"
            """;

        var value = await context.ReadSql.ReadRequiredAsync<Result>(sql, new Inputs(14, "O'Brien", Status.Completed));

        Assert.AreEqual(14L, value.Id);
        Assert.AreEqual("O'Brien", value.Name);
        Assert.AreEqual(Status.Completed, value.State);
    }

    [TestMethod]
    public async Task ReadPage_UsesValidatedPaginationAndMapsJsonChildren()
    {
        await using var database = CreateDataSource();
        await using var context = new SqlContext(database);
        await context.BeginAsync(SqlAccessMode.ReadOnly);

        const string sql = """
            SELECT
                "Rows"."Id",
                jsonb_build_array(jsonb_build_object('id', "Rows"."Id")) AS "Children"
            FROM
                (VALUES (1::bigint), (2::bigint), (3::bigint)) AS "Rows"("Id")
            ORDER BY
                "Rows"."Id"
            LIMIT @PageSize
            OFFSET @Offset
            """;

        var result = await context.ReadSql.ReadPageAsync<JsonResult>(sql, new Inputs(0, string.Empty, Status.Pending, Page: 2, PageSize: 2));

        Assert.AreEqual(2, result.Page);
        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual(3L, result.Items[0].Id);
        Assert.AreEqual(3L, result.Items[0].Children[0].Id);
    }

    [TestMethod]
    public async Task ReadPage_RecognizesLowerCaseReservedSqlPlaceholders()
    {
        await using var database = CreateDataSource();
        await using var context = new SqlContext(database);
        await context.BeginAsync(SqlAccessMode.ReadOnly);

        const string sql = """
            SELECT "Values"."Id"
            FROM (VALUES (10::bigint), (20::bigint), (30::bigint)) AS "Values"("Id")
            ORDER BY "Values"."Id"
            LIMIT @pagesize
            OFFSET @offset
            """;

        var result = await context.ReadSql.ReadPageAsync<SingleId>(sql, new { Page = 2, PageSize = 2 });
        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual(30L, result.Items[0].Id);
    }

    private sealed record SingleId(long Id);

    [TestMethod]
    public async Task Write_UsesParametersAndDataFromSameContext()
    {
        await using var database = CreateDataSource();
        await using var context = new SqlContext(database);
        await context.BeginAsync(SqlAccessMode.ReadWrite);
        var sql = context.RequireLogicSql();

        await sql.ExecuteAsync("CREATE TABLE \"SqlExecutorWrite\" (\"Id\" bigint PRIMARY KEY, \"Name\" text NOT NULL)");
        await sql.ExecuteAsync("INSERT INTO \"SqlExecutorWrite\" (\"Id\", \"Name\") VALUES (@Id, @Name)", new { Id = 8L }, new UpdateFields("test"));
        Assert.AreEqual("test", await context.ReadSql.ExecuteScalarAsync("SELECT \"Name\" FROM \"SqlExecutorWrite\" WHERE \"Id\" = @Id", new { Id = 8L }));
        await context.CommitAsync();
    }

    [TestMethod]
    public async Task MissingDuplicateAndReservedParameters_FailClosed()
    {
        await using var database = CreateDataSource();
        await using var context = new SqlContext(database);
        await context.BeginAsync(SqlAccessMode.ReadOnly);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.ReadSql.ExecuteScalarAsync("SELECT @Missing", new { Id = 1 }));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.ReadSql.ExecuteScalarAsync("SELECT @ActorAccountId", new { ActorAccountId = 1L }));
    }

    [TestMethod]
    public async Task TwoDtoObjectsWithSameParameter_AreRejected()
    {
        await using var database = CreateDataSource();
        await using var context = new SqlContext(database);
        await context.BeginAsync(SqlAccessMode.ReadWrite);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.RequireLogicSql().ExecuteAsync("SELECT @Name", new { Name = "first" }, new { Name = "second" }));
    }

    [TestMethod]
    public async Task LiteralsCommentsAndDollarQuotes_AreNotBound()
    {
        await using var database = CreateDataSource();
        await using var context = new SqlContext(database);
        await context.BeginAsync(SqlAccessMode.ReadOnly);

        const string sql = """
            SELECT '@Fake' AS "Text",
                   $$@SecondFake$$ AS "Quoted",
                   @Id::bigint AS "Id"
            -- @ThirdFake
            /* nested @FourthFake /* @FifthFake */ */
            """;

        var result = await context.ReadSql.ExecuteScalarAsync("SELECT @Id::bigint, '@NotAParameter', $$@AlsoNotAParameter$$", new { Id = 17L });
        Assert.AreEqual(17L, result);

        var value = await context.ReadSql.ExecuteScalarAsync(sql, new { Id = 17L });
        Assert.AreEqual("@Fake", value);
    }

    private static NpgsqlDataSource CreateDataSource()
    {
        var connectionString = TestPostgres.ResolveConnectionString($"Data Source=sql-executor-{Guid.NewGuid():N}");
        return new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = true }.ConnectionString).Build();
    }
}
