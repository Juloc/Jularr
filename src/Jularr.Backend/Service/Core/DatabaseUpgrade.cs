using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.Core
{
    // The step that failed (a migration name, "backup" or "baseline") and why; the reason is for the log, never for a page.
    public sealed record DatabaseUpgradeFailure(string Migration, string Reason);

    // The database update at the start of a new version, run before the web host starts serving. The host calls it once; DatabaseBaseline
    // also runs as a hosted service, where a database that is current costs one version read.
    public static class DatabaseUpgrade
    {
        // Null when the database is current (after any migrations and their backup). A failed update was rolled back (or never began); it is logged
        // here and returned, and the caller serves its maintenance page instead of the application.
        public static async Task<DatabaseUpgradeFailure?> RunAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(services);
            try
            {
                await services.GetRequiredService<DatabaseBaseline>().StartAsync(cancellationToken);
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
