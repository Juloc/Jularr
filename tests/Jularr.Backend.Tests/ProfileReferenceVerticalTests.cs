using Jularr.Data.Common;
using Jularr.Data.Profile;
using Jularr.Data.User.Profile.V1;
using Jularr.Infrastructure.Sql;
using Jularr.Logic.Profile;
using Jularr.Service.Core;
using Jularr.Service.User.Profile.V1;
using Jularr.Tests.Infrastructure;
using Jularr.Data;
using Npgsql;

namespace Jularr.Tests;

[TestClass]
public sealed class ProfileReferenceVerticalTests
{
    [TestMethod]
    public async Task Read_UsesOpaquePublicIdAndOnlyTheActiveProfile()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        var gate = fixture.NewGate();
        var service = new ReadProfileV1(new ServiceRuntime(fixture.Database, gate));

        var own = await service.ExecuteAsync<ProfileDetails>(new ProfileParameters(fixture.ProfileAPublicId));
        Assert.AreEqual(fixture.ProfileAPublicId, own.PublicId);
        Assert.AreEqual("Profile A", own.DisplayName);

        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(
            () => service.ExecuteAsync<ProfileDetails>(new ProfileParameters(fixture.ProfileBPublicId)));

        Assert.IsFalse(gate.Events.Contains("transaction"));
        CollectionAssert.AreEqual(
            new[] { "modules", "caller", "access", "modules", "caller", "access" },
            gate.Events.ToArray());
    }

    [TestMethod]
    public async Task Update_LoadSetDataWrite_CommitsAndReturnsSuccess()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        var gate = fixture.NewGate();
        var runtime = new ServiceRuntime(fixture.Database, gate);
        var rename = new UpdateProfileV1(runtime);
        runtime.ValidateAll([rename]);

        var changed = await rename.ExecuteAsync<UpdateResult>(
            new ProfileParameters(fixture.ProfileAPublicId),
            new UpdateProfileDataV1("  Display Name Updated  "));

        Assert.IsTrue(changed.Success);
        var read = new ReadProfileV1(runtime);
        var reloaded = await read.ExecuteAsync<ProfileDetails>(new ProfileParameters(fixture.ProfileAPublicId));
        Assert.AreEqual("Display Name Updated", reloaded.DisplayName);
        Assert.AreEqual("Display Name Updated", await fixture.NameAsync(fixture.ProfileAId));
        Assert.AreEqual("Profile B", await fixture.NameAsync(fixture.ProfileBId));
        CollectionAssert.AreEqual(new[] { "modules", "caller", "access", "transaction", "modules", "caller", "access" }, gate.Events.ToArray());
    }

    [TestMethod]
    public async Task Rename_RejectsAnotherProfilesUuidEvenWhenMembershipExists()
    {
        await using var fixture = await ProfileFixture.CreateAsync();

        await fixture.RunSqlAsync(
            "INSERT INTO \"AccountProfiles\" (\"AccountId\", \"ProfileId\") VALUES (41, 202)");

        var rename = new UpdateProfileV1(new ServiceRuntime(fixture.Database, fixture.NewGate()));

        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(
            () => rename.ExecuteAsync<UpdateResult>(
                new ProfileParameters(fixture.ProfileBPublicId),
                new UpdateProfileDataV1("Should Not Happen")));

        Assert.AreEqual("Profile B", await fixture.NameAsync(fixture.ProfileBId));
    }

    [TestMethod]
    public async Task MembershipRevocationAndDisabledAccountBothFailClosed()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        var gate = fixture.NewGate();
        var runtime = new ServiceRuntime(fixture.Database, gate);
        var read = new ReadProfileV1(runtime);
        var rename = new UpdateProfileV1(runtime);
        var p = new ProfileParameters(fixture.ProfileAPublicId);

        await fixture.RunSqlAsync(
            """
            BEGIN;
            INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (42, 101);
            UPDATE "Profiles" SET "OwnerAccountId" = 42 WHERE "Id" = 101;
            UPDATE "AccountSessions" SET "ActiveProfileId" = NULL;
            DELETE FROM "AccountProfiles" WHERE "AccountId" = 41 AND "ProfileId" = 101;
            COMMIT;
            """);

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => read.ExecuteAsync<ProfileDetails>(p));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
            () => rename.ExecuteAsync<UpdateResult>(p, new UpdateProfileDataV1("Denied")));

        await fixture.RunSqlAsync(
            "INSERT INTO \"AccountProfiles\" (\"AccountId\", \"ProfileId\") VALUES (41, 101)");
        await fixture.RunSqlAsync("UPDATE \"AccountSessions\" SET \"ActiveProfileId\" = 101");
        await fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"IsEnabled\" = FALSE WHERE \"Id\" = 41");

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => read.ExecuteAsync<ProfileDetails>(p));
        Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));
    }

    [TestMethod]
    public async Task TransactionalGateDenialPreventsAnyMutation()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        var gate = fixture.NewGate();
        gate.DenyTransactionalWrite = true;
        var rename = new UpdateProfileV1(new ServiceRuntime(fixture.Database, gate));

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
            () => rename.ExecuteAsync<UpdateResult>(
                new ProfileParameters(fixture.ProfileAPublicId),
                new UpdateProfileDataV1("Denied")));

        Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));
        CollectionAssert.AreEqual(new[] { "modules", "caller", "access", "transaction" }, gate.Events.ToArray());
    }

    [TestMethod]
    public async Task InvalidNameRollsBackAndParameterBindingDoesNotInterpretSql()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        var rename = new UpdateProfileV1(new ServiceRuntime(fixture.Database, fixture.NewGate()));

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => rename.ExecuteAsync<UpdateResult>(
                new ProfileParameters(fixture.ProfileAPublicId),
                new UpdateProfileDataV1("   ")));
        Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));

        const string literal = "Safe'); DROP TABLE \"Profiles\"; --";
        var changed = await rename.ExecuteAsync<UpdateResult>(
            new ProfileParameters(fixture.ProfileAPublicId),
            new UpdateProfileDataV1(literal));

        Assert.IsTrue(changed.Success);
        Assert.AreEqual(literal, await fixture.NameAsync(fixture.ProfileAId));
        Assert.AreEqual("Profile B", await fixture.NameAsync(fixture.ProfileBId));
    }

    [TestMethod]
    public async Task EmptyPublicIdRejectedBeforeDatabaseAccess()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        var gate = fixture.NewGate();
        var read = new ReadProfileV1(new ServiceRuntime(fixture.Database, gate));

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => read.ExecuteAsync<ProfileDetails>(new ProfileParameters(Guid.Empty)));

        CollectionAssert.AreEqual(new[] { "modules" }, gate.Events.ToArray());
    }

    [TestMethod]
    public async Task Update_LateFailure_RollsBackSharedLogicTransaction()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        var service = new FailingUpdate(new ServiceRuntime(fixture.Database, fixture.NewGate()));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteAsync<UpdateResult>(
            new ProfileParameters(fixture.ProfileAPublicId), new UpdateProfileDataV1("Pending")));

        Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));
    }

    private sealed class FailingUpdate(ServiceRuntime runtime) : UserService<ProfileParameters, UpdateProfileDataV1>(runtime)
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ProfileParameters parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ProfileParameters parameters, Type resultType) => new("profile.self.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var updateData = await Context.ReadProfileForUpdateAsync(Parameters.PublicId, CancellationToken);
            updateData.SetData(new ProfileNameChange(Parameters.PublicId, Data.DisplayName));
            await Context.UpdateProfileAsync(updateData, CancellationToken);
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."PublicId",
                    profile."DisplayName"
                FROM
                    "Profiles" AS profile
                WHERE
                    profile."PublicId" = @PublicId;
                """;
            sql.Parameters.Add(Parameters);
            var pending = await Context.ReadRequiredAsync<ProfileDetails>(sql, CancellationToken);
            Assert.AreEqual("Pending", pending.DisplayName);
            throw new InvalidOperationException("Fail before commit.");
        }
    }

    internal sealed class ProfileFixture : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _setup;

        public NpgsqlDataSource Database { get; }
        public long AccountAId { get; } = 41;
        public long ProfileAId { get; } = 101;
        public long ProfileBId { get; } = 202;
        public Guid ProfileAPublicId { get; } = Guid.NewGuid();
        public Guid ProfileBPublicId { get; } = Guid.NewGuid();
        public Guid SessionPublicId { get; } = Guid.NewGuid();

        private ProfileFixture(NpgsqlDataSource setup, NpgsqlDataSource database)
        {
            _setup = setup;
            Database = database;
        }

        public static async Task<ProfileFixture> CreateAsync()
        {
            var cs = TestPostgres.ResolveConnectionString($"Data Source=profile-vertical-{Guid.NewGuid():N}");
            var isolated = new NpgsqlConnectionStringBuilder(cs) { SearchPath = "\"ProfileReference\"" };
            var setup = new NpgsqlDataSourceBuilder(isolated.ConnectionString).Build();
            var database = new NpgsqlDataSourceBuilder(isolated.ConnectionString).Build();
            var fixture = new ProfileFixture(setup, database);

            try
            {
                await fixture.RunBootstrapAsync("CREATE SCHEMA \"ProfileReference\";");
                using var schema = typeof(ReadProfileV1).Assembly.GetManifestResourceStream("Jularr.Database.Profile.sql")
                    ?? throw new InvalidOperationException("The profile schema is missing.");
                using var reader = new StreamReader(schema);
                await fixture.RunBootstrapAsync(await reader.ReadToEndAsync());
                await fixture.RunBootstrapAsync(
                    """
                    BEGIN;
                    INSERT INTO "UiLocales" ("Id", "Locale", "Name") VALUES (1, 'de-DE', 'Deutsch');
                    INSERT INTO "Accounts" ("Id", "Email", "DisplayName", "AccountRoleTypeId")
                    VALUES (41, 'a@example.test', 'Account A', 2), (42, 'b@example.test', 'Account B', 2);
                    INSERT INTO "ProfileReference"."Profiles" ("Id", "PublicId", "OwnerAccountId", "DisplayName", "UiLocaleId")
                    VALUES (101, @ProfileAPublicId, 41, 'Profile A', 1), (202, @ProfileBPublicId, 42, 'Profile B', 1);
                    INSERT INTO "ProfileReference"."AccountProfiles" ("AccountId", "ProfileId") VALUES (41, 101), (42, 202);
                    INSERT INTO "ProfileReference"."AccountSessions" ("PublicId", "AccountId", "ActiveProfileId", "TokenHash", "ExpiresAt")
                    VALUES (@SessionPublicId, 41, 101, decode(repeat('ab', 32), 'hex'), now() + interval '1 hour');
                    COMMIT;
                    """,
                    SqlParams.Create().Add("ProfileAPublicId", fixture.ProfileAPublicId).Add("ProfileBPublicId", fixture.ProfileBPublicId)
                        .Add("SessionPublicId", fixture.SessionPublicId).ToArray());
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal ProfileGate NewGate() => new(Database, AccountAId, ProfileAId, ProfileAPublicId, SessionPublicId);

        public async Task<string> NameAsync(long profileId)
        {
            await using var conn = await Database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT \"DisplayName\" FROM \"Profiles\" WHERE \"Id\"=@id", conn);
            command.Parameters.AddRange(SqlParams.Create().Add("id", profileId).ToArray());
            return (string)(await command.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("Missing profile row."));
        }

        public async Task RunSqlAsync(string sql)
        {
            await using var conn = await Database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, conn);
            await command.ExecuteNonQueryAsync();
        }

        private async Task RunBootstrapAsync(string sql, NpgsqlParameter[]? parameters = null)
        {
            await using var conn = await _setup.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, conn);
            if (parameters is not null)
            {
                command.Parameters.AddRange(parameters);
            }
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            try
            {
                await RunBootstrapAsync("DROP SCHEMA IF EXISTS \"ProfileReference\" CASCADE");
            }
            finally
            {
                await _setup.DisposeAsync();
            }
        }
    }

    private sealed record LockedProfile(long Id);

    internal sealed class ProfileGate(NpgsqlDataSource database, long accountId, long profileId, Guid profilePublicId, Guid sessionPublicId) : IServiceGate
    {
        public List<string> Events { get; } = [];
        public bool DenyTransactionalWrite { get; set; }

        public Task RequireModulesAsync(ModuleRequirement modules, CancellationToken cancellationToken)
        {
            Events.Add("modules");
            if (modules.Mode != ModuleRequirementMode.None)
            {
                throw new UnauthorizedAccessException("Unexpected module requirement.");
            }

            return Task.CompletedTask;
        }

        public Task<ServiceCaller> RequireCallerAsync(ServiceArea area, CancellationToken cancellationToken)
        {
            Events.Add("caller");
            if (area != ServiceArea.User)
            {
                throw new UnauthorizedAccessException("Expected user service.");
            }

            return Task.FromResult(new ServiceCaller(accountId, profileId, ServiceArea.User, sessionPublicId.ToString("D"), sessionPublicId, 1, profilePublicId, accountId));
        }

        public async Task RequireAccessAsync(
            ServiceCaller caller,
            ServicePermission permission,
            IReadOnlyList<ResourceTarget> targets,
            CancellationToken cancellationToken)
        {
            Events.Add("access");
            if (targets.Count != 0 ||
                (permission.Key != "profile.self.read" && permission.Key != "profile.self.update"))
            {
                throw new UnauthorizedAccessException("Invalid permission or target.");
            }

            await using var conn = await database.OpenConnectionAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM "AccountProfiles" AS membership
                    JOIN "Accounts" AS account ON account."Id"=membership."AccountId"
                    WHERE membership."AccountId"=@account
                      AND membership."ProfileId"=@profile
                      AND account."IsEnabled"=true
                )
                """, conn);
            cmd.Parameters.AddWithValue("account", caller.AccountId!.Value);
            cmd.Parameters.AddWithValue("profile", caller.ProfileId!.Value);

            if (await cmd.ExecuteScalarAsync(cancellationToken) is not true)
            {
                throw new UnauthorizedAccessException("Account/Profile access denied.");
            }
        }

        public async Task RequireTransactionalAccessAsync(
            ServiceCaller caller,
            ServicePermission permission,
            IReadOnlyList<ResourceTarget> targets,
            LogicContext context,
            CancellationToken cancellationToken)
        {
            Events.Add("transaction");
            if (DenyTransactionalWrite || permission.Key != "profile.self.update")
            {
                throw new UnauthorizedAccessException("Profile mutation is not permitted.");
            }

            await context.Sql.ReadForUpdateAsync<LockedProfile>(
                """
                SELECT profile."Id"
                FROM "Profiles" AS profile
                JOIN "AccountProfiles" AS membership
                  ON membership."ProfileId"=profile."Id"
                JOIN "Accounts" AS account
                  ON account."Id"=membership."AccountId"
                WHERE profile."Id"=@ActiveProfileId
                  AND membership."AccountId"=@ActorAccountId
                  AND account."IsEnabled"=true
                FOR UPDATE OF profile, membership, account
                """,
                new object(),
                cancellationToken);
        }
    }
}
