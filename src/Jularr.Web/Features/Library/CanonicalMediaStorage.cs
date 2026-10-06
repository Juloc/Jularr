using System.Collections.Concurrent;
using Jularr.Web.Data;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Library;

public enum MediaAssetKind
{
    Video = 0,
    Audio = 1,
    Ebook = 2,
    ComicArchive = 3,
    Subtitle = 4,
    Image = 5
}

/// <summary>Logical playable/readable artifact belonging to one imported WorkVersion.</summary>
public sealed class MediaAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkId { get; set; }
    public Guid? WorkEpisodeId { get; set; }

    /// <summary>The track of an album Work an Audio asset belongs to; null for every other asset.</summary>
    public Guid? WorkTrackId { get; set; }

    public Guid WorkVersionId { get; set; }
    public MediaAssetKind Kind { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Physical file owned by Library/Storage. EpisodeId is a temporary Anime migration bridge only;
/// canonical consumers resolve the logical target through MediaAsset.
/// </summary>
public sealed class StoredFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? MediaAssetId { get; set; }
    public Guid LibraryRootId { get; set; }
    public Guid? EpisodeId { get; set; }
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
}

public sealed record CanonicalVideoAttachment(
    Guid WorkId,
    Guid? WorkEpisodeId,
    string Path,
    string? StorageRootPath = null);

public sealed record CanonicalAudioAttachment(Guid WorkId, Guid WorkTrackId, string Path);

public sealed record CanonicalAudioFile(Guid MediaAssetId, Guid StoredFileId, Guid WorkTrackId, string Path);

public sealed record CanonicalPlayableFile(
    Guid MediaAssetId,
    Guid StoredFileId,
    Guid WorkId,
    Guid? WorkEpisodeId,
    Guid WorkVersionId,
    string Path,
    long SizeBytes,
    DateTime LastWriteTimeUtc);

/// <summary>
/// Single application owner for canonical video Asset/StoredFile identity. Batch attachment is used by
/// imports/backfill so a TV pack does not perform per-file database lookups.
/// </summary>
public sealed class CanonicalMediaStorageService(AppDbContext db)
{
    private const string LocalVideoVersionPrefix = "video-file:";
    private const string LocalAudioVersionPrefix = "audio-file:";

    public async Task<CanonicalPlayableFile> AttachVideoAsync(
        Guid workId,
        Guid? workEpisodeId,
        string path,
        string? storageRootPath,
        CancellationToken cancellationToken)
    {
        var rows = await AttachVideosAsync(
            [new CanonicalVideoAttachment(workId, workEpisodeId, path, storageRootPath)],
            cancellationToken);
        return rows[0];
    }

    public async Task<IReadOnlyList<CanonicalPlayableFile>> AttachVideosAsync(
        IReadOnlyCollection<CanonicalVideoAttachment> requested,
        CancellationToken cancellationToken)
    {
        if (requested.Count == 0)
        {
            return [];
        }

        var normalized = requested
            .Select(item =>
            {
                var fullPath = System.IO.Path.GetFullPath(item.Path);
                var rootPath = string.IsNullOrWhiteSpace(item.StorageRootPath)
                    ? System.IO.Path.GetDirectoryName(fullPath)
                    : System.IO.Path.GetFullPath(item.StorageRootPath);
                return item with { Path = fullPath, StorageRootPath = rootPath };
            })
            .ToArray();

        var conflictingPath = normalized
            .GroupBy(x => x.Path, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Select(x => (x.WorkId, x.WorkEpisodeId)).Distinct().Count() > 1);
        if (conflictingPath is not null)
        {
            throw new InvalidOperationException(
                $"Stored file '{conflictingPath.Key}' cannot target more than one canonical unit.");
        }

        normalized = normalized
            .GroupBy(x => x.Path, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        var workIds = normalized.Select(x => x.WorkId).Distinct().ToArray();
        var existingWorkIds = await db.Works
            .AsNoTracking()
            .Where(x => workIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        if (existingWorkIds.Count != workIds.Length)
        {
            throw new InvalidOperationException("A canonical video file must target an existing Work.");
        }

        var episodeIds = normalized
            .Where(x => x.WorkEpisodeId.HasValue)
            .Select(x => x.WorkEpisodeId!.Value)
            .Distinct()
            .ToArray();
        List<VideoEpisodeRow> episodeRows = episodeIds.Length == 0
            ? []
            : await db.WorkEpisodes
                .AsNoTracking()
                .Where(x => episodeIds.Contains(x.Id))
                .Select(x => new VideoEpisodeRow(x.Id, x.WorkId, x.SeasonNumber, x.EpisodeNumber))
                .ToListAsync(cancellationToken);
        var episodeById = episodeRows.ToDictionary(x => x.Id);
        foreach (var item in normalized.Where(x => x.WorkEpisodeId.HasValue))
        {
            if (!episodeById.TryGetValue(item.WorkEpisodeId!.Value, out var episode) ||
                episode.WorkId != item.WorkId)
            {
                throw new InvalidOperationException("The canonical episode does not belong to the requested Work.");
            }
        }

        var paths = normalized.Select(x => x.Path).ToArray();
        var storedByPath = (await db.StoredFiles
                .Where(x => paths.Contains(x.Path))
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Path, StringComparer.Ordinal);

        var roots = await db.LibraryRoots.ToListAsync(cancellationToken);
        var rootByPath = roots
            .GroupBy(x => NormalizeRoot(x.Path), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var item in normalized)
        {
            var info = new FileInfo(item.Path);
            if (!info.Exists)
            {
                throw new FileNotFoundException("Cannot attach a missing stored video file.", item.Path);
            }

            var root = ResolveStorageRoot(item.Path, roots) ??
                       EnsureStorageRoot(item, rootByPath, roots);

            if (!storedByPath.TryGetValue(item.Path, out var stored))
            {
                stored = new StoredFile
                {
                    LibraryRootId = root.Id,
                    Path = item.Path,
                    SizeBytes = info.Length,
                    LastWriteTimeUtc = info.LastWriteTimeUtc
                };
                db.StoredFiles.Add(stored);
                storedByPath.Add(item.Path, stored);
            }
            else
            {
                stored.LibraryRootId = root.Id;
                stored.SizeBytes = info.Length;
                stored.LastWriteTimeUtc = info.LastWriteTimeUtc;
            }
        }

        var versionKeys = storedByPath.Values
            .Select(x => BuildVersionKey(x.Id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var versions = await db.WorkVersions
            .Where(x => workIds.Contains(x.WorkId) && versionKeys.Contains(x.VersionKey))
            .ToListAsync(cancellationToken);
        var versionByKey = versions.ToDictionary(x => (x.WorkId, x.VersionKey));

        foreach (var item in normalized)
        {
            var stored = storedByPath[item.Path];
            var key = BuildVersionKey(stored.Id);
            var unitKey = item.WorkEpisodeId is { } episodeId
                ? BuildUnitKey(episodeById[episodeId])
                : null;

            if (!versionByKey.TryGetValue((item.WorkId, key), out var version))
            {
                version = new WorkVersion
                {
                    WorkId = item.WorkId,
                    VersionKey = key,
                    UnitKey = unitKey,
                    Source = "local"
                };
                db.WorkVersions.Add(version);
                versionByKey.Add((item.WorkId, key), version);
            }
            else
            {
                version.UnitKey = unitKey;
            }
        }

        var versionIds = versionByKey.Values.Select(x => x.Id).Distinct().ToArray();
        var assets = await db.MediaAssets
            .Where(x => versionIds.Contains(x.WorkVersionId) && x.Kind == MediaAssetKind.Video)
            .ToListAsync(cancellationToken);
        var assetByVersion = assets.ToDictionary(x => x.WorkVersionId);

        var result = new List<CanonicalPlayableFile>(normalized.Length);
        foreach (var item in normalized)
        {
            var stored = storedByPath[item.Path];
            var version = versionByKey[(item.WorkId, BuildVersionKey(stored.Id))];
            if (!assetByVersion.TryGetValue(version.Id, out var asset))
            {
                asset = new MediaAsset
                {
                    WorkId = item.WorkId,
                    WorkEpisodeId = item.WorkEpisodeId,
                    WorkVersionId = version.Id,
                    Kind = MediaAssetKind.Video
                };
                db.MediaAssets.Add(asset);
                assetByVersion.Add(version.Id, asset);
            }
            else
            {
                asset.WorkId = item.WorkId;
                asset.WorkEpisodeId = item.WorkEpisodeId;
            }

            stored.MediaAssetId = asset.Id;
            result.Add(new CanonicalPlayableFile(
                asset.Id,
                stored.Id,
                item.WorkId,
                item.WorkEpisodeId,
                version.Id,
                stored.Path,
                stored.SizeBytes,
                stored.LastWriteTimeUtc));
        }

        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Records placed audio files as canonical Audio assets of album tracks: one StoredFile, one WorkVersion and one MediaAsset per file, in a
    /// single save. Idempotent by path: recording a file again refreshes its row and keeps its asset. A file must lie inside a LibraryRoot (the
    /// importer placed it there) and a path never serves two tracks.
    /// </summary>
    public async Task<IReadOnlyList<CanonicalAudioFile>> AttachAudiosAsync(IReadOnlyCollection<CanonicalAudioAttachment> requested, CancellationToken cancellationToken)
    {
        if (requested.Count == 0)
        {
            return [];
        }

        var normalized = requested.Select(item => item with { Path = System.IO.Path.GetFullPath(item.Path) }).ToArray();
        if (normalized.GroupBy(item => item.Path, StringComparer.Ordinal).Any(group => group.Select(item => item.WorkTrackId).Distinct().Count() > 1))
        {
            throw new InvalidOperationException("A stored audio file cannot serve more than one track.");
        }

        var trackIds = normalized.Select(item => item.WorkTrackId).Distinct().ToArray();
        var tracks = await db.WorkTracks.AsNoTracking().Where(track => trackIds.Contains(track.Id)).ToDictionaryAsync(track => track.Id, cancellationToken);
        foreach (var item in normalized)
        {
            if (!tracks.TryGetValue(item.WorkTrackId, out var track) || track.WorkId != item.WorkId)
            {
                throw new InvalidOperationException("The canonical track does not belong to the album.");
            }
        }

        var paths = normalized.Select(item => item.Path).ToArray();
        var storedByPath = (await db.StoredFiles.Where(file => paths.Contains(file.Path)).ToListAsync(cancellationToken)).ToDictionary(file => file.Path, StringComparer.Ordinal);
        var roots = await db.LibraryRoots.ToListAsync(cancellationToken);
        foreach (var item in normalized)
        {
            var info = new FileInfo(item.Path);
            if (!info.Exists)
            {
                throw new FileNotFoundException("Cannot attach a missing stored audio file.", item.Path);
            }

            var root = ResolveStorageRoot(item.Path, roots) ?? throw new InvalidOperationException("A stored audio file has to lie inside a library root.");
            if (!storedByPath.TryGetValue(item.Path, out var stored))
            {
                stored = new StoredFile { LibraryRootId = root.Id, Path = item.Path };
                db.StoredFiles.Add(stored);
                storedByPath.Add(item.Path, stored);
            }

            stored.LibraryRootId = root.Id;
            stored.SizeBytes = info.Length;
            stored.LastWriteTimeUtc = info.LastWriteTimeUtc;
        }

        var workIds = normalized.Select(item => item.WorkId).Distinct().ToArray();
        var versionKeys = storedByPath.Values.Select(file => $"{LocalAudioVersionPrefix}{file.Id:N}").ToArray();
        var versionByKey = (await db.WorkVersions.Where(version => workIds.Contains(version.WorkId) && versionKeys.Contains(version.VersionKey)).ToListAsync(cancellationToken))
            .ToDictionary(version => (version.WorkId, version.VersionKey));
        foreach (var item in normalized)
        {
            var key = $"{LocalAudioVersionPrefix}{storedByPath[item.Path].Id:N}";
            var track = tracks[item.WorkTrackId];
            if (!versionByKey.ContainsKey((item.WorkId, key)))
            {
                var version = new WorkVersion { WorkId = item.WorkId, VersionKey = key, UnitKey = $"D{track.Disc:D2}T{track.Number:D2}", Source = "local" };
                db.WorkVersions.Add(version);
                versionByKey.Add((item.WorkId, key), version);
            }
        }

        var versionIds = versionByKey.Values.Select(version => version.Id).ToArray();
        var assetByVersion = (await db.MediaAssets.Where(asset => versionIds.Contains(asset.WorkVersionId) && asset.Kind == MediaAssetKind.Audio).ToListAsync(cancellationToken))
            .ToDictionary(asset => asset.WorkVersionId);
        var result = new List<CanonicalAudioFile>(normalized.Length);
        foreach (var item in normalized)
        {
            var stored = storedByPath[item.Path];
            var version = versionByKey[(item.WorkId, $"{LocalAudioVersionPrefix}{stored.Id:N}")];
            if (!assetByVersion.TryGetValue(version.Id, out var asset))
            {
                asset = new MediaAsset { WorkId = item.WorkId, WorkTrackId = item.WorkTrackId, WorkVersionId = version.Id, Kind = MediaAssetKind.Audio };
                db.MediaAssets.Add(asset);
                assetByVersion.Add(version.Id, asset);
            }

            asset.WorkTrackId = item.WorkTrackId;
            stored.MediaAssetId = asset.Id;
            result.Add(new CanonicalAudioFile(asset.Id, stored.Id, item.WorkTrackId, stored.Path));
        }

        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<CanonicalPlayableFile?> ResolveVideoAsync(
        Guid workId,
        Guid? workEpisodeId,
        CancellationToken cancellationToken) =>
        await (
            from asset in db.MediaAssets.AsNoTracking()
            join file in db.StoredFiles.AsNoTracking()
                on (Guid?)asset.Id equals file.MediaAssetId
            where asset.Kind == MediaAssetKind.Video &&
                  asset.WorkId == workId &&
                  asset.WorkEpisodeId == workEpisodeId
            orderby file.Path
            select new CanonicalPlayableFile(
                asset.Id,
                file.Id,
                asset.WorkId,
                asset.WorkEpisodeId,
                asset.WorkVersionId,
                file.Path,
                file.SizeBytes,
                file.LastWriteTimeUtc))
        .FirstOrDefaultAsync(cancellationToken);

    private static string BuildVersionKey(Guid storedFileId) =>
        $"{LocalVideoVersionPrefix}{storedFileId:N}";

    private static string BuildUnitKey(VideoEpisodeRow episode) =>
        $"S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}";

    private static string NormalizeRoot(string path) =>
        System.IO.Path.GetFullPath(path)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

    private static LibraryRoot? ResolveStorageRoot(string fullPath, IReadOnlyCollection<LibraryRoot> roots)
    {
        LibraryRoot? best = null;
        var bestLength = -1;
        foreach (var root in roots)
        {
            var rootPath = NormalizeRoot(root.Path);
            var prefix = rootPath + System.IO.Path.DirectorySeparatorChar;
            if ((string.Equals(fullPath, rootPath, StringComparison.Ordinal) ||
                 fullPath.StartsWith(prefix, StringComparison.Ordinal)) &&
                rootPath.Length > bestLength)
            {
                best = root;
                bestLength = rootPath.Length;
            }
        }

        return best;
    }

    private LibraryRoot EnsureStorageRoot(
        CanonicalVideoAttachment item,
        Dictionary<string, LibraryRoot> rootByPath,
        List<LibraryRoot> roots)
    {
        var requestedRoot = NormalizeRoot(
            item.StorageRootPath ?? System.IO.Path.GetDirectoryName(item.Path) ?? item.Path);
        if (rootByPath.TryGetValue(requestedRoot, out var existing))
        {
            return existing;
        }

        var suffix = System.IO.Path.GetFileName(requestedRoot);
        var name = string.IsNullOrWhiteSpace(suffix) ? "Imported media" : $"Imported media: {suffix}";
        if (name.Length > 120)
        {
            name = name[..120];
        }

        var root = new LibraryRoot
        {
            Name = name,
            Path = requestedRoot,
            IsEnabled = false
        };
        db.LibraryRoots.Add(root);
        roots.Add(root);
        rootByPath.Add(requestedRoot, root);
        return root;
    }

    private sealed record VideoEpisodeRow(Guid Id, Guid WorkId, int SeasonNumber, int EpisodeNumber);
}


/// <summary>
/// Idempotent bridge for legacy Anime records. Every legacy Anime and Episode gets its canonical Work,
/// WorkEpisode and source link whether or not a file exists, because the consumer Library derives
/// membership and missing units from the canonical structure. Files are attached to canonical
/// Asset/StoredFile identity afterwards. File/analysis ids are retained because the schema evolves the
/// old tables in place; only canonical WorkEpisode/Version/Asset relations are added.
/// </summary>
public sealed class CanonicalVideoStorageBackfillService(
    AppDbContext db,
    LegacyWorkBridge bridge,
    CanonicalMediaStorageService storage,
    ILogger<CanonicalVideoStorageBackfillService> logger)
{
    // A stale file stays pending until a scan removes it; warn once per process instead of on every scan.
    private static readonly ConcurrentDictionary<string, byte> WarnedMissingFiles = new(StringComparer.Ordinal);

    /// <returns>The number of legacy files attached to canonical video Assets.</returns>
    public async Task<int> BackfillLegacyAnimeAsync(Guid? libraryRootId, CancellationToken cancellationToken)
    {
        var pendingFiles = await db.StoredFiles
            .AsNoTracking()
            .Where(x => x.EpisodeId != null && x.MediaAssetId == null && (!libraryRootId.HasValue || x.LibraryRootId == libraryRootId.Value))
            .Select(x => new LegacyStoredFile(x.Path, x.EpisodeId!.Value))
            .ToListAsync(cancellationToken);
        var pendingEpisodeIds = pendingFiles.Select(x => x.LegacyEpisodeId).Distinct().ToArray();

        var episodes = await (
                from episode in db.Episodes.AsNoTracking()
                join anime in db.Anime.AsNoTracking() on episode.AnimeId equals anime.Id
                where pendingEpisodeIds.Contains(episode.Id) ||
                      !db.WorkSourceLinks.Any(link => link.SourceKind == WorkSourceKind.Episode && link.SourceId == episode.Id)
                select new LegacyAnimeEpisode(episode.Id, anime.Id, anime.Key, anime.Title, episode.SeasonNumber, episode.Number, episode.Title))
            .ToListAsync(cancellationToken);
        var animeWithoutWork = await db.Anime
            .AsNoTracking()
            .Where(anime => !db.WorkSourceLinks.Any(link => link.SourceKind == WorkSourceKind.Anime && link.SourceId == anime.Id))
            .Select(anime => new LegacyAnime(anime.Id, anime.Key, anime.Title))
            .ToListAsync(cancellationToken);

        if (episodes.Count == 0 && animeWithoutWork.Count == 0)
        {
            return 0;
        }

        var animeById = episodes
            .Select(x => new LegacyAnime(x.AnimeId, x.AnimeKey, x.AnimeTitle))
            .Concat(animeWithoutWork)
            .DistinctBy(x => x.Id)
            .ToDictionary(x => x.Id);
        var animeIds = animeById.Keys.ToArray();
        var workByAnime = await db.WorkSourceLinks
            .AsNoTracking()
            .Where(x => x.SourceKind == WorkSourceKind.Anime && animeIds.Contains(x.SourceId))
            .ToDictionaryAsync(x => x.SourceId, x => x.WorkId, cancellationToken);
        foreach (var anime in animeById.Values.Where(x => !workByAnime.ContainsKey(x.Id)))
        {
            workByAnime[anime.Id] = await bridge.EnsureWorkForAnimeAsync(
                new Anime { Id = anime.Id, Key = anime.Key, Title = anime.Title },
                cancellationToken);
        }

        var workIds = workByAnime.Values.Distinct().ToArray();
        var canonicalEpisodeByKey = (await db.WorkEpisodes
                .Where(x => workIds.Contains(x.WorkId))
                .ToListAsync(cancellationToken))
            .ToDictionary(x => (x.WorkId, x.SeasonNumber, x.EpisodeNumber));

        foreach (var episode in episodes.GroupBy(x => x.LegacyEpisodeId).Select(group => group.First()))
        {
            var workId = workByAnime[episode.AnimeId];
            var key = (workId, episode.SeasonNumber, episode.EpisodeNumber);
            if (canonicalEpisodeByKey.ContainsKey(key))
            {
                continue;
            }

            var canonical = new WorkEpisode
            {
                WorkId = workId,
                SeasonNumber = episode.SeasonNumber,
                EpisodeNumber = episode.EpisodeNumber,
                IsSpecial = episode.SeasonNumber == 0,
                Title = episode.EpisodeTitle
            };
            db.WorkEpisodes.Add(canonical);
            canonicalEpisodeByKey.Add(key, canonical);
        }

        var legacyEpisodeIds = episodes.Select(x => x.LegacyEpisodeId).Distinct().ToArray();
        var linkedEpisodeIds = (await db.WorkSourceLinks
                .AsNoTracking()
                .Where(x => x.SourceKind == WorkSourceKind.Episode && legacyEpisodeIds.Contains(x.SourceId))
                .Select(x => x.SourceId)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        foreach (var episode in episodes
                     .Where(x => !linkedEpisodeIds.Contains(x.LegacyEpisodeId))
                     .GroupBy(x => x.LegacyEpisodeId)
                     .Select(group => group.First()))
        {
            db.WorkSourceLinks.Add(new WorkSourceLink
            {
                WorkId = workByAnime[episode.AnimeId],
                SourceKind = WorkSourceKind.Episode,
                SourceId = episode.LegacyEpisodeId
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        var episodeById = episodes.GroupBy(x => x.LegacyEpisodeId).ToDictionary(group => group.Key, group => group.First());
        // A file on unmounted or removed media must not stop startup or the scan; it is attached once it is back.
        var attachments = pendingFiles
            .Where(file => episodeById.ContainsKey(file.LegacyEpisodeId) && IsFilePresent(file.Path))
            .Select(file =>
            {
                var episode = episodeById[file.LegacyEpisodeId];
                var workId = workByAnime[episode.AnimeId];
                return new CanonicalVideoAttachment(workId, canonicalEpisodeByKey[(workId, episode.SeasonNumber, episode.EpisodeNumber)].Id, file.Path);
            })
            .ToArray();

        await storage.AttachVideosAsync(attachments, cancellationToken);
        return attachments.Length;
    }

    private bool IsFilePresent(string path)
    {
        if (File.Exists(path))
        {
            return true;
        }

        if (WarnedMissingFiles.TryAdd(path, 0))
        {
            logger.LogWarning("Skipping the canonical video bridge for missing file {Path}; it is attached when the file is back or removed by a scan.", path);
        }

        return false;
    }

    private sealed record LegacyStoredFile(string Path, Guid LegacyEpisodeId);

    private sealed record LegacyAnime(Guid Id, string Key, string Title);

    private sealed record LegacyAnimeEpisode(
        Guid LegacyEpisodeId,
        Guid AnimeId,
        string AnimeKey,
        string AnimeTitle,
        int SeasonNumber,
        int EpisodeNumber,
        string EpisodeTitle);
}
