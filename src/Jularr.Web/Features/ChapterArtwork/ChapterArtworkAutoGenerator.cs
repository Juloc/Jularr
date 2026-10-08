using Jularr.Web.Data;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Performance;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.ChapterArtwork;

/// <summary>
/// Opt-in background generation (off by default): for profiles that enabled
/// "generate missing artwork", creates artwork for the chapter they are
/// reading and the next one when neither has any artwork yet. Bounded per
/// sweep, never replaces existing images, and never runs as a side effect of
/// opening a page.
/// </summary>
public sealed class ChapterArtworkAutoGenerator(
    IServiceScopeFactory scopeFactory,
    ILogger<ChapterArtworkAutoGenerator> logger,
    BackgroundWorkGovernor? governor = null) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private const int MaxPerProfile = 2;
    private const int RecentWorks = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);

            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var store = new ChapterArtworkStore(scope.ServiceProvider.GetRequiredService<AppDbContext>());
                await store.FailInterruptedAsync(DateTime.UtcNow - InitialDelay, stoppingToken);
            }

            using var timer = new PeriodicTimer(Interval);
            do
            {
                await governor.RunGovernedAsync(BackgroundWorkClass.Maintenance, "ChapterArtwork.Generate", SweepAsync, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using (var moduleScope = scopeFactory.CreateAsyncScope())
        {
            var modules = moduleScope.ServiceProvider.GetService<IInstanceModuleService>();
            if (modules is not null
                && !await modules.IsEnabledAsync(
                    InstanceModule.Novel,
                    cancellationToken))
            {
                return;
            }
        }

        IReadOnlyList<string> profiles;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            profiles = await new ChapterArtworkStore(scope.ServiceProvider.GetRequiredService<AppDbContext>())
                .ListAutoGenerateProfilesAsync(cancellationToken);
        }

        foreach (var profileId in profiles)
        {
            try
            {
                await SweepProfileAsync(profileId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Automatic chapter artwork failed for profile {ProfileId}.", profileId);
            }
        }
    }

    private async Task SweepProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = ChapterArtworkService.ForProfile(scope.ServiceProvider, profileId);

        var reading = await (
            from progress in db.NovelProgress.AsNoTracking()
            join chapter in db.NovelChapters.AsNoTracking() on progress.ChapterId equals chapter.Id
            where progress.ProfileId == profileId
            orderby progress.UpdatedAt descending
            select new { progress.WorkId, chapter.Number }
        ).Take(RecentWorks).ToListAsync(cancellationToken);

        var generated = 0;
        foreach (var item in reading)
        {
            foreach (var chapterNumber in new[] { item.Number, item.Number + 1 })
            {
                if (generated >= MaxPerProfile)
                {
                    return;
                }

                var request = await service.RequestAsync(item.WorkId, chapterNumber, profileId, regenerate: false, cancellationToken);
                if (request.Outcome == ChapterArtworkRequestOutcome.Unavailable)
                {
                    break;
                }

                if (request.Outcome != ChapterArtworkRequestOutcome.Queued)
                {
                    continue;
                }

                generated++;
                try
                {
                    await service.GenerateAsync(request.ArtworkIds, cancellationToken);

                    // Opting into automatic artwork means accepting it; it can still be replaced or removed.
                    await service.AcceptAsync(request.ArtworkIds[0], cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The rows carry the failure; the owner can retry from the artwork page.
                    logger.LogInformation(exception, "Automatic chapter artwork failed for work {WorkId}.", item.WorkId);
                }
            }
        }
    }
}
