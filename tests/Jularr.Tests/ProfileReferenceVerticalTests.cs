using Jularr.Data.User.Profile.V1;
using Jularr.Infrastructure.Sql;
using Jularr.Logic.Profile;
using Jularr.Service.Core;
using Jularr.Service.User.Profile.V1;
using Jularr.Tests.Infrastructure;
using Jularr.Web.Data;
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
        var rename = new RenameProfile(runtime);
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

        await fixture.RunSqlAsync(
            "INSERT INTO \"AccountProfiles\" (\"AccountId\", \"ProfileId\") VALUES (41, 202)");

        var rename = new RenameProfile(new ServiceRuntime(fixture.Database, fixture.NewGate()));

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
        var rename = new RenameProfile(runtime);
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
            () => rename.ExecuteAsync<ProfileDetails>(p, new RenameProfileData("Denied")));

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
        var rename = new RenameProfile(new ServiceRuntime(fixture.Database, gate));

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
        var rename = new RenameProfile(new ServiceRuntime(fixture.Database, fixture.NewGate()));

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
            var setup = new NpgsqlDataSourceBuilder(cs).Build();
            var isolated = new NpgsqlConnectionStringBuilder(cs) { SearchPath = "\"ProfileReference\"" };
            var database = new NpgsqlDataSourceBuilder(isolated.ConnectionString).Build();
            var fixture = new ProfileFixture(setup, database);

            try
            {
                await fixture.RunBootstrapAsync(
                    """
                    BEGIN;
                    CREATE SCHEMA "ProfileReference";
                    CREATE TABLE "ProfileReference"."Accounts" (
                        "Id" bigint PRIMARY KEY,
                        "IsEnabled" boolean NOT NULL,
                        "CredentialRevision" bigint NOT NULL DEFAULT 1
                    );
                    CREATE TABLE "ProfileReference"."Profiles" (
                        "Id" bigint PRIMARY KEY,
                        "PublicId" uuid NOT NULL UNIQUE,
                        "OwnerAccountId" bigint NOT NULL REFERENCES "ProfileReference"."Accounts"("Id"),
                        "DisplayName" text NOT NULL CHECK (length(btrim("DisplayName"))>0),
                        "UpdatedAt" timestamptz NOT NULL DEFAULT now()
                    );
                    CREATE TABLE "ProfileReference"."AccountProfiles" (
                        "AccountId" bigint NOT NULL REFERENCES "ProfileReference"."Accounts"("Id"),
                        "ProfileId" bigint NOT NULL REFERENCES "ProfileReference"."Profiles"("Id"),
                        PRIMARY KEY ("AccountId","ProfileId")
                    );
                    ALTER TABLE "ProfileReference"."Profiles"
                        ADD CONSTRAINT "FK_Profiles_OwnerAccountProfiles" FOREIGN KEY ("OwnerAccountId", "Id")
                        REFERENCES "ProfileReference"."AccountProfiles" ("AccountId", "ProfileId") DEFERRABLE INITIALLY DEFERRED;
                    CREATE TABLE "ProfileReference"."AccountSessions" (
                        "PublicId" uuid PRIMARY KEY,
                        "AccountId" bigint NOT NULL REFERENCES "ProfileReference"."Accounts"("Id"),
                        "ActiveProfileId" bigint,
                        "ExpiresAt" timestamptz NOT NULL,
                        "RevokedAt" timestamptz,
                        "CredentialRevision" bigint NOT NULL DEFAULT 1,
                        "RotationRevision" bigint NOT NULL DEFAULT 1,
                        FOREIGN KEY ("AccountId", "ActiveProfileId") REFERENCES "ProfileReference"."AccountProfiles"("AccountId", "ProfileId")
                    );
                    INSERT INTO "ProfileReference"."Accounts" ("Id", "IsEnabled") VALUES (41, TRUE), (42, TRUE);
                    INSERT INTO "ProfileReference"."Profiles" ("Id", "PublicId", "OwnerAccountId", "DisplayName")
                    VALUES (101, @ProfileAPublicId, 41, 'Profile A'), (202, @ProfileBPublicId, 42, 'Profile B');
                    INSERT INTO "ProfileReference"."AccountProfiles" VALUES (41, 101), (42, 202);
                    INSERT INTO "ProfileReference"."AccountSessions" ("PublicId", "AccountId", "ActiveProfileId", "ExpiresAt")
                    VALUES (@SessionPublicId, 41, 101, now() + interval '1 hour');
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

        internal ProfileGate NewGate() => new(Database, AccountAId, ProfileAId, SessionPublicId);

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

    internal sealed class ProfileGate(NpgsqlDataSource database, long accountId, long profileId, Guid sessionPublicId) : IServiceGate
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

            return Task.FromResult(new ServiceCaller(accountId, profileId, ServiceArea.User, sessionPublicId.ToString("D"), sessionPublicId, 1));
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
