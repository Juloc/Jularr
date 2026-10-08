using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Pipeline;

// When the canonical episodes of the monitored anime were last completed; the slots only change with metadata and mappings, so a pass need not repeat it.
public sealed class AnimeCanonicalEpisodesState
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private long lastTicks;

    public bool IsDue(DateTime nowUtc) => nowUtc.Ticks - Interlocked.Read(ref lastTicks) >= Interval.Ticks;

    public void Mark(DateTime nowUtc) => Interlocked.Exchange(ref lastTicks, nowUtc.Ticks);
}

/// <summary>
/// Gives every episode slot an anime is expected to have (its local episodes, its AniList match and episode-range mappings, read by
/// <see cref="AnimeAcquisitionInventory"/>) a canonical <see cref="WorkEpisode"/>, so Monitoring, Wanted, coverage and upgrades address anime episodes by
/// one identity. It only creates what is missing: an existing episode keeps its title, air date and numbers, the AniList entry and remote episode of a slot
/// stay in the mapping evidence (a Jularr season is never an AniList entry), and an anime whose slots are unknown (no match, several local seasons without
/// mappings) gets nothing until the owner maps it.
/// </summary>
public sealed class AnimeCanonicalEpisodes(AppDbContext db, AnimeAcquisitionInventory inventory, AnimeMonitoring monitoring, LegacyWorkBridge bridge)
{
    public async Task<int> EnsureAsync(string animeKey, CancellationToken cancellationToken)
    {
        if (await inventory.LoadSlotsAsync(animeKey, cancellationToken) is not { Slots.Count: > 0 } slots)
        {
            return 0;
        }

        var workId = await db.WorkSourceLinks.AsNoTracking()
            .Where(link => link.SourceKind == WorkSourceKind.Anime && link.SourceId == slots.Anime.Id)
            .Select(link => (Guid?)link.WorkId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? await bridge.EnsureWorkForAnimeAsync(new Anime { Id = slots.Anime.Id, Key = slots.Anime.Key, Title = slots.Anime.Title }, cancellationToken);
        var existing = await db.WorkEpisodes.Where(episode => episode.WorkId == workId).ToListAsync(cancellationToken);
        var known = existing.Select(episode => (episode.SeasonNumber, episode.EpisodeNumber)).ToHashSet();
        var missing = slots.Slots.Where(slot => !known.Contains((slot.Key.SeasonNumber, slot.Key.EpisodeNumber))).ToArray();
        var unlinked = existing.Where(episode => episode.SeasonId is null).ToArray();
        if (missing.Length == 0 && unlinked.Length == 0)
        {
            return 0;
        }

        var seasonIds = await db.WorkSeasons.Where(season => season.WorkId == workId).ToDictionaryAsync(season => season.SeasonNumber, season => season.Id, cancellationToken);
        foreach (var number in missing.Select(slot => slot.Key.SeasonNumber).Concat(unlinked.Select(episode => episode.SeasonNumber)).Distinct().Where(number => !seasonIds.ContainsKey(number)))
        {
            var season = new WorkSeason { WorkId = workId, SeasonNumber = number, IsSpecial = number == 0 };
            db.WorkSeasons.Add(season);
            seasonIds[number] = season.Id;
        }

        // An episode that only carries its season number joins the season, so the season's decision reaches it.
        foreach (var episode in unlinked)
        {
            episode.SeasonId = seasonIds[episode.SeasonNumber];
        }

        db.WorkEpisodes.AddRange(missing.Select(slot => new WorkEpisode
        {
            WorkId = workId,
            SeasonId = seasonIds[slot.Key.SeasonNumber],
            SeasonNumber = slot.Key.SeasonNumber,
            EpisodeNumber = slot.Key.EpisodeNumber,
            IsSpecial = slot.Key.SeasonNumber == 0
        }));
        await db.SaveChangesAsync(cancellationToken);
        return missing.Length;
    }

    // The monitored anime, each completed once per interval.
    public async Task<int> EnsureMonitoredAsync(CancellationToken cancellationToken)
    {
        var created = 0;
        foreach (var key in await monitoring.MonitoredKeysAsync(cancellationToken))
        {
            created += await EnsureAsync(key, cancellationToken);
        }

        return created;
    }
}

/// <summary>The part of the shared Wanted pass that keeps the canonical episodes of monitored anime complete before anything reads them.</summary>
public sealed class AnimeEpisodesWantedSource(AnimeCanonicalEpisodes episodes, AnimeCanonicalEpisodesState state) : IWantedSource
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Anime;

    public async Task<int> PrepareAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (!state.IsDue(nowUtc))
        {
            return 0;
        }

        await episodes.EnsureMonitoredAsync(cancellationToken);
        state.Mark(nowUtc);
        return 0;
    }
}

/// <summary>Anime episodes: the installed quality of each canonical episode against the profile of the anime (assigned by its legacy anime id, else the Anime default).</summary>
public sealed class AnimeUpgradeAssessor(AppDbContext db, InstalledVideoVersions installed, QualityProfileStore profiles) : IUpgradeAssessor
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Anime;

    public WantedTargetKind TargetKind => WantedTargetKind.Episode;

    public async Task<IReadOnlyList<HeldTarget>> UpgradableAsync(Guid workId, IReadOnlyList<HeldTarget> held, CancellationToken cancellationToken)
    {
        var animeId = await db.WorkSourceLinks.AsNoTracking()
            .Where(link => link.SourceKind == WorkSourceKind.Anime && link.WorkId == workId)
            .Select(link => (Guid?)link.SourceId)
            .FirstOrDefaultAsync(cancellationToken);
        var profile = await profiles.ResolveAsync(animeId, cancellationToken);
        var qualities = await installed.BestQualityByEpisodeAsync(MediaAcquisitionKind.Anime, workId, profile, cancellationToken);
        return [.. held.Where(target => qualities.TryGetValue(target.TargetId, out var quality) && UpgradePolicy.Assess(profile, quality).IsUpgradable)];
    }
}
