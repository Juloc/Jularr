using Jularr.Data.Common;
using Jularr.Data.Profile;
using Jularr.Infrastructure.Sql;
using Jularr.Tests.Infrastructure;
using Jularr.Web.Data;
using Npgsql;
using NpgsqlTypes;

namespace Jularr.Tests;

[TestClass]
public sealed class DataFieldTests
{
    private sealed record RequiredName(
        [property: DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))] string? DisplayName);

    private sealed record OptionalName(
        [property: DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName), false)] string? DisplayName);

    private sealed record InvalidName(
        [property: DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))] long DisplayName);

    private sealed record OptionalId(
        [property: DataField(typeof(ProfileFields), nameof(ProfileFields.Id), false)] long? Id);

    [TestMethod]
    public void Field_RequiredAndOptional_ReuseOneDatabaseDefinition()
    {
        Assert.AreEqual("Profiles", ProfileFields.DisplayName.Table);
        Assert.AreEqual("DisplayName", ProfileFields.DisplayName.Column);
        Assert.AreEqual(typeof(string), ProfileFields.DisplayName.ClrType);
        Assert.ThrowsExactly<ArgumentException>(() => SqlParams.From(new RequiredName(null)).Add("DisplayName"));

        var parameter = SqlParams.From(new OptionalName(null)).Add("DisplayName").ToArray().Single();
        Assert.AreEqual(NpgsqlDbType.Text, parameter.NpgsqlDbType);
        Assert.AreEqual(DBNull.Value, parameter.Value);

        var id = SqlParams.From(new OptionalId(null)).Add("Id").ToArray().Single();
        Assert.AreEqual(NpgsqlDbType.Bigint, id.NpgsqlDbType);
        Assert.AreEqual(DBNull.Value, id.Value);
    }

    [TestMethod]
    public void Field_ConflictingClrOrSqlType_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => SqlParams.From(new InvalidName(1)).Add("DisplayName"));
        Assert.ThrowsExactly<ArgumentException>(() => SqlParams.From(new RequiredName("Name")).Add("DisplayName", NpgsqlDbType.Jsonb));
    }

    [TestMethod]
    public async Task Field_PostgresBindingAndProjection_EnforceNullability()
    {
        await using var database = new NpgsqlDataSourceBuilder(TestPostgres.ResolveConnectionString("Data Source=profile-data-fields")).Build();
        await using var sql = new SqlContext(database);
        await sql.BeginAsync(SqlAccessMode.ReadOnly);
        const string statement = """
            SELECT
                @DisplayName AS "DisplayName"
            """;

        var value = await sql.ReadSql.ReadRequiredAsync<RequiredName>(statement, new RequiredName("O'Brien"));
        Assert.AreEqual("O'Brien", value.DisplayName);
        var optional = await sql.ReadSql.ReadRequiredAsync<OptionalName>(statement, new OptionalName(null));
        Assert.IsNull(optional.DisplayName);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sql.ReadSql.ReadRequiredAsync<RequiredName>(statement, new OptionalName(null)));
    }
}
