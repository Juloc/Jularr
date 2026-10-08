using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Tracking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Web.Features.Acquisition.AniListAutoMonitor;

/// <summary>
/// P1 item 7: optionally auto-monitors anime that are on the profile's AniList Current/Planning
/// lists and already exist locally. Never adds an anime that has no local match, and never touches
/// an anime Sonarr manages (read-only coexistence): both are hard rules, not configurable.
/// </summary>
public sealed class AniListAutoMonitorService(
    AppDbContext db,
    AniListAccountStore accountStore,
    AniListAutoMonitorSettingsStore settingsStore,
    AcquisitionOwnershipStore ownershipStore,
    AnimeMonitoringStore monitoringStore,
    AnimeAcquisitionPipeline pipeline,
    IServiceProvider services,
    IHttpClientFactory httpClientFactory,
    ILogger<AniListAutoMonitorService> logger)
{
    public async Task<AniListAutoMonitorRunResult> RunForProfileAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        // Checked here too (not only by the scheduler's pre-filter), so calling this directly
        // never bypasses the opt-in rule.
        var autoMonitorSettings = await settingsStore.LoadAsync(cancellationToken);
        if (!autoMonitorSettings.IsEnabled(profileId))
        {
            return new(0, 0, 0, [$"Auto-monitor is off for profile '{profileId}'."]);
        }

        var account = await accountStore.LoadAsync(profileId, cancellationToken);
        if (account is null)
        {
            return new(0, 0, 0, [$"Profile '{profileId}' has no AniList connection."]);
        }

        IReadOnlyList<AniListLibraryMedia> library;
        try
        {
            var accountService = ActivatorUtilities.CreateInstance<AniListAccountService>(
                services,
                httpClientFactory.CreateClient(nameof(AniListAccountService)),
                AniListSyncReconciler.ProfileAccount(profileId));
            library = await accountService.GetLibraryAsync(AniListLibraryMediaType.Anime, cancellationToken);
        }
        catch (AniListAccountException exception)
        {
            logger.LogWarning(exception, "AniList list auto-monitor could not read the library for profile {ProfileId}.", profileId);
            return new(0, 0, 0, [exception.Message]);
        }

        var wanted = library
            .Where(media => media.ListStatus is "CURRENT" or "PLANNING")
            .ToArray();
        if (wanted.Length == 0)
        {
            return new(0, 0, 0, []);
        }

        var externalIds = wanted.Select(media => media.MediaId.ToString()).ToArray();
        var matches = await db.AnimeMetadata
            .AsNoTracking()
            .Where(meta => meta.Provider == AniListMetadataProvider.ProviderKey && externalIds.Contains(meta.ExternalId))
            .Select(meta => meta.AnimeId)
            .ToArrayAsync(cancellationToken);
        if (matches.Length == 0)
        {
            return new(wanted.Length, 0, 0, ["None of the profile's Current/Planning AniList anime exist locally yet."]);
        }

        var anime = await db.Anime
            .AsNoTracking()
            .Where(item => matches.Contains(item.Id))
            .Select(item => new { item.Id, item.Key })
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var ownership = await ownershipStore.LoadAsync(cancellationToken);
        var monitoring = await monitoringStore.LoadAsync(cancellationToken);
        var notes = new List<string>();
        var newlyMonitored = 0;

        foreach (var animeId in matches)
        {
            if (!anime.TryGetValue(animeId, out var entry))
            {
                continue;
            }

            // Never touch an anime Sonarr owns or coexists read-only with.
            if (SonarrParallelSafety.GetMode(ownership, entry.Key) == AnimeManagementMode.ReadOnlyCoexistence)
            {
                continue;
            }

            var existing = monitoring.Anime.TryGetValue(entry.Key, out var settings) ? settings : null;
            if (existing?.Monitored == true)
            {
                continue;
            }

            var update = await pipeline.UpdateAnimeSettingsAsync(
                animeId,
                monitored: true,
                searchOnAdd: true,
                profileId: null,
                indexerIds: existing?.IndexerIds ?? [],
                cancellationToken,
                targetRootId: existing?.TargetRootId);
            if (update is not null)
            {
                newlyMonitored++;
                notes.Add($"{entry.Key}: monitoring enabled (on the AniList Current/Planning list).");
            }
        }

        return new(wanted.Length, matches.Length, newlyMonitored, notes);
    }
}
