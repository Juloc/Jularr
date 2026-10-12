using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Common;
using System.Text.Json;
using Jularr.Backend.Data.Fields;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Tests.Infrastructure;
using Jularr.Backend.Data;
using Npgsql;
using NpgsqlTypes;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DataFieldTests
    {
        private sealed class ProfileListChange : BaseData
        {
            public ProfileListChange(long id, string displayName)
            {
                Id = id;
                DisplayName = displayName;
            }

            [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
            public long Id { get; }

            [DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))]
            public string DisplayName { get; }
        }

        [TestMethod]
        public void SetData_PartialFields_DoNotRenameProfileIdToId()
        {
            var target = new ProfileUpdateData(101, "Name", 1);
            target.SetData(new UpdateProfileParametersV1(999), new UpdateProfileDataV1 { UiLocaleId = 2 });
            Assert.AreEqual(101L, target.Id);
            Assert.AreEqual("Name", target.DisplayName);
            Assert.AreEqual(2L, target.UiLocaleId);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var json = JsonSerializer.Serialize(new UpdateProfileDataV1 { UiLocaleId = 3 }, options);
            Assert.AreEqual("{\"uiLocaleId\":3}", json);
            target.SetData(JsonSerializer.Deserialize<UpdateProfileDataV1>(json, options)!);
            Assert.AreEqual("Name", target.DisplayName);
            target.SetData(new UpdateProfileDataV1("  Changed  "), new UpdateProfileDataV1 { UiLocaleId = 4 });
            Assert.AreEqual("Changed", target.DisplayName);
            Assert.AreEqual(4L, target.UiLocaleId);
            Assert.ThrowsExactly<DataValidationException>(() => target.SetData(
                JsonSerializer.Deserialize<UpdateProfileDataV1>("{\"displayName\":null}", options)!));
            Assert.AreEqual("Changed", target.DisplayName);
        }

        [TestMethod]
        public void SetData_Lists_MatchEntityIdsAndRejectUnknownRows()
        {
            var first = new ProfileUpdateData(101, "A", 1);
            var second = new ProfileUpdateData(202, "B", 2);
            var target = new BaseDataList<ProfileUpdateData>([first, second]);
            target.SetData(new BaseDataList<ProfileListChange>([new(202, "B changed"), new(101, "A changed")]));
            Assert.AreEqual("A changed", first.DisplayName);
            Assert.AreEqual("B changed", second.DisplayName);
            Assert.AreEqual(2L, second.UiLocaleId);
            Assert.ThrowsExactly<DataValidationException>(() => target.SetData(
                new BaseDataList<ProfileListChange>([new(101, "Not applied"), new(999, "Unknown")])));
            Assert.AreEqual("A changed", first.DisplayName);
        }

        public static readonly StringField ShortLabel = new() { MaximumLength = 3, AllowBlank = false };

        private sealed class LabelData : BaseData
        {
            public LabelData(string label)
            {
                Label = label;
            }

            [DataField(typeof(DataFieldTests), nameof(ShortLabel))]
            public string Label { get; }
        }

        [TestMethod]
        public void Data_RequiredLengthAndList_ValidateWithoutSql()
        {
            new LabelData("😀😀😀").Validate();
            var list = JsonSerializer.Deserialize<BaseDataList<LabelData>>("[{\"Label\":\"ok\"}]")!;
            list.Validate();
            Assert.AreEqual("ok", list[0].Label);
            Assert.AreEqual("[{\"Label\":\"ok\"}]", JsonSerializer.Serialize(list));
            Assert.ThrowsExactly<DataValidationException>(() => new LabelData("1234").Validate());
            Assert.ThrowsExactly<DataValidationException>(() => new LabelData("\0").Validate());
            Assert.ThrowsExactly<DataValidationException>(() => new LabelData(" ").Validate());
            Assert.ThrowsExactly<DataValidationException>(() => new BaseDataList<LabelData>([new("ok"), new("long")]).Validate());
            Assert.ThrowsExactly<DataValidationException>(() => ProfileFields.UiLocaleId.Validate(0L, "UiLocaleId"));

            // Nullability and the all-zero Guid are separate field rules.
            Assert.ThrowsExactly<DataValidationException>(() => AccountSessionFields.PublicId.Validate(null, "SessionPublicId"));
            Assert.ThrowsExactly<DataValidationException>(() => AccountSessionFields.PublicId.Validate(Guid.Empty, "SessionPublicId"));
            AccountPasskeyFields.Aaguid.Validate(null, "Aaguid");
            AccountPasskeyFields.Aaguid.Validate(Guid.Empty, "Aaguid");
        }

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
            Assert.AreEqual(typeof(string), ProfileFields.DisplayName.ClrType);
            Assert.ThrowsExactly<DataValidationException>(() => SqlParams.Create().Add(new RequiredName(null)));

            var parameter = SqlParams.Create().Add(new OptionalName(null)).ToArray().Single();
            Assert.AreEqual(NpgsqlDbType.Text, parameter.NpgsqlDbType);
            Assert.AreEqual(DBNull.Value, parameter.Value);

            var id = SqlParams.Create().Add(new OptionalId(null)).ToArray().Single();
            Assert.AreEqual(NpgsqlDbType.Bigint, id.NpgsqlDbType);
            Assert.AreEqual(DBNull.Value, id.Value);
        }

        [TestMethod]
        public void Field_ConflictingClrType_IsRejected()
        {
            Assert.ThrowsExactly<ArgumentException>(() => SqlParams.Create().Add(new InvalidName(1)));
        }

        [TestMethod]
        public void PageResponse_InvalidItem_UsesCanonicalFieldValidation()
        {
            var result = new PageResult<ReadProfileResultV1>([new ReadProfileResultV1(0, "Name", 1)], new PageRequest());

            Assert.ThrowsExactly<DataValidationException>(result.Validate);
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

            var value = await sql.ReadSql.ReadRequiredAsync<RequiredName>(new SqlStatement { Statement = statement, Parameters = { new RequiredName("O'Brien") } });
            Assert.AreEqual("O'Brien", value.DisplayName);
            var optional = await sql.ReadSql.ReadRequiredAsync<OptionalName>(new SqlStatement { Statement = statement, Parameters = { new OptionalName(null) } });
            Assert.IsNull(optional.DisplayName);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sql.ReadSql.ReadRequiredAsync<RequiredName>(new SqlStatement { Statement = statement, Parameters = { new OptionalName(null) } }));
        }
    }
}
