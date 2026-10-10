using Jularr.Web.Data;
using NpgsqlTypes;

namespace Jularr.Tests;

[TestClass]
public sealed class SqlParamsTests
{
    private enum State : byte
    {
        Pending = 1,
        Finished = 2
    }

    private enum UnsupportedState
    {
        Pending = 1
    }

    private sealed record AccountData(long AccountId, string? DisplayName, State? State);

    [TestMethod]
    public void From_OnlyBindsSelectedProperties()
    {
        var data = new AccountData(42, "O'Brien", State.Finished);

        var parameters = SqlParams.From(data)
            .Add(nameof(data.AccountId))
            .Add(nameof(data.DisplayName))
            .ToArray();

        Assert.AreEqual(2, parameters.Length);
        Assert.AreEqual("AccountId", parameters[0].ParameterName);
        Assert.AreEqual(NpgsqlDbType.Bigint, parameters[0].NpgsqlDbType);
        Assert.AreEqual(42L, parameters[0].Value);
        Assert.AreEqual("DisplayName", parameters[1].ParameterName);
        Assert.AreEqual(NpgsqlDbType.Text, parameters[1].NpgsqlDbType);
        Assert.AreEqual("O'Brien", parameters[1].Value);
    }

    [TestMethod]
    public void From_NullValues_KeepDeclaredSqlTypes()
    {
        var data = new AccountData(1, null, null);

        var parameters = SqlParams.From(data)
            .Add(nameof(data.DisplayName))
            .Add(nameof(data.State))
            .ToArray();

        Assert.AreEqual(NpgsqlDbType.Text, parameters[0].NpgsqlDbType);
        Assert.AreEqual(DBNull.Value, parameters[0].Value);
        Assert.AreEqual(NpgsqlDbType.Smallint, parameters[1].NpgsqlDbType);
        Assert.AreEqual(DBNull.Value, parameters[1].Value);
    }

    [TestMethod]
    public void Create_InferredTypes_AreNamedAndTyped()
    {
        var now = new DateTime(2026, 10, 10, 10, 0, 0, DateTimeKind.Utc);
        var offset = new DateTimeOffset(now);

        var parameters = SqlParams.Create()
            .Add("Long", 10L)
            .Add("Integer", 10)
            .Add("Short", (short)2)
            .Add("Byte", (byte)3)
            .Add("Text", "test")
            .Add("Boolean", true)
            .Add("Uuid", Guid.Empty)
            .Add("Decimal", 3.50m)
            .Add("Float", 2.5f)
            .Add("Double", 3.5d)
            .Add("Timestamp", now)
            .Add("TimestampOffset", offset)
            .Add("Date", new DateOnly(2026, 10, 10))
            .Add("Time", new TimeOnly(12, 30))
            .Add("Binary", new byte[] { 1, 2 })
            .ToArray();

        var types = new[]
        {
            NpgsqlDbType.Bigint,
            NpgsqlDbType.Integer,
            NpgsqlDbType.Smallint,
            NpgsqlDbType.Smallint,
            NpgsqlDbType.Text,
            NpgsqlDbType.Boolean,
            NpgsqlDbType.Uuid,
            NpgsqlDbType.Numeric,
            NpgsqlDbType.Real,
            NpgsqlDbType.Double,
            NpgsqlDbType.TimestampTz,
            NpgsqlDbType.TimestampTz,
            NpgsqlDbType.Date,
            NpgsqlDbType.Time,
            NpgsqlDbType.Bytea
        };

        CollectionAssert.AreEqual(types, parameters.Select(parameter => parameter.NpgsqlDbType).ToArray());
        Assert.AreEqual((short)3, parameters[3].Value);
    }

    [TestMethod]
    public void Create_NullableTypeAndEnum_UseSmallint()
    {
        State? state = State.Finished;
        long? missing = null;

        var parameters = SqlParams.Create()
            .Add("State", state)
            .Add("Missing", missing)
            .ToArray();

        Assert.AreEqual(NpgsqlDbType.Smallint, parameters[0].NpgsqlDbType);
        Assert.AreEqual((short)2, parameters[0].Value);
        Assert.AreEqual(NpgsqlDbType.Bigint, parameters[1].NpgsqlDbType);
        Assert.AreEqual(DBNull.Value, parameters[1].Value);
    }

    [TestMethod]
    public void From_ExplicitTypes_AllowJsonbAndCitext()
    {
        var data = new { Document = "{\"id\":42}", Email = "a@example.com", Missing = (string?)null };

        var parameters = SqlParams.From(data)
            .Add(nameof(data.Document), NpgsqlDbType.Jsonb)
            .Add(nameof(data.Email), NpgsqlDbType.Citext)
            .Add(nameof(data.Missing), NpgsqlDbType.Jsonb)
            .ToArray();

        Assert.AreEqual(NpgsqlDbType.Jsonb, parameters[0].NpgsqlDbType);
        Assert.AreEqual("{\"id\":42}", parameters[0].Value);
        Assert.AreEqual(NpgsqlDbType.Citext, parameters[1].NpgsqlDbType);
        Assert.AreEqual(DBNull.Value, parameters[2].Value);
        Assert.AreEqual(NpgsqlDbType.Jsonb, parameters[2].NpgsqlDbType);
    }

    [TestMethod]
    public void Create_ExplicitArrayType_DoesNotGuessArrays()
    {
        var parameters = SqlParams.Create()
            .Add("Languages", new[] { "de", "en" }, NpgsqlDbType.Array | NpgsqlDbType.Text)
            .ToArray();

        Assert.AreEqual(NpgsqlDbType.Array | NpgsqlDbType.Text, parameters[0].NpgsqlDbType);
    }

    [TestMethod]
    public void Create_IncompatibleExplicitTypes_AreRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => SqlParams.Create().Add("Id", 42L, NpgsqlDbType.Text));

        Assert.ThrowsExactly<ArgumentException>(
            () => SqlParams.Create().Add("Json", 42, NpgsqlDbType.Jsonb));

        Assert.ThrowsExactly<ArgumentException>(
            () => SqlParams.Create().Add("Languages", new[] { "de" }, NpgsqlDbType.Array | NpgsqlDbType.Integer));

        Assert.ThrowsExactly<ArgumentException>(
            () => SqlParams.Create().Add("When", new[] { DateTime.UtcNow }, NpgsqlDbType.Array | NpgsqlDbType.TimestampTz));

        Assert.ThrowsExactly<NotSupportedException>(
            () => SqlParams.Create().Add("Languages", new[] { "de" }));
    }

    [TestMethod]
    public void From_UnknownOrDuplicatedProperty_IsRejected()
    {
        var data = new AccountData(12, "M", State.Pending);

        Assert.ThrowsExactly<ArgumentException>(
            () => SqlParams.From(data).Add("DoesNotExist"));

        Assert.ThrowsExactly<ArgumentException>(
            () => SqlParams.From(data).Add(nameof(data.AccountId)).Add(nameof(data.AccountId)));

        Assert.ThrowsExactly<ArgumentException>(
            () => SqlParams.Create().Add("Email", "one").Add("email", "two"));
    }

    [TestMethod]
    public void Create_UnsupportedAndInvalidNames_AreRejected()
    {
        Assert.ThrowsExactly<NotSupportedException>(
            () => SqlParams.Create().Add("Object", new object()));

        Assert.ThrowsExactly<NotSupportedException>(
            () => SqlParams.Create().Add("State", UnsupportedState.Pending));

        Assert.ThrowsExactly<ArgumentException>(
            () => SqlParams.Create().Add("a; DROP TABLE Accounts", "unsafe"));

        Assert.ThrowsExactly<InvalidOperationException>(
            () => SqlParams.Create().Add("Name"));
    }

    [TestMethod]
    public void Create_UtcTimestamps_AreRequired()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => SqlParams.Create().Add("When", DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Local)));

        Assert.ThrowsExactly<ArgumentException>(
            () => SqlParams.Create().Add("When", DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(2))));
    }

    [TestMethod]
    public void Create_ToArray_ReturnsFreshNpgsqlParameters()
    {
        var builder = SqlParams.Create().Add("AccountId", 7L);

        var first = builder.ToArray();
        var second = builder.ToArray();

        Assert.AreNotSame(first[0], second[0]);
        Assert.AreEqual(first[0].Value, second[0].Value);
    }
}
