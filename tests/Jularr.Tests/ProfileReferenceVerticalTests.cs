using Jularr.Data.User.Profile.V1;
using Jularr.Infrastructure.Sql;
using Jularr.Logic.Profile;
using Jularr.Service.Core;
using Jularr.Service.User.Profile.V1;
using Jularr.Tests.Infrastructure;
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
        var service = new GetProfile(new ServiceRuntime(fixture.Database, gate));

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
    public async Task Rename_OnlyLogicMutatesAndReadSeesSameCommittedTransaction()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        var gate = fixture.NewGate();
        var runtime = new ServiceRuntime(fixture.Database, gate);
        var rename = new RenameProfile(runtime, new ProfileLogic());
        runtime.ValidateAll([rename]);

        var changed = await rename.ExecuteAsync<ProfileDetails>(
            new ProfileParameters(fixture.ProfileAPublicId),
            new RenameProfileData("  Display Name Updated  "));

        Assert.AreEqual(fixture.ProfileAPublicId, changed.PublicId);
        Assert.AreEqual("Display Name Updated", changed.DisplayName);
        Assert.AreEqual("Display Name Updated", await fixture.NameAsync(fixture.ProfileAId));
        Assert.AreEqual("Profile B", await fixture.NameAsync(fixture.ProfileBId));
        CollectionAssert.AreEqual(new[] { "modules", "caller", "access", "transaction" }, gate.Events.ToArray());
    }

    [TestMethod]
    public async Task Rename_RejectsAnotherProfilesUuidEvenWhenMembershipExists()
    {
        await using var fixture = await ProfileFixture.CreateAsync();

        // A may also be a member of B, but the current active Profile remains A.
        await fixture.RunSqlAsync(
            $"INSERT INTO \"AccountProfiles\" (\"AccountId\",\"ProfileId\") VALUES ({fixture.AccountAId},{fixture.ProfileBId})");

        var rename = new RenameProfile(new ServiceRuntime(fixture.Database, fixture.NewGate()), new ProfileLogic());

        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(
            () => rename.ExecuteAsync<ProfileDetails>(
                new ProfileParameters(fixture.ProfileBPublicId),
                new RenameProfileData("Should Not Happen")));

        Assert.AreEqual("Profile B", await fixture.NameAsync(fixture.ProfileBId));
    }

    [TestMethod]
    public async Task MembershipRevocationAndDisabledAccountBothFailClosed()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        var gate = fixture.NewGate();
        var runtime = new ServiceRuntime(fixture.Database, gate);
        var read = new GetProfile(runtime);
        var rename = new RenameProfile(runtime, new ProfileLogic());
        var p = new ProfileParameters(fixture.ProfileAPublicId);

        await fixture.RunSqlAsync(
            $"DELETE FROM \"AccountProfiles\" WHERE \"AccountId\"={fixture.AccountAId} AND \"ProfileId\"={fixture.ProfileAId}");

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => read.ExecuteAsync<ProfileDetails>(p));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
            () => rename.ExecuteAsync<ProfileDetails>(p, new RenameProfileData("Denied")));

        await fixture.RunSqlAsync(
            $"INSERT INTO \"AccountProfiles\" (\"AccountId\",\"ProfileId\") VALUES ({fixture.AccountAId},{fixture.ProfileAId})");
        await fixture.RunSqlAsync($"UPDATE \"Accounts\" SET \"IsEnabled\"=false WHERE \"Id\"={fixture.AccountAId}");

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => read.ExecuteAsync<ProfileDetails>(p));
        Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));
    }

    [TestMethod]
    public async Task TransactionalGateDenialPreventsAnyMutation()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        var gate = fixture.NewGate();
        gate.DenyTransactionalWrite = true;
        var rename = new RenameProfile(new ServiceRuntime(fixture.Database, gate), new ProfileLogic());

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
            () => rename.ExecuteAsync<ProfileDetails>(
                new ProfileParameters(fixture.ProfileAPublicId),
                new RenameProfileData("Denied")));

        Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));
        CollectionAssert.AreEqual(new[] { "modules", "caller", "access", "transaction" }, gate.Events.ToArray());
    }

    [TestMethod]
    public async Task InvalidNameRollsBackAndParameterBindingDoesNotInterpretSql()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        var rename = new RenameProfile(
            new ServiceRuntime(fixture.Database, fixture.NewGate()),
            new ProfileLogic());

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => rename.ExecuteAsync<ProfileDetails>(
                new ProfileParameters(fixture.ProfileAPublicId),
                new RenameProfileData("   ")));
        Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));

        const string literal = "Safe'); DROP TABLE \"Profiles\"; --";
        var changed = await rename.ExecuteAsync<ProfileDetails>(
            new ProfileParameters(fixture.ProfileAPublicId),
            new RenameProfileData(literal));

        Assert.AreEqual(literal, changed.DisplayName);
        Assert.AreEqual(literal, await fixture.NameAsync(fixture.ProfileAId));
        Assert.AreEqual("Profile B", await fixture.NameAsync(fixture.ProfileBId));
    }

    [TestMethod]
    public async Task EmptyPublicIdRejectedBeforeDatabaseAccess()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        var gate = fixture.NewGate();
        var read = new GetProfile(new ServiceRuntime(fixture.Database, gate));

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => read.ExecuteAsync<ProfileDetails>(new ProfileParameters(Guid.Empty)));

        CollectionAssert.AreEqual(new[] { "modules" }, gate.Events.ToArray());
    }

    // Strict integration fixture: no AllowAll gate, no production wiring and
    // no changes to dev's live database. Each test uses a unique PG schema.
    private sealed class ProfileFixture : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _setup;
        private readonly string _schema;

        public NpgsqlDataSource Database { get; }
        public long AccountAId { get; } = 41;
        public long ProfileAId { get; } = 101;
        public long ProfileBId { get; } = 202;
        public Guid ProfileAPublicId { get; } = Guid.NewGuid();
        public Guid ProfileBPublicId { get; } = Guid.NewGuid();

        private ProfileFixture(NpgsqlDataSource setup, NpgsqlDataSource database, string schema)
        {
            _setup = setup;
            Database = database;
            _schema = schema;
        }

        public static async Task<ProfileFixture> CreateAsync()
        {
            var cs = TestPostgres.ResolveConnectionString($"Data Source=profile-vertical-{Guid.NewGuid():N}");
            var setup = new NpgsqlDataSourceBuilder(cs).Build();
            var schema = $"profile_reference_{Guid.NewGuid():N}";
            var isolated = new NpgsqlConnectionStringBuilder(cs) { SearchPath = schema };
            var database = new NpgsqlDataSourceBuilder(isolated.ConnectionString).Build();
            var fixture = new ProfileFixture(setup, database, schema);

            try
            {
                await fixture.RunBootstrapAsync(
                    $"""
                    CREATE SCHEMA "{schema}";
                    CREATE TABLE "{schema}"."Accounts" (
                        "Id" bigint PRIMARY KEY,
                        "IsEnabled" boolean NOT NULL
                    );
                    CREATE TABLE "{schema}"."Profiles" (
                        "Id" bigint PRIMARY KEY,
                        "PublicId" uuid NOT NULL UNIQUE,
                        "DisplayName" text NOT NULL CHECK (length(btrim("DisplayName"))>0),
                        "UpdatedAt" timestamptz NOT NULL DEFAULT now()
                    );
                    CREATE TABLE "{schema}"."AccountProfiles" (
                        "AccountId" bigint NOT NULL REFERENCES "{schema}"."Accounts"("Id"),
                        "ProfileId" bigint NOT NULL REFERENCES "{schema}"."Profiles"("Id"),
                        PRIMARY KEY ("AccountId","ProfileId")
                    );
                    INSERT INTO "{schema}"."Accounts" VALUES (41,true),(42,true);
                    INSERT INTO "{schema}"."Profiles" ("Id","PublicId","DisplayName")
                    VALUES (101,'{fixture.ProfileAPublicId}','Profile A'),
                           (202,'{fixture.ProfileBPublicId}','Profile B');
                    INSERT INTO "{schema}"."AccountProfiles" VALUES (41,101),(42,202);
                    """);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public ProfileGate NewGate() => new(Database, AccountAId, ProfileAId);

        public async Task<string> NameAsync(long profileId)
        {
            await using var conn = await Database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT \"DisplayName\" FROM \"Profiles\" WHERE \"Id\"=@id", conn);
            command.Parameters.AddWithValue("id", profileId);
            return (string)(await command.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("Missing profile row."));
        }

        public async Task RunSqlAsync(string sql)
        {
            await using var conn = await Database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, conn);
            await command.ExecuteNonQueryAsync();
        }

        private async Task RunBootstrapAsync(string sql)
        {
            await using var conn = await _setup.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, conn);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            try
            {
                await RunBootstrapAsync($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE");
            }
            finally
            {
                await _setup.DisposeAsync();
            }
        }
    }

    private sealed record LockedProfile(long Id);

    private sealed class ProfileGate(NpgsqlDataSource database, long accountId, long profileId) : IServiceGate
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

            return Task.FromResult(new ServiceCaller(accountId, profileId, ServiceArea.User, "isolated-test-user"));
        }

        public async Task RequireAccessAsync(
            ServiceCaller caller,
            ServicePermission permission,
            IReadOnlyList<ResourceTarget> targets,
            CancellationToken cancellationToken)
        {
            Events.Add("access");
            if (targets.Count != 0 ||
                (permission.Key != "profile.self.read" && permission.Key != "profile.self.rename"))
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
            if (DenyTransactionalWrite || permission.Key != "profile.self.rename")
            {
                throw new UnauthorizedAccessException("Profile mutation is not permitted.");
            }

            // Recheck and lock the actual authorized Account/Profile membership
            // INSIDE the service's shared write transaction.
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
