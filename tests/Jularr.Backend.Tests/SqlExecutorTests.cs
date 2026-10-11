using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Tests.Infrastructure;
using Npgsql;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class SqlExecutorTests
    {
        private enum Status : byte
        {
            Pending = 1,
            Completed = 2
        }

        private sealed record Inputs(long Id, string Name, Status State);

        private sealed record UpdateFields(string Name);

        private sealed record Result(long Id, string Name, Status State);

        private sealed record JsonResult(long Id, IReadOnlyList<Child> Children);

        private sealed record Child(long Id);

        [TestMethod]
        public async Task SqlObject_ParameterObjects_UseCanonicalBindingAndReadGuard()
        {
            await using var database = CreateDataSource();
            await using var context = new SqlContext(database);
            await context.BeginAsync(SqlAccessMode.ReadOnly);
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    @Id::bigint AS "Id",
                    @Name::text AS "Name",
                    @State::smallint AS "State"
                """;
            sql.Parameters.Add(new { Id = 14L, State = Status.Completed, Unused = "not referenced" });
            sql.Parameters.Add(new UpdateFields("O'Brien"));
            sql.Parameters.Add(new { AnotherUnusedValue = 7L });

            var result = await context.ReadSql.ReadRequiredAsync<Result>(sql);
            Assert.AreEqual(14L, result.Id);
            Assert.AreEqual("O'Brien", result.Name);
            Assert.AreEqual(Status.Completed, result.State);

            sql.Statement = "DELETE FROM \"Profiles\"";
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => context.ReadSql.ExecuteScalarAsync(sql));
        }

        [TestMethod]
        public async Task SqlObject_ActorName_IsAnOrdinaryExplicitParameter()
        {
            await using var database = CreateDataSource();
            await using var context = new SqlContext(database);
            await context.BeginAsync(SqlAccessMode.ReadOnly);
            var sql = new SqlStatement();
            sql.Statement = "SELECT @ActorAccountId::bigint";
            sql.Parameters.Add(new { ActorAccountId = 999L });

            Assert.AreEqual(999L, await context.ReadSql.ExecuteScalarAsync(sql));
        }

        [TestMethod]
        public async Task ReadRequired_BindsFullDtoWithUnusedProperties()
        {
            await using var database = CreateDataSource();
            await using var context = new SqlContext(database);
            await context.BeginAsync(SqlAccessMode.ReadOnly);

            const string sql = """
                SELECT @Id::bigint AS "Id", @Name::text AS "Name", @State::smallint AS "State"
                """;

            var value = await context.ReadSql.ReadRequiredAsync<Result>(new SqlStatement { Statement = sql, Parameters = { new Inputs(14, "O'Brien", Status.Completed) } });

            Assert.AreEqual(14L, value.Id);
            Assert.AreEqual("O'Brien", value.Name);
            Assert.AreEqual(Status.Completed, value.State);
        }

        [TestMethod]
        public async Task TypedPostgresArrays_AreBoundFromCompleteDto()
        {
            await using var database = CreateDataSource();
            await using var context = new SqlContext(database);
            await context.BeginAsync(SqlAccessMode.ReadOnly);

            var count = await context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT cardinality(@Ids::bigint[])", Parameters = { new { Ids = new long[] { 11, 13, 17 } } } });
            Assert.AreEqual(3, count);

            var names = await context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT array_to_string(@Names::text[], ',')", Parameters = { new { Names = new[] { "one", "two" } } } });
            Assert.AreEqual("one,two", names);

            var empty = await context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT COALESCE(cardinality(@Ids::bigint[]), 0)", Parameters = { new { Ids = (long[]?)null } } });
            Assert.AreEqual(0, empty);
        }

        [TestMethod]
        public async Task TypedPostgresArrays_LeaveSqlCastsToPostgresAndRejectUnsupportedElementType()
        {
            await using var database = CreateDataSource();
            await using var context = new SqlContext(database);
            await context.BeginAsync(SqlAccessMode.ReadOnly);

            var count = await context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT cardinality(@Ids::integer[])", Parameters = { new { Ids = new long[] { 1, 2 } } } });
            Assert.AreEqual(2, count);

            await Assert.ThrowsExactlyAsync<NotSupportedException>(
                () => context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT cardinality(@Ids)", Parameters = { new { Ids = new DateTime[] { DateTime.UtcNow } } } }));
        }

        [TestMethod]
        public async Task ReadScalar_KeywordNamedParameter_RemainsValidSelect()
        {
            await using var database = CreateDataSource();
            await using var context = new SqlContext(database);
            await context.BeginAsync(SqlAccessMode.ReadOnly);

            var value = await context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT @Update::bigint", Parameters = { new { Update = 7L } } });
            Assert.AreEqual(7L, value);
        }

        [TestMethod]
        public async Task ReadRequired_UndefinedPersistedEnum_IsRejected()
        {
            await using var database = CreateDataSource();
            await using var context = new SqlContext(database);
            await context.BeginAsync(SqlAccessMode.ReadOnly);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => context.ReadSql.ReadRequiredAsync<Result>(new SqlStatement { Statement = "SELECT 1::bigint AS \"Id\", 'bad'::text AS \"Name\", 250::smallint AS \"State\"", Parameters = { new { } } }));
        }

        [TestMethod]
        public async Task RequiredAndOptionalRowReads_DistinguishNotFoundFromDuplicateRows()
        {
            await using var database = CreateDataSource();
            await using var context = new SqlContext(database);
            await context.BeginAsync(SqlAccessMode.ReadOnly);

            const string emptySql = "SELECT 1::bigint AS \"Id\" WHERE FALSE";
            var optional = await context.ReadSql.ReadOptionalAsync<SingleId>(new SqlStatement { Statement = emptySql, Parameters = { new { } } });
            Assert.IsNull(optional);

            await Assert.ThrowsExactlyAsync<KeyNotFoundException>(
                () => context.ReadSql.ReadRequiredAsync<SingleId>(new SqlStatement { Statement = emptySql, Parameters = { new { } } }));

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => context.ReadSql.ReadRequiredAsync<SingleId>(new SqlStatement { Statement = "SELECT \"Values\".\"Id\" FROM (VALUES (1::bigint), (2::bigint)) AS \"Values\"(\"Id\")", Parameters = { new { } } }));
        }

        [TestMethod]
        public async Task ReadPage_UsesValidatedPaginationAndMapsJsonChildren()
        {
            await using var database = CreateDataSource();
            await using var context = new SqlContext(database);
            await context.BeginAsync(SqlAccessMode.ReadOnly);

            var sql = new SqlStatement();
            sql.Statement = """
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

            sql.Page = new PageRequest(2, 2);
            var result = await context.ReadSql.ReadPageAsync<JsonResult>(sql);

            Assert.AreEqual(2, result.Page);
            Assert.AreEqual(1, result.Items.Count);
            Assert.AreEqual(3L, result.Items[0].Id);
            Assert.AreEqual(3L, result.Items[0].Children[0].Id);
        }

        [TestMethod]
        public async Task ReadPage_BindsExplicitPageWithLowerCaseSqlPlaceholders()
        {
            await using var database = CreateDataSource();
            await using var context = new SqlContext(database);
            await context.BeginAsync(SqlAccessMode.ReadOnly);

            var sql = new SqlStatement();
            sql.Statement = """
                SELECT "Values"."Id"
                FROM (VALUES (10::bigint), (20::bigint), (30::bigint)) AS "Values"("Id")
                ORDER BY "Values"."Id"
                LIMIT @pagesize
                OFFSET @offset
                """;

            sql.Page = new PageRequest(2, 2);
            var result = await context.ReadSql.ReadPageAsync<SingleId>(sql);
            Assert.AreEqual(1, result.Items.Count);
            Assert.AreEqual(30L, result.Items[0].Id);
        }

        private sealed record SingleId(long Id);

        [TestMethod]
        public async Task ReadPage_RequiresExplicitPageAndRejectsUnboundedRows()
        {
            await using var database = CreateDataSource();
            await using var context = new SqlContext(database);
            await context.BeginAsync(SqlAccessMode.ReadOnly);
            var sql = new SqlStatement();
            sql.Statement = "SELECT value::bigint AS \"Id\" FROM generate_series(1, 2) AS value";

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => context.ReadSql.ReadPageAsync<SingleId>(sql));
            sql.Page = new PageRequest(1, 1);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => context.ReadSql.ReadPageAsync<SingleId>(sql));
        }

        [TestMethod]
        public async Task Write_UsesParametersAndDataFromSameContext()
        {
            await using var database = CreateDataSource();
            await using var context = new SqlContext(database);
            await context.BeginAsync(SqlAccessMode.ReadWrite);
            var sql = context.RequireLogicSql();

            await sql.ExecuteAsync(new SqlStatement { Statement = "CREATE TABLE \"SqlExecutorWrite\" (\"Id\" bigint PRIMARY KEY, \"Name\" text NOT NULL)" });
            await sql.ExecuteAsync(new SqlStatement { Statement = "INSERT INTO \"SqlExecutorWrite\" (\"Id\", \"Name\") VALUES (@Id, @Name)", Parameters = { new { Id = 8L }, new UpdateFields("test") } });
            Assert.AreEqual("test", await context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT \"Name\" FROM \"SqlExecutorWrite\" WHERE \"Id\" = @Id", Parameters = { new { Id = 8L } } }));
            await context.CommitAsync();
        }

        [TestMethod]
        public async Task MissingParameter_IsRejectedByPostgres()
        {
            await using var database = CreateDataSource();
            await using var context = new SqlContext(database);
            await context.BeginAsync(SqlAccessMode.ReadOnly);

            var error = await Assert.ThrowsExactlyAsync<PostgresException>(
                () => context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT @Missing", Parameters = { new { Id = 1 } } }));
            Assert.AreEqual("42703", error.SqlState);
        }

        [TestMethod]
        public async Task TwoDtoObjectsWithSameParameter_AreRejected()
        {
            await using var database = CreateDataSource();
            await using var context = new SqlContext(database);
            await context.BeginAsync(SqlAccessMode.ReadWrite);

            await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => context.RequireLogicSql().ExecuteAsync(new SqlStatement { Statement = "SELECT @Name", Parameters = { new { Name = "first" }, new { Name = "second" } } }));
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

            var result = await context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT @Id::bigint, '@NotAParameter', $$@AlsoNotAParameter$$", Parameters = { new { Id = 17L } } });
            Assert.AreEqual(17L, result);

            var value = await context.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = sql, Parameters = { new { Id = 17L } } });
            Assert.AreEqual("@Fake", value);
        }

        private static NpgsqlDataSource CreateDataSource()
        {
            var connectionString = TestPostgres.ResolveConnectionString($"Data Source=sql-executor-{Guid.NewGuid():N}");
            return new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = true }.ConnectionString).Build();
        }
    }
}
