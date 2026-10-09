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
/// one identity. It only creates what is missing: an existing episode keeps its title, air date and numbers (an absolute number it lacks is filled in only where the slots name it unambiguously), the AniList entry and remote episode of a slot
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
        var known = existing.ToDictionary(episode => (episode.SeasonNumber, episode.EpisodeNumber));

        // An absolute number is evidence only where it names one slot: entries that each count from 1 would give several episodes the same number, and a number another
        // episode of the Work already holds (a mapping or manual correction) is never claimed twice. A held number is not rewritten either.
        var ambiguous = slots.Slots.Where(slot => slot.Key.AbsoluteEpisodeNumber is not null).GroupBy(slot => slot.Key.AbsoluteEpisodeNumber).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        var holders = existing.Where(episode => episode.AbsoluteNumber is not null).GroupBy(episode => episode.AbsoluteNumber!.Value).ToDictionary(group => group.Key, group => group.Select(episode => (episode.SeasonNumber, episode.EpisodeNumber)).ToHashSet());
        var missing = new List<(AnimeEpisodeSlot Slot, int? Absolute)>();
        var filled = 0;
        foreach (var slot in slots.Slots)
        {
            var place = (slot.Key.SeasonNumber, slot.Key.EpisodeNumber);
            var absolute = slot.Key.AbsoluteEpisodeNumber is { } number && !ambiguous.Contains(number) ? number : (int?)null;
            var heldElsewhere = absolute is { } wanted && holders.TryGetValue(wanted, out var held) && !held.Contains(place);
            if (known.TryGetValue(place, out var episode))
            {
                if (episode.AbsoluteNumber is null && absolute is { } fill && !heldElsewhere)
                {
                    episode.AbsoluteNumber = fill;
                    holders[fill] = [place];
                    filled++;
                }
            }
            else if (!heldElsewhere)
            {
                missing.Add((slot, absolute));
                if (absolute is { } claimed)
                {
                    holders[claimed] = [place];
                }
            }
        }

        var unlinked = existing.Where(episode => episode.SeasonId is null).ToArray();
        if (missing.Count == 0 && unlinked.Length == 0 && filled == 0)
        {
            return 0;
        }

        var seasonIds = await db.WorkSeasons.Where(season => season.WorkId == workId).ToDictionaryAsync(season => season.SeasonNumber, season => season.Id, cancellationToken);
        foreach (var number in missing.Select(item => item.Slot.Key.SeasonNumber).Concat(unlinked.Select(episode => episode.SeasonNumber)).Distinct().Where(number => !seasonIds.ContainsKey(number)))
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

        db.WorkEpisodes.AddRange(missing.Select(item => new WorkEpisode
        {
            WorkId = workId,
            SeasonId = seasonIds[item.Slot.Key.SeasonNumber],
            SeasonNumber = item.Slot.Key.SeasonNumber,
            EpisodeNumber = item.Slot.Key.EpisodeNumber,
            AbsoluteNumber = item.Absolute,
            IsSpecial = item.Slot.Key.SeasonNumber == 0
        }));
        await db.SaveChangesAsync(cancellationToken);
        return missing.Count;
    }

    /// <summary>The episodes of the Wanted queue (of one anime, or of all): missing ones, and installed ones the shared upgrade policy still wants better.</summary>
    public static async Task<IReadOnlyList<AnimeWantedEpisode>> WantedAsync(AppDbContext db, string? animeKey, CancellationToken cancellationToken)
    {
        var rows = await (
                from item in db.WantedItems.AsNoTracking()
                where item.TargetKind == WantedTargetKind.Episode
                join episode in db.WorkEpisodes.AsNoTracking() on item.TargetId equals episode.Id
                join link in db.WorkSourceLinks.AsNoTracking() on item.WorkId equals link.WorkId
                join anime in db.Anime.AsNoTracking() on link.SourceId equals anime.Id
                where link.SourceKind == WorkSourceKind.Anime && (animeKey == null || anime.Key == animeKey)
                select new
                {
                    anime.Key,
                    episode.SeasonNumber,
                    episode.EpisodeNumber,
                    episode.AbsoluteNumber,
                    item.CreatedAt,
                    Installed = db.MediaAssets.Any(asset => asset.WorkEpisodeId == episode.Id && asset.Kind == MediaAssetKind.Video && db.StoredFiles.Any(file => file.MediaAssetId == asset.Id))
                })
            .ToListAsync(cancellationToken);
        return
        [
            .. rows
                .OrderBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.SeasonNumber)
                .ThenBy(row => row.EpisodeNumber)
                .Select(row => new AnimeWantedEpisode(
                    new AnimeEpisodeKey(row.Key, row.SeasonNumber, row.EpisodeNumber, row.AbsoluteNumber),
                    row.Installed ? AnimeWantedReason.CutoffUnmet : AnimeWantedReason.Missing,
                    new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc))))
        ];
    }

    public async Task<Guid?> WorkOfAsync(string animeKey, CancellationToken cancellationToken) =>
        await (
                from link in db.WorkSourceLinks.AsNoTracking()
                join anime in db.Anime.AsNoTracking() on link.SourceId equals anime.Id
                where link.SourceKind == WorkSourceKind.Anime && anime.Key == animeKey
                select (Guid?)link.WorkId)
            .FirstOrDefaultAsync(cancellationToken);

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
