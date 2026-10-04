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
/// Idempotent bridge for legacy Anime files. File/analysis ids are retained because the schema evolves
/// the old tables in place; only canonical WorkEpisode/Version/Asset relations are added.
/// </summary>
public sealed class CanonicalVideoStorageBackfillService(
    AppDbContext db,
    LegacyWorkBridge bridge,
    CanonicalMediaStorageService storage)
{
    public async Task<int> BackfillLegacyAnimeAsync(
        Guid? libraryRootId,
        CancellationToken cancellationToken)
    {
        var candidates = await (
                from file in db.StoredFiles.AsNoTracking()
                where file.EpisodeId != null &&
                      file.MediaAssetId == null &&
                      (!libraryRootId.HasValue || file.LibraryRootId == libraryRootId.Value)
                join episode in db.Episodes.AsNoTracking()
                    on file.EpisodeId!.Value equals episode.Id
                join anime in db.Anime.AsNoTracking()
                    on episode.AnimeId equals anime.Id
                select new LegacyAnimeStoredFile(
                    file.Path,
                    episode.Id,
                    episode.AnimeId,
                    anime.Key,
                    anime.Title,
                    episode.SeasonNumber,
                    episode.Number,
                    episode.Title))
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var workByAnime = new Dictionary<Guid, Guid>();
        foreach (var anime in candidates
                     .GroupBy(x => x.AnimeId)
                     .Select(group => group.First()))
        {
            workByAnime[anime.AnimeId] = await bridge.EnsureWorkForAnimeAsync(
                new Anime { Id = anime.AnimeId, Key = anime.AnimeKey, Title = anime.AnimeTitle },
                cancellationToken);
        }

        var workIds = workByAnime.Values.Distinct().ToArray();
        var existingEpisodes = await db.WorkEpisodes
            .Where(x => workIds.Contains(x.WorkId))
            .ToListAsync(cancellationToken);
        var canonicalEpisodeByKey = existingEpisodes.ToDictionary(
            x => (x.WorkId, x.SeasonNumber, x.EpisodeNumber));

        foreach (var candidate in candidates
                     .GroupBy(x => x.LegacyEpisodeId)
                     .Select(group => group.First()))
        {
            var workId = workByAnime[candidate.AnimeId];
            var key = (workId, candidate.SeasonNumber, candidate.EpisodeNumber);
            if (canonicalEpisodeByKey.ContainsKey(key))
            {
                continue;
            }

            var episode = new WorkEpisode
            {
                WorkId = workId,
                SeasonNumber = candidate.SeasonNumber,
                EpisodeNumber = candidate.EpisodeNumber,
                IsSpecial = candidate.SeasonNumber == 0,
                Title = candidate.EpisodeTitle
            };
            db.WorkEpisodes.Add(episode);
            canonicalEpisodeByKey.Add(key, episode);
        }

        var legacyEpisodeIds = candidates.Select(x => x.LegacyEpisodeId).Distinct().ToArray();
        var linkedEpisodeIds = await db.WorkSourceLinks
            .AsNoTracking()
            .Where(x => x.SourceKind == WorkSourceKind.Episode && legacyEpisodeIds.Contains(x.SourceId))
            .Select(x => x.SourceId)
            .ToListAsync(cancellationToken);
        var linkedSet = linkedEpisodeIds.ToHashSet();
        foreach (var candidate in candidates
                     .Where(x => !linkedSet.Contains(x.LegacyEpisodeId))
                     .GroupBy(x => x.LegacyEpisodeId)
                     .Select(group => group.First()))
        {
            db.WorkSourceLinks.Add(new WorkSourceLink
            {
                WorkId = workByAnime[candidate.AnimeId],
                SourceKind = WorkSourceKind.Episode,
                SourceId = candidate.LegacyEpisodeId
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        var attachments = candidates.Select(candidate =>
        {
            var workId = workByAnime[candidate.AnimeId];
            var episode = canonicalEpisodeByKey[(workId, candidate.SeasonNumber, candidate.EpisodeNumber)];
            return new CanonicalVideoAttachment(workId, episode.Id, candidate.Path);
        }).ToArray();

        await storage.AttachVideosAsync(attachments, cancellationToken);
        return attachments.Length;
    }

    private sealed record LegacyAnimeStoredFile(
        string Path,
        Guid LegacyEpisodeId,
        Guid AnimeId,
        string AnimeKey,
        string AnimeTitle,
        int SeasonNumber,
        int EpisodeNumber,
        string EpisodeTitle);
}
