using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.Core
{
    // The step that failed (a migration name, "backup" or "baseline") and why; the reason is for the log, never for a page.
    public sealed record DatabaseUpgradeFailure(string Migration, string Reason);

    // What the start of a new version is doing: looking at the database, making the backup, applying migrations (Done of Total).
    public enum DatabaseUpgradePhase { Checking, BackingUp, Migrating }

    public sealed record DatabaseUpgradeProgress(DatabaseUpgradePhase Phase, int Done = 0, int Total = 0);

    // The database update at the start of a new version, run before the web host starts serving. The host calls it once; DatabaseBaseline
    // also runs as a hosted service, where a database that is current costs one version read.
    public static class DatabaseUpgrade
    {
        // Null when the database is current (after any migrations and their backup). A failed update was rolled back (or never began); it is logged
        // here and returned, and the caller serves its maintenance page instead of the application.
        // The progress callback is told each phase as it begins, for the status page shown while the start is busy.
        public static async Task<DatabaseUpgradeFailure?> RunAsync(IServiceProvider services, CancellationToken cancellationToken = default, Action<DatabaseUpgradeProgress>? progress = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            try
            {
                var baseline = services.GetRequiredService<DatabaseBaseline>();
                baseline.Progress = progress;
                progress?.Invoke(new DatabaseUpgradeProgress(DatabaseUpgradePhase.Checking));
                try
                {
                    await baseline.StartAsync(cancellationToken);
                }
                finally
                {
                    baseline.Progress = null;
                }

                return null;
            }
            catch (DatabaseUpgradeException exception)
            {
                var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Jularr.DatabaseUpgrade");
                logger.LogCritical(exception,
                    "Database update failed at {Migration}: {Reason}. Nothing was changed by the failed step; Jularr does not start and serves a maintenance page. Start the previous version again; the backup made before the update is in the backup folder.",
                    exception.Migration, exception.Reason);
                return new DatabaseUpgradeFailure(exception.Migration, exception.Reason);
            }
        }
    }
}
