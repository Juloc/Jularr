using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Performance;

namespace Jularr.Web.Features.Tracking;

public sealed record AniListSyncOverview(
    AniListSyncMode Mode,
    DateTimeOffset? EnabledAt,
    IReadOnlyList<AniListSyncItem> Activity,
    DateTimeOffset? PausedUntil);

/// <summary>
/// Current profile's automatic sync setting and recent activity for the
/// AniList settings page. Everything is read and written for the signed-in
/// profile only.
/// </summary>
public sealed class AniListSyncService(
    AniListAccountStore accounts,
    AniListSyncStateStore states,
    AniListRateLimitGate rateLimit,
    CurrentAccountContext currentAccount,
    TimeProvider timeProvider,
    IInstanceModuleService? instanceModules = null)
{
    public const int ActivityLimit = 20;

    public async Task<AniListSyncOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        if (!await IsTrackingEnabledAsync(cancellationToken))
        {
            return new AniListSyncOverview(AniListSyncMode.Off, null, [], null);
        }

        var account = await accounts.LoadAsync(currentAccount.ProfileId, cancellationToken);
        if (account is null)
        {
            return new AniListSyncOverview(AniListSyncMode.Off, null, [], null);
        }

        var state = await states.LoadAsync(
            currentAccount.ProfileId,
            account.ViewerId,
            cancellationToken);

        var instance = instanceModules is null
            ? null
            : await instanceModules.GetAsync(cancellationToken);

        return new AniListSyncOverview(
            account.SyncMode,
            account.SyncEnabledAt,
            state.Items
                .Where(x => x.Status != AniListSyncItemStatus.NotMatched)
                .Where(x => IsMediaKindEnabled(instance, x.MediaKind))
                .OrderByDescending(x => x.LastAttemptAt)
                .Take(ActivityLimit)
                .ToArray(),
            account.SyncMode == AniListSyncMode.Off
                ? null
                : rateLimit.BlockedUntil(timeProvider.GetUtcNow()));
    }

    public async Task<bool> SetModeAsync(AniListSyncMode mode, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (!await IsTrackingEnabledAsync(cancellationToken))
        {
            return false;
        }

        return await accounts.UpdateSyncModeAsync(
            currentAccount.ProfileId,
            mode,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private async Task<bool> IsTrackingEnabledAsync(CancellationToken cancellationToken) =>
        instanceModules is null
        || await instanceModules.IsEnabledAsync(
            InstanceModule.Tracking,
            cancellationToken);

    private static bool IsMediaKindEnabled(
        InstanceModuleSettings? settings,
        string mediaKind) =>
        settings is null
        || mediaKind switch
        {
            AniListSyncCheckpoints.Anime => settings.IsEnabled(InstanceModule.Anime),
            AniListSyncCheckpoints.Manga => settings.IsEnabled(InstanceModule.Manga),
            AniListSyncCheckpoints.Novel => settings.IsEnabled(InstanceModule.Novel),
            _ => true
        };
}

/// <summary>
/// In-process loop that runs <see cref="AniListSyncReconciler"/> once per
/// <see cref="AniListSyncReconciler.Interval"/>. Each pass gets its own DI
/// scope; each profile gets its own <see cref="AniListAccountService"/> bound
/// to that profile's identity and AniList token.
/// </summary>
public sealed class AniListSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    AniListAccountStore accounts,
    AniListSyncStateStore states,
    AniListRateLimitGate rateLimit,
    TimeProvider timeProvider,
    ILogger<AniListSyncBackgroundService> logger,
    BackgroundWorkGovernor? governor = null) : BackgroundService
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, timeProvider, stoppingToken);
            using var timer = new PeriodicTimer(AniListSyncReconciler.Interval, timeProvider);
            do
            {
                await governor.RunGovernedAsync(BackgroundWorkClass.ProviderRefresh, "AniList.Sync", RunPassAsync, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunPassAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var modules = services.GetService<IInstanceModuleService>();
            InstanceModuleSettings? instance = null;
            if (modules is not null)
            {
                instance = await modules.GetAsync(cancellationToken);
                if (!instance.IsEnabled(InstanceModule.Tracking))
                {
                    return;
                }
            }

            var enabledMediaKinds = new HashSet<string>(StringComparer.Ordinal);
            if (instance is null || instance.IsEnabled(InstanceModule.Anime))
            {
                enabledMediaKinds.Add(AniListSyncCheckpoints.Anime);
            }
            if (instance is null || instance.IsEnabled(InstanceModule.Manga))
            {
                enabledMediaKinds.Add(AniListSyncCheckpoints.Manga);
            }
            if (instance is null || instance.IsEnabled(InstanceModule.Novel))
            {
                enabledMediaKinds.Add(AniListSyncCheckpoints.Novel);
            }

            var httpClients = services.GetRequiredService<IHttpClientFactory>();
            var reconciler = new AniListSyncReconciler(
                services.GetRequiredService<AppDbContext>(),
                accounts,
                states,
                rateLimit,
                profileId => ActivatorUtilities.CreateInstance<AniListAccountService>(
                    services,
                    httpClients.CreateClient(nameof(AniListAccountService)),
                    AniListSyncReconciler.ProfileAccount(profileId)),
                timeProvider,
                logger,
                enabledMediaKinds);

            var summary = await reconciler.RunOnceAsync(cancellationToken);
            if (summary.Evaluated > 0)
            {
                logger.LogInformation(
                    "Automatic AniList sync evaluated {Evaluated} work(s): {Written} written, {Failed} failed.",
                    summary.Evaluated,
                    summary.Written,
                    summary.Failed);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Keep the loop alive; the next pass retries from the persisted cursors.
            logger.LogError(exception, "Automatic AniList sync pass failed.");
        }
    }
}
