using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Data;

/// <summary>
/// Applies EF Core migrations to the canonical PostgreSQL database at startup and in
/// tests. EF Core takes its own advisory lock for the duration of the migration, so a
/// second instance starting against the same database waits rather than racing.
/// </summary>
public static class DatabaseMigrationBridge
{
    private static readonly TimeSpan MigrationTimeout = TimeSpan.FromMinutes(5);

    public static async Task UpgradeAsync(
        AppDbContext db,
        CancellationToken cancellationToken = default,
        Action<string>? log = null)
    {
        log?.Invoke("Applying pending EF Core migrations.");

        using var migrationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        migrationCts.CancelAfter(MigrationTimeout);

        try
        {
            await db.Database.MigrateAsync(migrationCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Database migration did not finish within {MigrationTimeout.TotalSeconds:0} seconds. " +
                "Another Jularr instance may be migrating the same database, or the database is locked.");
        }

        log?.Invoke("Database migrations are complete.");
    }
}
