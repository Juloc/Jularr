using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Session;
using Npgsql;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class SessionLockTests
    {
        [TestMethod]
        [DataRow("profile", true)]
        [DataRow("disabled", false)]
        [DataRow("credentials", false)]
        [DataRow("revoked", false)]
        [DataRow("rotated", false)]
        public async Task Authorization_ConcurrentAccountWrite_LocksAccountFirstAndRevalidates(string change, bool remainsAuthorized)
        {
            await using var fixture = await ProfileReferenceVerticalTests.ProfileFixture.CreateAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ct = timeout.Token;
            await using var writer = await fixture.Database.OpenConnectionAsync(ct);
            await using var transaction = await writer.BeginTransactionAsync(ct);
            await using (var accountLock = new NpgsqlCommand("SELECT \"Id\" FROM \"Accounts\" WHERE \"Id\" = 41 FOR UPDATE", writer, transaction))
            {
                await accountLock.ExecuteScalarAsync(ct);
            }

            await using var authorization = new SqlContext(fixture.Database);
            await authorization.BeginAsync(SqlAccessMode.ReadWrite, cancellationToken: ct);
            var authorizationPid = (int)(await authorization.ReadSql.ExecuteScalarAsync(new SqlStatement { Statement = "SELECT pg_backend_pid()" }, ct))!;
            var parameters = new SessionParameters(fixture.SessionPublicId, 1, SHA256.HashData(Encoding.UTF8.GetBytes(fixture.SessionToken)));
            var pendingAuthorization = new LogicContext(authorization).ReadSessionAsync(parameters, ct);

            try
            {
                await using var observer = await fixture.Database.OpenConnectionAsync(ct);
                await using var blocked = new NpgsqlCommand("SELECT @writer = ANY(pg_blocking_pids(@reader))", observer);
                blocked.Parameters.AddWithValue("writer", writer.ProcessID);
                blocked.Parameters.AddWithValue("reader", authorizationPid);
                while (!(bool)(await blocked.ExecuteScalarAsync(ct))!)
                {
                    await Task.Delay(20, ct);
                }

                var statement = change switch
                {
                    "profile" => "UPDATE \"Profiles\" SET \"UiLocaleId\" = 2 WHERE \"Id\" = 101",
                    "disabled" => "UPDATE \"Accounts\" SET \"IsEnabled\" = FALSE WHERE \"Id\" = 41",
                    "credentials" => "UPDATE \"Accounts\" SET \"CredentialRevision\" = 2 WHERE \"Id\" = 41",
                    "revoked" => "UPDATE \"AccountSessions\" SET \"RevokedAt\" = now() WHERE \"AccountId\" = 41",
                    "rotated" => "UPDATE \"AccountSessions\" SET \"RotationRevision\" = 2 WHERE \"AccountId\" = 41",
                    _ => throw new ArgumentOutOfRangeException(nameof(change))
                };
                await using (var update = new NpgsqlCommand(statement, writer, transaction))
                {
                    Assert.AreEqual(1, await update.ExecuteNonQueryAsync(ct));
                }

                await transaction.CommitAsync(ct);
                if (remainsAuthorized)
                {
                    var session = await pendingAuthorization;
                    Assert.AreEqual(fixture.AccountAId, session.AccountId);
                    Assert.AreEqual(fixture.ProfileAId, session.ProfileId);
                }
                else
                {
                    await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => pendingAuthorization);
                }
                await authorization.CommitAsync(ct);
            }
            finally
            {
                // Release the writer before awaiting a failed or cancelled reader.
                await transaction.DisposeAsync();
                if (!pendingAuthorization.IsCompleted)
                {
                    await timeout.CancelAsync();
                }

                try
                {
                    await pendingAuthorization;
                }
                catch when (ct.IsCancellationRequested || pendingAuthorization.IsFaulted)
                {
                }
            }
        }

        [TestMethod]
        public async Task Authorization_ProfileForeignKeyReferences_DoNotWaitForAuthorization()
        {
            await using var fixture = await ProfileReferenceVerticalTests.ProfileFixture.CreateAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ct = timeout.Token;
            await using var authorization = new SqlContext(fixture.Database);
            await authorization.BeginAsync(SqlAccessMode.ReadWrite, cancellationToken: ct);
            var parameters = new SessionParameters(fixture.SessionPublicId, 1, SHA256.HashData(Encoding.UTF8.GetBytes(fixture.SessionToken)));
            await new LogicContext(authorization).ReadSessionAsync(parameters, ct);

            await using var writer = await fixture.Database.OpenConnectionAsync(ct);
            await using var reference = new NpgsqlCommand("INSERT INTO \"AccountProfiles\" (\"AccountId\", \"ProfileId\") VALUES (42, 101)", writer);
            Assert.AreEqual(1, await reference.ExecuteNonQueryAsync(ct));
            await authorization.CommitAsync(ct);
        }
    }
}
