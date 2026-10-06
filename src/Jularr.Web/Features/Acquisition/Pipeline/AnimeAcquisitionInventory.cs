using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Pipeline;

public sealed record AnimeAcquisitionAnime(
    Guid Id,
    string Key,
    string Title);

// One local episode slot: either a file exists in the library or the AniList mapping says the
// episode is expected. SearchTitle/SearchAliases are the titles of the AniList entry the local
// episode maps to, so season 2 of a split series is searched by its own AniList title while the
// absolute number stays the AniList episode number.
public sealed record AnimeAcquisitionEpisode(
    AnimeEpisodeKey Key,
    string? FilePath,
    long? FileSizeBytes,
    string SearchTitle,
    IReadOnlyList<string> SearchAliases)
{
    public bool HasFile => FilePath is not null;
}

public sealed record AnimeAcquisitionTarget(
    AnimeAcquisitionAnime Anime,
    AnimeQualityProfile Profile,
    IReadOnlyList<AnimeAcquisitionEpisode> Episodes,
    string? Diagnostic)
{
    public AnimeAcquisitionEpisode? Find(int seasonNumber, int episodeNumber) =>
        Episodes.FirstOrDefault(episode =>
            episode.Key.SeasonNumber == seasonNumber &&
            episode.Key.EpisodeNumber == episodeNumber);

    public IReadOnlyList<string> AllTitles =>
        Episodes
            .SelectMany(episode => new[] { episode.SearchTitle }.Concat(episode.SearchAliases))
            .Append(Anime.Title)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

// One episode slot without titles. SourceExternalId is the AniList entry the slot is expected from and Key.AbsoluteEpisodeNumber
// its episode number there; both are null for a local episode nothing expected.
public sealed record AnimeEpisodeSlot(
    AnimeEpisodeKey Key,
    bool HasFile,
    string? SourceExternalId);

// ExpectedEpisodesUnknown: the series expects no episodes at all (an AniList entry without an episode count, several local seasons without
// mappings), so the slots are only what the library has and the pipeline searches nothing.
public sealed record AnimeEpisodeSlots(
    AnimeAcquisitionAnime Anime,
    string? MatchedExternalId,
    string? MatchedStatus,
    IReadOnlyList<AnimeEpisodeSlot> Slots,
    bool ExpectedEpisodesUnknown);

// Where imported files of an anime go: the library root and series folder its existing files
// live in. AnimeDirectory is null when the anime has no folder in any library root yet.
public sealed record AnimeLibraryLocation(
    Guid RootId,
    string RootPath,
    string? AnimeDirectory,
    bool UsesSeasonFolders);

/// <summary>
/// Builds the per-anime episode inventory the monitoring engine and the import planner work on
/// from the canonical library (episodes/files), AniList match and AniList episode-range mappings.
/// </summary>
public sealed class AnimeAcquisitionInventory(
    AppDbContext db,
    AnimeMetadataService metadata,
    AnimeQualityProfileStore profiles,
    IEnumerable<IAnimeMetadataProvider> providers,
    ILogger<AnimeAcquisitionInventory> logger)
{
    private readonly Dictionary<string, IReadOnlyList<string>> titleCache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<AnimeAcquisitionTarget?> LoadAsync(
        string animeKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(animeKey);

        var layout = await ReadLayoutAsync(animeKey, cancellationToken);
        if (layout is null)
        {
            return null;
        }

        var profile = await profiles.ResolveAsync(layout.Anime.Id, cancellationToken);
        var primaryTitles = PrimaryTitles(layout.Anime, layout.Match);
        var episodes = new List<AnimeAcquisitionEpisode>();
        foreach (var slot in MergeSlots(layout))
        {
            var titles = slot.Source is { } source
                ? await TitlesForAsync(source.Provider, source.ExternalId, source.PreferredTitle, layout.Match, primaryTitles, cancellationToken)
                : primaryTitles;
            episodes.Add(new AnimeAcquisitionEpisode(slot.Key, slot.FilePath, slot.FileSizeBytes, titles[0], titles));
        }

        return new AnimeAcquisitionTarget(new AnimeAcquisitionAnime(layout.Anime.Id, layout.Anime.Key, layout.Anime.Title), profile, episodes, layout.Diagnostic);
    }

    /// <summary>
    /// The same episode slots as <see cref="LoadAsync"/> (one rule for what a series is expected to have), without the titles:
    /// reading them needs no provider call, so it is cheap enough to repeat for every open request.
    /// </summary>
    public async Task<AnimeEpisodeSlots?> LoadSlotsAsync(string animeKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(animeKey);

        var layout = await ReadLayoutAsync(animeKey, cancellationToken);
        if (layout is null)
        {
            return null;
        }

        var slots = MergeSlots(layout).Select(slot => new AnimeEpisodeSlot(slot.Key, slot.FilePath is not null, slot.Source?.ExternalId)).ToArray();
        return new AnimeEpisodeSlots(new AnimeAcquisitionAnime(layout.Anime.Id, layout.Anime.Key, layout.Anime.Title), layout.Match?.ExternalId, layout.Match?.Status, slots, layout.Diagnostic is not null);
    }

    /// <summary>
    /// The expected slots with the local episodes laid over them, ordered by season and episode: an expected slot keeps the AniList entry
    /// and absolute number it comes from, and a local episode nothing expected stands for itself.
    /// </summary>
    private static IReadOnlyList<MergedSlot> MergeSlots(InventoryLayout layout)
    {
        var key = layout.Anime.Key;
        var slots = new Dictionary<(int Season, int Episode), MergedSlot>();
        foreach (var (slot, source) in layout.Expected)
        {
            slots[slot] = new MergedSlot(new AnimeEpisodeKey(key, slot.Season, slot.Episode, source.Absolute), null, null, source);
        }

        foreach (var episode in layout.Local)
        {
            var slot = (episode.SeasonNumber, episode.Number);
            var known = slots.TryGetValue(slot, out var planned) ? planned : new MergedSlot(new AnimeEpisodeKey(key, episode.SeasonNumber, episode.Number), null, null, null);
            slots[slot] = known with { FilePath = episode.FilePath, FileSizeBytes = episode.FilePath is null ? null : episode.SizeBytes };
        }

        return slots.Values.OrderBy(slot => slot.Key.SeasonNumber).ThenBy(slot => slot.Key.EpisodeNumber).ToArray();
    }

    /// <summary>
    /// The one reading of what an anime has and is expected to have: its local episodes (with files), its AniList match and
    /// episode-range mappings, and for each expected slot the AniList entry and episode number it comes from.
    /// </summary>
    private async Task<InventoryLayout?> ReadLayoutAsync(string animeKey, CancellationToken cancellationToken)
    {
        var anime = await db.Anime
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Key == animeKey, cancellationToken);
        if (anime is null)
        {
            return null;
        }

        var local = await db.Episodes
            .AsNoTracking()
            .Where(episode => episode.AnimeId == anime.Id)
            .Select(episode => new LocalEpisode(
                episode.SeasonNumber,
                episode.Number,
                db.MediaFiles
                    .Where(file => file.EpisodeId == episode.Id)
                    .OrderBy(file => file.Path)
                    .Select(file => file.Path)
                    .FirstOrDefault(),
                db.MediaFiles
                    .Where(file => file.EpisodeId == episode.Id)
                    .OrderBy(file => file.Path)
                    .Select(file => (long?)file.SizeBytes)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);

        var match = await metadata.GetAsync(anime.Id, cancellationToken);
        var mappings = await metadata.GetEpisodeMappingsAsync(anime.Id, cancellationToken);

        var expected = new Dictionary<(int Season, int Episode), ExpectedSlot>();
        string? diagnostic = null;

        if (mappings.Count > 0)
        {
            // Explicit ranges first, then ranges extended to the AniList episode count, so an
            // explicit mapping always wins over the extension of a neighbouring one.
            foreach (var mapping in mappings)
            {
                for (var number = mapping.LocalEpisodeStart; number <= mapping.LocalEpisodeEnd; number++)
                {
                    expected[(mapping.SeasonNumber, number)] = new ExpectedSlot(mapping.ResolveRemoteEpisode(number), mapping.Provider, mapping.ExternalId, mapping.PreferredTitle);
                }
            }

            foreach (var mapping in mappings)
            {
                if (mapping.EpisodeCount is not > 0)
                {
                    continue;
                }

                var end = mapping.LocalEpisodeStart + (mapping.EpisodeCount.Value - mapping.RemoteEpisodeStart);
                for (var number = mapping.LocalEpisodeEnd + 1; number <= end; number++)
                {
                    expected.TryAdd((mapping.SeasonNumber, number), new ExpectedSlot(mapping.ResolveRemoteEpisode(number), mapping.Provider, mapping.ExternalId, mapping.PreferredTitle));
                }
            }
        }
        else if (match is not null)
        {
            var seasons = local.Select(episode => episode.SeasonNumber).Distinct().ToArray();
            if (match.EpisodeCount is > 0 && seasons.Length <= 1)
            {
                var season = seasons.Length == 1 ? seasons[0] : 1;
                for (var number = 1; number <= match.EpisodeCount.Value; number++)
                {
                    expected[(season, number)] = new ExpectedSlot(number, match.Provider, match.ExternalId, match.PreferredTitle);
                }
            }
            else
            {
                diagnostic = seasons.Length > 1
                    ? "Several local seasons without AniList episode mappings; map each season on the anime page before monitoring."
                    : "The AniList entry has no episode count yet, so only existing episodes are tracked.";
            }
        }
        else
        {
            diagnostic = "No AniList match; nothing is expected until the anime is matched.";
        }

        return new InventoryLayout(anime, match, local, expected, diagnostic);
    }

    // preferredRootId (the anime's assigned target root, item 3 of the P1 backlog) is used only
    // when the anime has no existing folder in any library root yet: an anime that is already
    // organized under a root keeps importing there, so switching the assignment never splits an
    // anime's episodes across two roots.
    public async Task<AnimeLibraryLocation?> GetLibraryLocationAsync(
        Guid animeId,
        CancellationToken cancellationToken,
        Guid? preferredRootId = null)
    {
        var files = await db.MediaFiles
            .AsNoTracking()
            .Where(file => db.Episodes.Any(episode => episode.Id == file.EpisodeId && episode.AnimeId == animeId))
            .Select(file => new { file.LibraryRootId, file.Path })
            .ToListAsync(cancellationToken);

        var roots = await db.LibraryRoots
            .AsNoTracking()
            .Where(root => root.IsEnabled)
            .OrderBy(root => root.CreatedAt)
            .ToListAsync(cancellationToken);

        foreach (var group in files.GroupBy(file => file.LibraryRootId).OrderByDescending(group => group.Count()))
        {
            var root = roots.FirstOrDefault(item => item.Id == group.Key);
            if (root is null)
            {
                continue;
            }

            var rootPath = Path.GetFullPath(root.Path);
            var directories = group
                .Select(file => AnimeDirectoryOf(rootPath, file.Path))
                .Where(directory => directory is not null)
                .GroupBy(directory => directory!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(item => item.Count())
                .Select(item => item.Key)
                .ToArray();

            if (directories.Length == 0)
            {
                continue;
            }

            var usesSeasonFolders = group.Any(file =>
                Path.GetDirectoryName(file.Path) is { } parent &&
                !parent.Equals(directories[0], StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(parent).StartsWith("Season", StringComparison.OrdinalIgnoreCase));

            return new AnimeLibraryLocation(root.Id, rootPath, directories[0], usesSeasonFolders);
        }

        var preferred = preferredRootId is { } id ? roots.FirstOrDefault(root => root.Id == id) : null;
        var fallback = preferred ?? roots.FirstOrDefault();
        return fallback is null
            ? null
            : new AnimeLibraryLocation(fallback.Id, Path.GetFullPath(fallback.Path), null, false);
    }

    // Converts one slot into the monitoring engine's inventory. A file whose quality cannot be
    // parsed is never offered for upgrades because the profile could not compare it safely.
    public static AnimeEpisodeInventory ToInventory(
        AnimeAcquisitionEpisode episode,
        AnimeQualityProfile profile)
    {
        AnimeReleaseScoreResult? current = null;
        if (episode.FilePath is not null)
        {
            var release = AnimeReleaseParser.Parse(Path.GetFileName(episode.FilePath));
            if (release.Resolution is > 0 || release.Source != AnimeReleaseSource.Unknown)
            {
                current = AnimeReleaseScorer.Score(
                    profile,
                    new AnimeReleaseCandidate(release, episode.FileSizeBytes));
            }
        }

        return new AnimeEpisodeInventory(episode.Key, null, episode.HasFile, current);
    }

    private static IReadOnlyList<string> PrimaryTitles(Anime anime, AnimeMetadata? match) =>
        Distinct(
            match?.PreferredTitle,
            match?.RomajiTitle,
            match?.EnglishTitle,
            match?.NativeTitle,
            anime.Title);

    private async Task<IReadOnlyList<string>> TitlesForAsync(
        string provider,
        string externalId,
        string preferredTitle,
        AnimeMetadata? match,
        IReadOnlyList<string> primaryTitles,
        CancellationToken cancellationToken)
    {
        if (match is not null &&
            match.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) &&
            match.ExternalId.Equals(externalId, StringComparison.OrdinalIgnoreCase))
        {
            return primaryTitles;
        }

        var cacheKey = $"{provider}:{externalId}";
        if (titleCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        IReadOnlyList<string> titles = Distinct(preferredTitle);
        var source = providers.FirstOrDefault(item => item.Key.Equals(provider, StringComparison.OrdinalIgnoreCase));
        if (source is not null)
        {
            try
            {
                var candidate = await source.GetAsync(externalId, cancellationToken);
                if (candidate is not null)
                {
                    titles = Distinct(
                        preferredTitle,
                        candidate.PreferredTitle,
                        candidate.RomajiTitle,
                        candidate.EnglishTitle,
                        candidate.NativeTitle);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is MetadataProviderException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Could not load alternative titles for {Provider}:{ExternalId}; searching with the mapped title only.",
                    provider,
                    externalId);
            }
        }

        titleCache[cacheKey] = titles;
        return titles;
    }

    private static string[] Distinct(params string?[] values)
    {
        var result = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return result.Length == 0 ? ["Unknown"] : result;
    }

    private static string? AnimeDirectoryOf(string rootPath, string mediaPath)
    {
        var relative = Path.GetRelativePath(rootPath, mediaPath);
        var parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return parts.Length < 2 || relative.StartsWith("..", StringComparison.Ordinal)
            ? null
            : Path.GetFullPath(Path.Combine(rootPath, parts[0]));
    }

    private sealed record LocalEpisode(
        int SeasonNumber,
        int Number,
        string? FilePath,
        long? SizeBytes);

    private sealed record MergedSlot(
        AnimeEpisodeKey Key,
        string? FilePath,
        long? FileSizeBytes,
        ExpectedSlot? Source);

    private sealed record ExpectedSlot(
        int? Absolute,
        string Provider,
        string ExternalId,
        string PreferredTitle);

    private sealed record InventoryLayout(
        Anime Anime,
        AnimeMetadata? Match,
        IReadOnlyList<LocalEpisode> Local,
        IReadOnlyDictionary<(int Season, int Episode), ExpectedSlot> Expected,
        string? Diagnostic);
}
