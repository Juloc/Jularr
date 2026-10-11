using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Enums.Account;
using System.Reflection;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields;
using Jularr.Backend.Data.Fields.Localization;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.Fields.Provider;
using Jularr.Backend.Data.Fields.Session;
using Jularr.Backend.Infrastructure.Sql;
using Npgsql;
using NpgsqlTypes;
using Fixture = Jularr.Backend.Tests.ProfileReferenceVerticalTests.ProfileFixture;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class AccountSchemaTests
    {
        [TestMethod]
        public async Task Schema_FieldDefinitionsAndCatalogs_MatchPostgres()
        {
            await using var fixture = await Fixture.CreateAsync();
            var definitions = new Dictionary<string, Type>
            {
                ["Accounts"] = typeof(AccountFields),
                ["AccountRoleTypes"] = typeof(AccountRoleTypeFields),
                ["Profiles"] = typeof(ProfileFields),
                ["AccountProfiles"] = typeof(AccountProfileFields),
                ["UiLocales"] = typeof(UiLocaleFields),
                ["Providers"] = typeof(ProviderFields),
                ["AccountPasswords"] = typeof(AccountPasswordFields),
                ["AccountPasskeys"] = typeof(AccountPasskeyFields),
                ["AccountTotpFactors"] = typeof(AccountTotpFactorFields),
                ["AccountRecoveryCodes"] = typeof(AccountRecoveryCodeFields),
                ["AccountExternalLogins"] = typeof(AccountExternalLoginFields),
                ["AuthChallengePurposeTypes"] = typeof(AuthChallengePurposeTypeFields),
                ["AccountAuthChallenges"] = typeof(AccountAuthChallengeFields),
                ["AccountExternalAuthFlows"] = typeof(AccountExternalAuthFlowFields),
                ["AccountSessions"] = typeof(SessionFields)
            };
            var databaseTypes = new Dictionary<string, NpgsqlDbType>
            {
                ["int8"] = NpgsqlDbType.Bigint,
                ["int2"] = NpgsqlDbType.Smallint,
                ["text"] = NpgsqlDbType.Text,
                ["varchar"] = NpgsqlDbType.Varchar,
                ["citext"] = NpgsqlDbType.Citext,
                ["bool"] = NpgsqlDbType.Boolean,
                ["timestamptz"] = NpgsqlDbType.TimestampTz,
                ["uuid"] = NpgsqlDbType.Uuid,
                ["bytea"] = NpgsqlDbType.Bytea,
                ["_text"] = NpgsqlDbType.Array | NpgsqlDbType.Text
            };
            await using var connection = await fixture.Database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                """
                SELECT
                    table_name,
                    column_name,
                    udt_name,
                    is_nullable,
                    character_maximum_length
                FROM
                    information_schema.columns
                WHERE
                    table_schema = 'ProfileReference';
                """, connection);
            var seen = new HashSet<(string Table, string Column)>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var table = reader.GetString(0);
                    var column = reader.GetString(1);
                    var definition = definitions[table].GetField(column, BindingFlags.Public | BindingFlags.Static);
                    Assert.IsNotNull(definition, table + "." + column);
                    var field = (DataField)definition.GetValue(null)!;
                    Assert.AreEqual(databaseTypes[reader.GetString(2)], field.DatabaseType, table + "." + column);
                    Assert.AreEqual(reader.GetString(3) == "NO", field.Required, table + "." + column);
                    if (!reader.IsDBNull(4))
                    {
                        Assert.AreEqual(reader.GetInt32(4), field.MaximumLength, table + "." + column);
                    }

                    seen.Add((table, column));
                }
            }

            foreach (var (table, type) in definitions)
            {
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    Assert.IsTrue(seen.Contains((table, field.Name)), table + "." + field.Name);
                }
            }

            command.CommandText =
                """
                SELECT 'role' AS catalog, "Id", "Key" FROM "AccountRoleTypes"
                UNION ALL
                SELECT 'purpose' AS catalog, "Id", "Key" FROM "AuthChallengePurposeTypes";
                """;
            var actual = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    actual.Add($"{reader.GetString(0)}:{reader.GetInt16(1)}:{reader.GetString(2)}");
                }
            }

            CollectionAssert.AreEquivalent(new[]
            {
                $"role:{(byte)AccountRoleType.Owner}:owner",
                $"role:{(byte)AccountRoleType.User}:user",
                $"role:{(byte)AccountRoleType.MediaManager}:media_manager",
                $"purpose:{(byte)AuthChallengePurposeType.EmailVerification}:email_verification",
                $"purpose:{(byte)AuthChallengePurposeType.PasswordReset}:password_reset",
                $"purpose:{(byte)AuthChallengePurposeType.TotpEnrollment}:totp_enrollment",
                $"purpose:{(byte)AuthChallengePurposeType.TotpSignIn}:totp_sign_in",
                $"purpose:{(byte)AuthChallengePurposeType.PasskeyEnrollment}:passkey_enrollment",
                $"purpose:{(byte)AuthChallengePurposeType.PasskeySignIn}:passkey_sign_in",
                $"purpose:{(byte)AuthChallengePurposeType.ExternalLogin}:external_login",
                $"purpose:{(byte)AuthChallengePurposeType.ExternalLink}:external_link",
                $"purpose:{(byte)AuthChallengePurposeType.ProviderMediaConsent}:provider_media_consent"
            }, actual);
        }

        private sealed class AuthBinding : BaseData
        {
            public AuthBinding(long accountId, string email, AccountRoleType accountRoleTypeId, byte[] credentialId, byte[] publicKey, string[] transports, string displayName)
            {
                AccountId = accountId;
                Email = email;
                AccountRoleTypeId = accountRoleTypeId;
                CredentialId = credentialId;
                PublicKey = publicKey;
                Transports = transports;
                DisplayName = displayName;
            }

            [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
            public long AccountId { get; }

            [DataField(typeof(AccountFields), nameof(AccountFields.Email))]
            public string Email { get; }

            [DataField(typeof(AccountFields), nameof(AccountFields.AccountRoleTypeId))]
            public AccountRoleType AccountRoleTypeId { get; }

            [DataField(typeof(AccountPasskeyFields), nameof(AccountPasskeyFields.CredentialId))]
            public byte[] CredentialId { get; }

            [DataField(typeof(AccountPasskeyFields), nameof(AccountPasskeyFields.PublicKey))]
            public byte[] PublicKey { get; }

            [DataField(typeof(AccountPasskeyFields), nameof(AccountPasskeyFields.Transports))]
            public string[] Transports { get; }

            [DataField(typeof(AccountPasskeyFields), nameof(AccountPasskeyFields.DisplayName))]
            public string DisplayName { get; }
        }

        [TestMethod]
        public async Task Auth_TypedBindingAndConstraints_PreserveIdentityAndSecretRules()
        {
            await using var fixture = await Fixture.CreateAsync();
            var data = new AuthBinding(43, "c@example.test", AccountRoleType.User, [1], [2], ["internal"], "Passkey");
            data.Validate();
            Assert.ThrowsExactly<DataValidationException>(() => new AuthBinding(data.AccountId, data.Email, (AccountRoleType)99, data.CredentialId, data.PublicKey, data.Transports, data.DisplayName).Validate());
            Assert.ThrowsExactly<DataValidationException>(() => SessionFields.TokenHash.Validate(new byte[31], "TokenHash"));
            Assert.ThrowsExactly<DataValidationException>(() => SessionFields.TokenHash.Validate(new byte[33], "TokenHash"));
            await using (var context = new SqlContext(fixture.Database))
            {
                await context.BeginAsync(SqlAccessMode.ReadWrite);
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    INSERT INTO "Accounts" ("Id", "Email", "DisplayName", "AccountRoleTypeId")
                    VALUES (@AccountId, @Email, 'Account A', @AccountRoleTypeId);
                    INSERT INTO "AccountPasskeys" ("AccountId", "CredentialId", "PublicKey", "Transports", "DisplayName")
                    VALUES (@AccountId, @CredentialId, @PublicKey, @Transports, @DisplayName);
                    """;
                sql.Parameters.Add(data);
                await context.RequireLogicSql().ExecuteAsync(sql);
                await context.CommitAsync();
            }

            await fixture.RunSqlAsync(
                """
                INSERT INTO "AccountPasswords" ("AccountId", "PasswordHash") VALUES (41, 'stored-password-hash');
                INSERT INTO "AccountTotpFactors" ("AccountId", "EncryptedSecret", "ConfirmedAt", "LastAcceptedStep")
                VALUES (41, decode('ab', 'hex'), now(), 1);
                INSERT INTO "AccountRecoveryCodes" ("AccountId", "CodeHash") VALUES (41, decode(repeat('cd', 32), 'hex'));
                INSERT INTO "Providers" ("Id", "Key") VALUES (1, 'plex') ON CONFLICT DO NOTHING;
                INSERT INTO "AccountExternalLogins" ("AccountId", "ProviderId", "ExternalAccountId") VALUES (41, 1, 'external-a');
                INSERT INTO "AccountAuthChallenges" ("Id", "AccountId", "AuthChallengePurposeTypeId", "TokenHash", "CredentialRevision", "ExpiresAt")
                VALUES (51, 41, 2, decode(repeat('ef', 32), 'hex'), 1, now() + interval '1 hour'),
                    (52, NULL, 7, decode(repeat('01', 32), 'hex'), NULL, now() + interval '1 hour');
                INSERT INTO "AccountExternalAuthFlows" ("AccountAuthChallengeId", "AuthChallengePurposeTypeId", "ProviderId", "ExternalPinId", "ClientIdentifier")
                VALUES (52, 7, 1, 1, 'client');
                INSERT INTO "AccountSessions" ("AccountId", "TokenHash", "ExpiresAt")
                VALUES (43, decode(repeat('02', 32), 'hex'), now() + interval '1 hour');
                """);

            (string Statement, string Constraint)[] invalid =
            [
                ("INSERT INTO \"Accounts\" (\"Email\", \"DisplayName\", \"AccountRoleTypeId\") VALUES ('A@EXAMPLE.TEST', 'other', 2)", "UX_Accounts_Email"),
                ("INSERT INTO \"AccountPasswords\" (\"AccountId\", \"PasswordHash\") VALUES (42, ' ')", "CK_AccountPasswords_Hash"),
                ("INSERT INTO \"AccountPasskeys\" (\"AccountId\", \"CredentialId\", \"PublicKey\", \"BackedUp\") VALUES (41, decode('03', 'hex'), decode('04', 'hex'), TRUE)", "CK_AccountPasskeys_BackupState"),
                ("INSERT INTO \"AccountTotpFactors\" (\"AccountId\", \"EncryptedSecret\", \"LastAcceptedStep\") VALUES (41, decode('ab', 'hex'), 1)", "CK_AccountTotpFactors_Timestamps"),
                ("INSERT INTO \"AccountRecoveryCodes\" (\"AccountId\", \"CodeHash\") VALUES (41, decode('ab', 'hex'))", "CK_AccountRecoveryCodes_Hash"),
                ("INSERT INTO \"AccountExternalLogins\" (\"AccountId\", \"ProviderId\", \"ExternalAccountId\") VALUES (42, 1, 'external-a')", "UX_AccountExternalLogins_Provider_ExternalId"),
                ("INSERT INTO \"AccountAuthChallenges\" (\"AuthChallengePurposeTypeId\", \"TokenHash\", \"ExpiresAt\") VALUES (2, decode(repeat('03', 32), 'hex'), now() + interval '1 hour')", "CK_AccountAuthChallenges_Account"),
                ("INSERT INTO \"AccountSessions\" (\"AccountId\", \"ActiveProfileId\", \"TokenHash\", \"ExpiresAt\") VALUES (42, 101, decode(repeat('04', 32), 'hex'), now() + interval '1 hour')", "FK_AccountSessions_AccountProfiles"),
                ("UPDATE \"AccountSessions\" SET \"TokenHash\" = decode('ab', 'hex') WHERE \"AccountId\" = 43", "CK_AccountSessions_Hash"),
                ("UPDATE \"AccountExternalAuthFlows\" SET \"AuthChallengePurposeTypeId\" = 8 WHERE \"AccountAuthChallengeId\" = 52", "FK_AccountExternalAuthFlows_ChallengePurpose"),
                ("UPDATE \"AccountExternalAuthFlows\" SET \"ReturnPath\" = '//foreign.example' WHERE \"AccountAuthChallengeId\" = 52", "CK_AccountExternalAuthFlows_ReturnPath")
            ];
            foreach (var (statement, constraint) in invalid)
            {
                var exception = await Assert.ThrowsExactlyAsync<PostgresException>(() => fixture.RunSqlAsync(statement));
                Assert.AreEqual(constraint, exception.ConstraintName);
            }
        }
    }
}
