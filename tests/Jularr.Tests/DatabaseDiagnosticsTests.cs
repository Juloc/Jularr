using Jularr.Web.Features.Performance;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jularr.Tests;

/// <summary>The read-only PostgreSQL evidence behind Admin → Database (#859), on the real test PostgreSQL.</summary>
[TestClass]
public sealed class DatabaseDiagnosticsTests
{
    [TestMethod]
    public async Task TheReportDescribesTheDatabaseAndAnUnavailableStatementModuleIsSaidSo()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();

        var report = await new DatabaseDiagnosticsService(db).ReadAsync(DatabaseStatementRanking.TotalTime, CancellationToken.None);

        Assert.IsFalse(string.IsNullOrWhiteSpace(report.Overview.Version));
        Assert.IsFalse(report.Overview.Version.Contains(' '), "Only the version number is shown.");
        Assert.IsTrue(report.Overview.SizeBytes > 0);
        Assert.IsTrue(report.Overview.MaxConnections > 0);
        Assert.IsTrue(report.Overview.ActiveConnections + report.Overview.IdleConnections + report.Overview.IdleInTransaction >= 1);
        Assert.IsTrue(report.Tables.Count > 0 && report.Tables.Count <= DatabaseDiagnosticsService.RowLimit);
        Assert.IsTrue(report.Tables.Zip(report.Tables.Skip(1)).All(pair => pair.First.TotalBytes >= pair.Second.TotalBytes), "The largest tables come first.");
        Assert.IsTrue(report.Indexes.Count > 0);
        Assert.AreEqual(0, report.LockWaits.Count);
        Assert.IsTrue(report.StatementsState is DatabaseStatementsState.NotInstalled or DatabaseStatementsState.NotLoaded or DatabaseStatementsState.Available);
        if (report.StatementsState != DatabaseStatementsState.Available)
        {
            Assert.AreEqual(0, report.Statements.Count);
        }
    }

    [TestMethod]
    public async Task TheDiagnosticsRunInAReadOnlyTransactionAndLeaveTheConnectionUsable()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var service = new DatabaseDiagnosticsService(db);

        await service.ReadAsync(DatabaseStatementRanking.Calls, CancellationToken.None);
        await service.ReadAsync(DatabaseStatementRanking.MeanTime, CancellationToken.None);

        Assert.IsTrue(await db.Database.CanConnectAsync(), "The shared context is still usable after the reads.");
    }

    [TestMethod]
    public async Task AStatementWaitingForALockIsReportedWithItsBlocker()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var connectionString = db.Database.GetConnectionString()!;
        await using var holder = new NpgsqlConnection(connectionString);
        await holder.OpenAsync();
        await using var transaction = await holder.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("LOCK TABLE \"__EFMigrationsHistory\" IN ACCESS EXCLUSIVE MODE", holder, transaction))
        {
            await lockCommand.ExecuteNonQueryAsync();
        }

        await using var waiter = new NpgsqlConnection(connectionString);
        await waiter.OpenAsync();
        var waiting = Task.Run(async () =>
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" <> 'secret-literal-value'", waiter);
            return await command.ExecuteScalarAsync();
        });

        var service = new DatabaseDiagnosticsService(db);
        DatabaseDiagnosticsReport? report = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            report = await service.ReadAsync(DatabaseStatementRanking.TotalTime, CancellationToken.None);
            if (report.LockWaits.Count > 0)
            {
                break;
            }

            await Task.Delay(100);
        }

        await transaction.RollbackAsync();
        await waiting;

        Assert.IsNotNull(report);
        var wait = report.LockWaits.Single();
        Assert.AreEqual(1, wait.BlockerPids.Count);
        Assert.IsFalse(wait.Query.Contains("secret-literal-value"), "Query text is shown without its literals.");
        Assert.IsTrue(report.Warnings.Contains("lockWaits"));
        Assert.IsTrue(report.RelationsSkipped, "Sizes are skipped, not waited for, while a table is exclusively locked.");
        Assert.AreEqual(0, report.Tables.Count);

        var afterwards = await service.ReadAsync(DatabaseStatementRanking.TotalTime, CancellationToken.None);
        Assert.IsFalse(afterwards.RelationsSkipped);
        Assert.IsTrue(afterwards.Tables.Count > 0);
    }

    [TestMethod]
    public void QueryTextLosesLiteralsLongNumbersAndExcessLength()
    {
        var sanitized = DatabaseDiagnosticsService.Sanitize("SELECT *\n  FROM \"Users\" WHERE \"Email\" = 'a@b.example' AND \"Token\" = 'it''s secret' AND \"Id\" = 123456789 AND \"Age\" = 42");

        Assert.AreEqual("SELECT * FROM \"Users\" WHERE \"Email\" = '?' AND \"Token\" = '?' AND \"Id\" = ? AND \"Age\" = 42", sanitized);
        var bounded = DatabaseDiagnosticsService.Sanitize(new string('x', 1000));
        Assert.AreEqual(DatabaseDiagnosticsService.QueryTextLimit + 1, bounded.Length);
        Assert.AreEqual(string.Empty, DatabaseDiagnosticsService.Sanitize(null));
    }

    [TestMethod]
    public void ReviewHintsNeedEvidenceAndAConstraintBackedIndexIsNeverFlagged()
    {
        Assert.AreEqual("deadTuples", DatabaseDiagnosticsService.TableHint(1_000_000, live: 4_000, dead: 2_000, sequentialTuplesRead: 0, indexScans: 0, lastAnalyze: DateTime.UtcNow));
        Assert.AreEqual("neverAnalyzed", DatabaseDiagnosticsService.TableHint(1_000_000, live: 5_000, dead: 0, sequentialTuplesRead: 0, indexScans: 0, lastAnalyze: null));
        Assert.AreEqual("sequentialReads", DatabaseDiagnosticsService.TableHint(1_000_000, live: 20_000, dead: 0, sequentialTuplesRead: 5_000_000, indexScans: 10, lastAnalyze: DateTime.UtcNow));
        Assert.IsNull(DatabaseDiagnosticsService.TableHint(1_000, live: 50, dead: 40, sequentialTuplesRead: 100, indexScans: 0, lastAnalyze: null), "A tiny table is never flagged.");

        Assert.AreEqual("unused", DatabaseDiagnosticsService.IndexHint(5_000_000, scans: 0, backsConstraint: false));
        Assert.IsNull(DatabaseDiagnosticsService.IndexHint(5_000_000, scans: 0, backsConstraint: true));
        Assert.IsNull(DatabaseDiagnosticsService.IndexHint(5_000_000, scans: 3, backsConstraint: false));
        Assert.IsNull(DatabaseDiagnosticsService.IndexHint(1_000, scans: 0, backsConstraint: false));
    }
}
