using Jularr.Web.Features.Performance;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>
/// Runs <see cref="LibraryWorkBackfill"/> in the background so that library entries that arrive without a request (a dropped EPUB, a scan) get their Work too.
/// Nothing runs on the startup path: the first pass waits <see cref="StartupDelay"/>, and a pass that finds nothing unbound costs one query.
/// </summary>
public sealed class LibraryWorkBackfillService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<LibraryWorkBackfillService> logger, BackgroundWorkGovernor? governor = null) : BackgroundService
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await governor.RunGovernedAsync(BackgroundWorkClass.Maintenance, "Library.WorkBackfill", async token =>
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<LibraryWorkBackfill>().RunAsync(token);
                    }, stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "The library Work backfill pass failed; the next pass tries again.");
                }

                await Task.Delay(Interval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
