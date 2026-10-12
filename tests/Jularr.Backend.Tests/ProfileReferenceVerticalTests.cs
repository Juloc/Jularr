using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Profile;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Profile.V1;
using Jularr.Backend.Tests.Infrastructure;
using Jularr.Backend.Data;
using Npgsql;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ProfileReferenceVerticalTests
    {
        [TestMethod]
        public async Task Read_UsesLongIdAndOnlyTheActiveProfile()
        {
            await using var fixture = await ProfileFixture.CreateAsync();
            var gate = fixture.NewGate();
            var service = new ServiceRuntime(fixture.Database, gate).Create<ReadProfileV1>();

            var own = await service.ExecuteAsync<ReadProfileResultV1>(new ReadProfileParametersV1(fixture.ProfileAId));
            Assert.AreEqual(fixture.ProfileAId, own.Id);
            Assert.AreEqual("Profile A", own.DisplayName);

            await Assert.ThrowsExactlyAsync<KeyNotFoundException>(
                () => service.ExecuteAsync<ReadProfileResultV1>(new ReadProfileParametersV1(fixture.ProfileBId)));

            Assert.IsFalse(gate.Events.Contains("transaction"));
            CollectionAssert.AreEqual(
                new[] { "caller", "access", "modules", "caller", "access", "modules" },
                gate.Events.ToArray());
        }

        [TestMethod]
        public async Task Update_LoadSetDataWrite_CommitsAndReturnsSuccess()
        {
            await using var fixture = await ProfileFixture.CreateAsync();
            var gate = fixture.NewGate();
            var runtime = new ServiceRuntime(fixture.Database, gate);
            var rename = runtime.Create<UpdateProfileV1>();
            runtime.ValidateAll([rename]);

            var changed = await rename.ExecuteAsync<UpdateResult>(
                new UpdateProfileParametersV1(fixture.ProfileAId),
                new UpdateProfileDataV1("  Display Name Updated  "));

            Assert.IsTrue(changed.Success);
            var read = runtime.Create<ReadProfileV1>();
            var reloaded = await read.ExecuteAsync<ReadProfileResultV1>(new ReadProfileParametersV1(fixture.ProfileAId));
            Assert.AreEqual("Display Name Updated", reloaded.DisplayName);
            Assert.AreEqual("Display Name Updated", await fixture.NameAsync(fixture.ProfileAId));
            Assert.AreEqual("Profile B", await fixture.NameAsync(fixture.ProfileBId));
            CollectionAssert.AreEqual(new[] { "caller", "access", "transaction", "modules", "caller", "access", "modules" }, gate.Events.ToArray());
        }

        [TestMethod]
        public async Task Update_ForeignProfileId_IsDeniedEvenWithMembership()
        {
            await using var fixture = await ProfileFixture.CreateAsync();

            await fixture.RunSqlAsync(
                "INSERT INTO \"AccountProfiles\" (\"AccountId\", \"ProfileId\") VALUES (41, 202)");

            var rename = new ServiceRuntime(fixture.Database, fixture.NewGate()).Create<UpdateProfileV1>();

            await Assert.ThrowsExactlyAsync<KeyNotFoundException>(
                () => rename.ExecuteAsync<UpdateResult>(
                    new UpdateProfileParametersV1(fixture.ProfileBId),
                    new UpdateProfileDataV1("Should Not Happen")));

            Assert.AreEqual("Profile B", await fixture.NameAsync(fixture.ProfileBId));
        }

        [TestMethod]
        public async Task MembershipRevocationAndDisabledAccountBothFailClosed()
        {
            await using var fixture = await ProfileFixture.CreateAsync();
            var gate = fixture.NewGate();
            var runtime = new ServiceRuntime(fixture.Database, gate);
            var read = runtime.Create<ReadProfileV1>();
            var rename = runtime.Create<UpdateProfileV1>();
            var p = new ReadProfileParametersV1(fixture.ProfileAId);

            await fixture.RunSqlAsync(
                """
                BEGIN;
                INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (42, 101);
                UPDATE "Profiles" SET "OwnerAccountId" = 42 WHERE "Id" = 101;
                UPDATE "AccountSessions" SET "ActiveProfileId" = NULL;
                DELETE FROM "AccountProfiles" WHERE "AccountId" = 41 AND "ProfileId" = 101;
                COMMIT;
                """);

            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => read.ExecuteAsync<ReadProfileResultV1>(p));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
                () => rename.ExecuteAsync<UpdateResult>(new UpdateProfileParametersV1(p.ProfileId), new UpdateProfileDataV1("Denied")));

            await fixture.RunSqlAsync(
                "INSERT INTO \"AccountProfiles\" (\"AccountId\", \"ProfileId\") VALUES (41, 101)");
            await fixture.RunSqlAsync("UPDATE \"AccountSessions\" SET \"ActiveProfileId\" = 101");
            await fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"IsEnabled\" = FALSE WHERE \"Id\" = 41");

            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => read.ExecuteAsync<ReadProfileResultV1>(p));
            Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));
        }

        [TestMethod]
        public async Task TransactionalGateDenialPreventsAnyMutation()
        {
            await using var fixture = await ProfileFixture.CreateAsync();
            var gate = fixture.NewGate();
            gate.DenyTransactionalWrite = true;
            var rename = new ServiceRuntime(fixture.Database, gate).Create<UpdateProfileV1>();

            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
                () => rename.ExecuteAsync<UpdateResult>(
                    new UpdateProfileParametersV1(fixture.ProfileAId),
                    new UpdateProfileDataV1("Denied")));

            Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));
            CollectionAssert.AreEqual(new[] { "caller", "access", "transaction" }, gate.Events.ToArray());
        }

        [TestMethod]
        public async Task InvalidNameRollsBackAndParameterBindingDoesNotInterpretSql()
        {
            await using var fixture = await ProfileFixture.CreateAsync();
            var rename = new ServiceRuntime(fixture.Database, fixture.NewGate()).Create<UpdateProfileV1>();

            await Assert.ThrowsExactlyAsync<DataValidationException>(
                () => rename.ExecuteAsync<UpdateResult>(
                    new UpdateProfileParametersV1(fixture.ProfileAId),
                    new UpdateProfileDataV1("   ")));
            Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));

            const string literal = "Safe'); DROP TABLE \"Profiles\"; --";
            var changed = await rename.ExecuteAsync<UpdateResult>(
                new UpdateProfileParametersV1(fixture.ProfileAId),
                new UpdateProfileDataV1(literal));

            Assert.IsTrue(changed.Success);
            Assert.AreEqual(literal, await fixture.NameAsync(fixture.ProfileAId));
            Assert.AreEqual("Profile B", await fixture.NameAsync(fixture.ProfileBId));
        }

        [TestMethod]
        public async Task InvalidProfileIdRejectedBeforeDatabaseAccess()
        {
            await using var fixture = await ProfileFixture.CreateAsync();
            var gate = fixture.NewGate();
            var read = new ServiceRuntime(fixture.Database, gate).Create<ReadProfileV1>();

            await Assert.ThrowsExactlyAsync<DataValidationException>(
                () => read.ExecuteAsync<ReadProfileResultV1>(new ReadProfileParametersV1(0)));

            Assert.AreEqual(0, gate.Events.Count);
        }

        [TestMethod]
        public async Task Update_LateFailure_RollsBackSharedLogicTransaction()
        {
            await using var fixture = await ProfileFixture.CreateAsync();
            var service = new ServiceRuntime(fixture.Database, fixture.NewGate()).Create(new FailingUpdate());

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteAsync<UpdateResult>(
                new UpdateProfileParametersV1(fixture.ProfileAId), new UpdateProfileDataV1("Pending")));

            Assert.AreEqual("Profile A", await fixture.NameAsync(fixture.ProfileAId));
        }

        private sealed class FailingUpdate : UserService<UpdateProfileParametersV1, UpdateProfileDataV1>
        {
            protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
            protected override ModuleRequirement GetInstanceModules(UpdateProfileParametersV1 parameters) => ModuleRequirement.None;
            protected override ServicePermission GetPermission(UpdateProfileParametersV1 parameters, Type resultType) => new("profile.self.update");

            protected override async Task<IServiceOutput> OnUpdate()
            {
                var updateData = await Context.ReadProfileAsync(Parameters.ProfileId, CancellationToken);
                updateData.SetData(Data);
                await Context.UpdateProfileAsync(updateData, CancellationToken);
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        profile."Id",
                        profile."DisplayName",
                        profile."UiLocaleId"
                    FROM
                        "Profiles" AS profile
                    WHERE
                        profile."Id" = @ProfileId;
                    """;
                sql.Parameters.Add(Parameters);
                var pending = await Context.ReadRequiredAsync<ReadProfileResultV1>(sql, CancellationToken);
                Assert.AreEqual("Pending", pending.DisplayName);
                throw new InvalidOperationException("Fail before commit.");
            }
        }

        internal sealed class ProfileFixture : IAsyncDisposable
        {
            private readonly NpgsqlDataSource _setup;

            public NpgsqlDataSource Database { get; }
            public string ConnectionString { get; }
            public long AccountAId { get; } = 41;
            public long ProfileAId { get; } = 101;
            public long ProfileBId { get; } = 202;
            public Guid SessionPublicId { get; } = Guid.NewGuid();
            public string SessionToken { get; } = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

            private ProfileFixture(NpgsqlDataSource setup, NpgsqlDataSource database, string connectionString)
            {
                _setup = setup;
                Database = database;
                ConnectionString = connectionString;
            }

            public static async Task<ProfileFixture> CreateAsync()
            {
                var cs = TestPostgres.ResolveConnectionString($"Data Source=profile-vertical-{Guid.NewGuid():N}");
                var isolated = new NpgsqlConnectionStringBuilder(cs) { SearchPath = "\"ProfileReference\"" };
                var setup = new NpgsqlDataSourceBuilder(isolated.ConnectionString).Build();
                var database = new NpgsqlDataSourceBuilder(isolated.ConnectionString).Build();
                var fixture = new ProfileFixture(setup, database, isolated.ConnectionString);

                try
                {
                    await fixture.RunBootstrapAsync("CREATE SCHEMA \"ProfileReference\";");
                    using var schema = typeof(ReadProfileV1).Assembly.GetManifestResourceStream("Jularr.Database.Account.sql")
                        ?? throw new InvalidOperationException("The account schema is missing.");
                    using var reader = new StreamReader(schema);
                    await fixture.RunBootstrapAsync(await reader.ReadToEndAsync());
                    await fixture.RunBootstrapAsync(
                        """
                        BEGIN;
                        INSERT INTO "UiLocales" ("Id", "Locale", "Name") VALUES (1, 'de-DE', 'Deutsch'), (2, 'en-GB', 'English');
                        SELECT setval(pg_get_serial_sequence('"UiLocales"', 'Id'), 2);
                        INSERT INTO "Accounts" ("Id", "Email", "DisplayName", "AccountRoleTypeId")
                        VALUES (41, 'a@example.test', 'Account A', 2), (42, 'b@example.test', 'Account B', 2);
                        INSERT INTO "ProfileReference"."Profiles" ("Id", "OwnerAccountId", "DisplayName", "UiLocaleId")
                        VALUES (101, 41, 'Profile A', 1), (202, 42, 'Profile B', 1);
                        INSERT INTO "ProfileReference"."AccountProfiles" ("AccountId", "ProfileId") VALUES (41, 101), (42, 202);
                        INSERT INTO "ProfileReference"."AccountSessions" ("PublicId", "AccountId", "ActiveProfileId", "TokenHash", "ExpiresAt")
                        VALUES (@SessionPublicId, 41, 101, @SessionTokenHash, now() + interval '1 hour');
                        COMMIT;
                        """,
                        SqlParams.Create().Add("SessionPublicId", fixture.SessionPublicId)
                            .Add("SessionTokenHash", System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fixture.SessionToken))).ToArray());
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

            public Task RequireModulesAsync(ModuleRequirement modules, LogicContext context, bool canMutate, CancellationToken cancellationToken)
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

                return Task.FromResult(new ServiceCaller(accountId, profileId, ServiceArea.User, sessionPublicId.ToString("D"), sessionPublicId, 1, accountId));
            }

            public async Task RequireAccessAsync(
                ServiceCaller caller,
                ServicePermission permission,
                CancellationToken cancellationToken)
            {
                Events.Add("access");
                if (permission.Key != "profile.self.read" && permission.Key != "profile.self.update")
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
                LogicContext context,
                CancellationToken cancellationToken)
            {
                Events.Add("transaction");
                if (DenyTransactionalWrite || permission.Key != "profile.self.update")
                {
                    throw new UnauthorizedAccessException("Profile mutation is not permitted.");
                }

                var sql = new SqlStatement();
                sql.Statement = """
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
                    """;
                sql.Parameters.Add(new
                {
                    ActiveProfileId = caller.ProfileId!.Value,
                    ActorAccountId = caller.AccountId!.Value
                });

                await context.ReadRequiredAsync<LockedProfile>(sql, cancellationToken);
            }
        }
    }
}
