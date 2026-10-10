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
    public long WorkId { get; set; }
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

/// <param name="Quality">The quality key of the release the file came from (see <c>ReleaseQuality</c>); it is what the upgrade policy compares later. Null keeps what the Version already says.</param>
public sealed record CanonicalVideoAttachment(
    long WorkId,
    Guid? WorkEpisodeId,
    string Path,
    string? StorageRootPath = null,
    string? Quality = null);

public sealed record CanonicalAudioAttachment(long WorkId, Guid WorkTrackId, string Path, string? Quality = null);

/// <summary>One video file of a Work as the canonical Version/Asset/File chain holds it; <see cref="Quality"/> is the Version's recorded quality, null when none was recorded.</summary>
public sealed record InstalledVideoFile(Guid StoredFileId, Guid? WorkEpisodeId, string Path, string? Quality);

/// <summary>One audio file of an album Work as the canonical Version/Asset/File chain holds it; <see cref="Quality"/> is the Version's recorded quality, null when none was recorded.</summary>
public sealed record InstalledAudioFile(Guid StoredFileId, Guid WorkTrackId, string Path, string? Quality);

public sealed record CanonicalAudioFile(Guid MediaAssetId, Guid StoredFileId, Guid WorkTrackId, string Path);

public sealed record CanonicalPlayableFile(
    Guid MediaAssetId,
    Guid StoredFileId,
    long WorkId,
    Guid? WorkEpisodeId,
    Guid WorkVersionId,
    string Path,
    long SizeBytes,
    DateTime LastWriteTimeUtc,
    [property: System.Text.Json.Serialization.JsonIgnore] string? VersionSource = null,
    [property: System.Text.Json.Serialization.JsonIgnore] string? VersionNotes = null);

/// <summary>
/// Single application owner for canonical video Asset/StoredFile identity. Batch attachment is used by
/// imports/backfill so a TV pack does not perform per-file database lookups.
/// </summary>
public sealed class CanonicalMediaStorageService(AppDbContext db)
{
    // Every recipe version is a disposable playback derivative, never a canonical original.
    // A new recipe must be explicitly approved by the playback eligibility verifier, but it
    // must already be excluded from installed releases and original fallback.
    public const string PreparedVideoVersionPrefix = "jularr-prepared:";
    public const string PreparedVideoVersionSource = PreparedVideoVersionPrefix + "v1";

    public static bool IsPreparedVideoSource(string? source) =>
        source?.StartsWith(PreparedVideoVersionPrefix, StringComparison.Ordinal) == true;
    private const string LocalVideoVersionPrefix = "video-file:";
    private const string LocalAudioVersionPrefix = "audio-file:";

    public async Task<CanonicalPlayableFile> AttachVideoAsync(
        long workId,
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

            if (item.Quality is { Length: > 0 } quality)
            {
                version.Quality = quality;
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
            if (!versionByKey.TryGetValue((item.WorkId, key), out var version))
            {
                version = new WorkVersion { WorkId = item.WorkId, VersionKey = key, UnitKey = $"D{track.Disc:D2}T{track.Number:D2}", Source = "local" };
                db.WorkVersions.Add(version);
                versionByKey.Add((item.WorkId, key), version);
            }

            if (item.Quality is { Length: > 0 } quality)
            {
                version.Quality = quality;
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

    /// <summary>
    /// Publishes a completed, probed, source-linked derivative through the canonical Version/Asset/File
    /// owner in one database commit. The caller must use the Maintenance operation for encoding and
    /// place only finished output inside Jularr's managed prepared-cache root before calling this.
    /// Nothing is visible during conversion; a failed verification never creates any Version.
    /// </summary>
    public async Task<CanonicalPlayableFile> PublishVerifiedPreparedVideoAsync(
        CanonicalPlayableFile source,
        MediaInventoryEntry sourceAnalysis,
        string preparedPath,
        string managedCacheRoot,
        string recipeKey,
        IMediaProbeRunner probeRunner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceAnalysis);
        ArgumentNullException.ThrowIfNull(probeRunner);
        if (string.IsNullOrWhiteSpace(recipeKey) || recipeKey.Length > 32 ||
            recipeKey.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("The recipe identifier must be a short alphanumeric key.", nameof(recipeKey));
        }

        var root = NormalizeRoot(managedCacheRoot);
        if (root.Length < 2 || string.Equals(
                root, NormalizeRoot(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(managedCacheRoot))!),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The prepared cache cannot use a filesystem root.");
        }

        var path = System.IO.Path.GetFullPath(preparedPath);
        if (!path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !string.Equals(System.IO.Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A prepared file must stay inside the managed cache.");
        }

        var sourceVersion = await db.WorkVersions.AsNoTracking()
            .SingleOrDefaultAsync(version => version.Id == source.WorkVersionId &&
                                             version.WorkId == source.WorkId, cancellationToken);
        var sourceFile = await db.StoredFiles.AsNoTracking()
            .SingleOrDefaultAsync(file => file.Id == source.StoredFileId &&
                                           file.Path == source.Path, cancellationToken);
        if (sourceVersion is null || sourceFile is null ||
            sourceVersion.Source == PreparedVideoVersionSource ||
            sourceFile.SizeBytes != source.SizeBytes ||
            sourceFile.LastWriteTimeUtc != source.LastWriteTimeUtc ||
            sourceAnalysis.MediaFileId != source.StoredFileId ||
            sourceAnalysis.Status != MediaAnalysisStatus.Succeeded ||
            sourceAnalysis.ProbeVersion != MediaInventoryService.CurrentProbeVersion ||
            sourceAnalysis.SourceSizeBytes != source.SizeBytes ||
            sourceAnalysis.SourceLastWriteTimeUtc != source.LastWriteTimeUtc ||
            sourceAnalysis.SourceFingerprint is not { Length: 64 } sourceFingerprint ||
            sourceAnalysis.Technical is not { } sourceTechnical)
        {
            throw new InvalidOperationException("The prepared file does not have a current canonical source.");
        }

        var original = new FileInfo(source.Path);
        var output = new FileInfo(path);
        if (!original.Exists || original.Length != source.SizeBytes ||
            Math.Abs((original.LastWriteTimeUtc - source.LastWriteTimeUtc).Ticks) >= 10 ||
            !output.Exists || output.Length == 0 ||
            string.Equals(source.Path, path, StringComparison.Ordinal) ||
            !string.Equals(
                await MediaInventoryService.TryComputeFingerprintAsync(source.Path, cancellationToken),
                sourceFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The original or prepared output is missing or changed.");
        }

        var probe = await probeRunner.ProbeAsync(path, cancellationToken);
        if (probe.Status != MediaProbeRunStatus.Completed)
        {
            throw new InvalidDataException("The prepared output did not pass ffprobe.");
        }

        MediaTechnicalInfo preparedTechnical;
        try
        {
            preparedTechnical = MediaProbeParser.Parse(probe.Output);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new InvalidDataException("The prepared output probe was invalid.", exception);
        }

        var sourceTracks = sourceTechnical.Streams.Where(track =>
            track.Kind is MediaTrackKind.Audio or MediaTrackKind.Subtitle).ToArray();
        var outputTracks = preparedTechnical.Streams.Where(track =>
            track.Kind is MediaTrackKind.Audio or MediaTrackKind.Subtitle).ToArray();
        if (sourceTechnical.DurationSeconds is not > 0 ||
            preparedTechnical.DurationSeconds is not > 0 ||
            Math.Abs(sourceTechnical.DurationSeconds.Value - preparedTechnical.DurationSeconds.Value) > 0.25 ||
            sourceTechnical.Video is not { Width: > 0, Height: > 0 } sourceVideo ||
            preparedTechnical.Video is not { Width: > 0, Height: > 0 } outputVideo ||
            !string.Equals(sourceVideo.DynamicRange, outputVideo.DynamicRange, StringComparison.OrdinalIgnoreCase) ||
            Math.Abs((double)sourceVideo.Width.Value / sourceVideo.Height.Value -
                     (double)outputVideo.Width.Value / outputVideo.Height.Value) > 0.02 ||
            sourceTracks.Length != outputTracks.Length ||
            !sourceTracks.Zip(outputTracks).All(pair =>
                pair.First.Kind == pair.Second.Kind &&
                pair.First.Index == pair.Second.Index &&
                string.Equals(pair.First.Language, pair.Second.Language, StringComparison.OrdinalIgnoreCase) &&
                pair.First.IsDefault == pair.Second.IsDefault &&
                pair.First.IsForced == pair.Second.IsForced &&
                pair.First.Channels == pair.Second.Channels &&
                string.Equals(pair.First.Title, pair.Second.Title, StringComparison.Ordinal) &&
                (pair.First.Kind != MediaTrackKind.Subtitle ||
                 string.Equals(pair.First.Codec, pair.Second.Codec, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidDataException("The prepared output changed the source timeline or track identity.");
        }

        output.Refresh();
        if (!output.Exists || output.Length == 0)
        {
            throw new IOException("The verified output disappeared before publication.");
        }

        var outputFingerprint = await MediaInventoryService.TryComputeFingerprintAsync(path, cancellationToken);
        if (outputFingerprint is not { Length: 64 })
        {
            throw new IOException("The prepared output cannot be fingerprinted.");
        }

        var versionKey = $"prepared:v1:{source.StoredFileId:N}:{sourceFingerprint[..32]}:{recipeKey}";
        var notes = System.Text.Json.JsonSerializer.Serialize(new
        {
            sourceStoredFileId = source.StoredFileId,
            sourceFingerprint,
            outputFingerprint,
            recipeVersion = 1,
            recipeKey,
            verifiedOutput = true
        });

        // Another job may have published the same source/recipe. Reuse the canonical row only
        // if it still points to these exact bytes; never adopt an unrelated existing path.
        var existing = await (
            from version in db.WorkVersions.AsNoTracking()
            join asset in db.MediaAssets.AsNoTracking() on version.Id equals asset.WorkVersionId
            join file in db.StoredFiles.AsNoTracking() on (Guid?)asset.Id equals file.MediaAssetId
            where version.WorkId == source.WorkId && version.VersionKey == versionKey &&
                  asset.WorkEpisodeId == source.WorkEpisodeId && asset.Kind == MediaAssetKind.Video
            select new { version, asset, file })
            .SingleOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            if (existing.version.Source != PreparedVideoVersionSource ||
                existing.version.Notes != notes ||
                existing.file.Path != path ||
                existing.file.SizeBytes != output.Length ||
                Math.Abs((existing.file.LastWriteTimeUtc - output.LastWriteTimeUtc).Ticks) >= 10)
            {
                throw new InvalidOperationException("An incompatible prepared version already exists.");
            }

            return new CanonicalPlayableFile(
                existing.asset.Id, existing.file.Id, source.WorkId, source.WorkEpisodeId,
                existing.version.Id, existing.file.Path, existing.file.SizeBytes,
                existing.file.LastWriteTimeUtc, existing.version.Source, existing.version.Notes);
        }

        if (await db.StoredFiles.AnyAsync(file => file.Path == path, cancellationToken))
        {
            throw new InvalidOperationException("The prepared cache path belongs to another StoredFile.");
        }

        // Prepared roots are recorded but not enabled for import scans. The real original
        // always stays on its existing storage; the disposable cache is a separate root.
        var libraryRoots = await db.LibraryRoots.ToListAsync(cancellationToken);
        if (libraryRoots.Any(candidate => candidate.IsEnabled &&
            (string.Equals(NormalizeRoot(candidate.Path), root, StringComparison.Ordinal) ||
             root.StartsWith(NormalizeRoot(candidate.Path) + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
             NormalizeRoot(candidate.Path).StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("The prepared cache may not overlap an enabled media library.");
        }

        var cacheRoot = libraryRoots.FirstOrDefault(candidate =>
            string.Equals(NormalizeRoot(candidate.Path), root, StringComparison.Ordinal));
        if (cacheRoot is null)
        {
            cacheRoot = new LibraryRoot
            {
                Name = "Prepared playback cache",
                Path = root,
                IsEnabled = false
            };
            db.LibraryRoots.Add(cacheRoot);
        }
        else if (cacheRoot.IsEnabled)
        {
            throw new InvalidOperationException("An enabled media library cannot be a prepared cache root.");
        }

        var versionRow = new WorkVersion
        {
            WorkId = source.WorkId,
            VersionKey = versionKey,
            UnitKey = sourceVersion.UnitKey,
            Source = PreparedVideoVersionSource,
            Notes = notes
        };
        var mediaAsset = new MediaAsset
        {
            WorkId = source.WorkId,
            WorkEpisodeId = source.WorkEpisodeId,
            Kind = MediaAssetKind.Video,
            WorkVersionId = versionRow.Id
        };
        var stored = new StoredFile
        {
            MediaAssetId = mediaAsset.Id,
            LibraryRootId = cacheRoot.Id,
            Path = path,
            SizeBytes = output.Length,
            LastWriteTimeUtc = output.LastWriteTimeUtc
        };

        db.WorkVersions.Add(versionRow);
        db.MediaAssets.Add(mediaAsset);
        db.StoredFiles.Add(stored);
        await db.SaveChangesAsync(cancellationToken);

        return new CanonicalPlayableFile(
            mediaAsset.Id, stored.Id, source.WorkId, source.WorkEpisodeId,
            versionRow.Id, stored.Path, stored.SizeBytes, stored.LastWriteTimeUtc,
            versionRow.Source, versionRow.Notes);
    }

    /// <summary>Every video file of the Work with the quality its Version recorded; episodes are told apart by <see cref="InstalledVideoFile.WorkEpisodeId"/>, a Movie has none.</summary>
    public async Task<IReadOnlyList<InstalledVideoFile>> ListVideoFilesAsync(long workId, CancellationToken cancellationToken) =>
        await (
            from asset in db.MediaAssets.AsNoTracking()
            join file in db.StoredFiles.AsNoTracking() on (Guid?)asset.Id equals file.MediaAssetId
            join version in db.WorkVersions.AsNoTracking() on asset.WorkVersionId equals version.Id
            where asset.Kind == MediaAssetKind.Video && asset.WorkId == workId &&
                  (version.Source == null || !version.Source.StartsWith(PreparedVideoVersionPrefix))
            orderby file.Path
            select new InstalledVideoFile(file.Id, asset.WorkEpisodeId, file.Path, version.Quality))
        .ToListAsync(cancellationToken);

    /// <summary>Every audio file of the album Work with the quality its Version recorded.</summary>
    public async Task<IReadOnlyList<InstalledAudioFile>> ListAudioFilesAsync(long workId, CancellationToken cancellationToken) =>
        await (
            from asset in db.MediaAssets.AsNoTracking()
            join file in db.StoredFiles.AsNoTracking() on (Guid?)asset.Id equals file.MediaAssetId
            join version in db.WorkVersions.AsNoTracking() on asset.WorkVersionId equals version.Id
            where asset.Kind == MediaAssetKind.Audio && asset.WorkId == workId && asset.WorkTrackId != null
            orderby file.Path
            select new InstalledAudioFile(file.Id, asset.WorkTrackId!.Value, file.Path, version.Quality))
        .ToListAsync(cancellationToken);

    /// <summary>
    /// Forgets stored files a better version replaced: their file rows go (probe results follow with them) and the files are deleted from disk when
    /// they are still there. The Assets and Versions stay as history; nothing resolves them without a file. A file that cannot be deleted stays
    /// on disk for the library scan and is reported in the returned notes, it never blocks the replacement.
    /// </summary>
    public async Task<IReadOnlyList<string>> RemoveFilesAsync(IReadOnlyCollection<Guid> storedFileIds, CancellationToken cancellationToken)
    {
        if (storedFileIds.Count == 0)
        {
            return [];
        }

        var files = await db.StoredFiles.Where(file => storedFileIds.Contains(file.Id)).ToListAsync(cancellationToken);
        var notes = new List<string>();
        db.StoredFiles.RemoveRange(files);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var file in files)
        {
            try
            {
                File.Delete(file.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                notes.Add($"Replaced file {System.IO.Path.GetFileName(file.Path)} could not be deleted: {exception.Message}");
            }
        }

        return notes;
    }

    public async Task<CanonicalPlayableFile?> ResolveVideoAsync(
        long workId,
        Guid? workEpisodeId,
        CancellationToken cancellationToken) =>
        (await ResolveVideoCandidatesAsync(workId, workEpisodeId, cancellationToken))
            .FirstOrDefault(candidate => !IsPreparedVideoSource(candidate.VersionSource));

    public async Task<IReadOnlyList<CanonicalPlayableFile>> ResolveVideoCandidatesAsync(
        long workId,
        Guid? workEpisodeId,
        CancellationToken cancellationToken) =>
        await (
            from asset in db.MediaAssets.AsNoTracking()
            join file in db.StoredFiles.AsNoTracking()
                on (Guid?)asset.Id equals file.MediaAssetId
            join version in db.WorkVersions.AsNoTracking()
                on asset.WorkVersionId equals version.Id
            where asset.Kind == MediaAssetKind.Video &&
                  asset.WorkId == workId &&
                  asset.WorkEpisodeId == workEpisodeId
            orderby version.Source != null && version.Source.StartsWith(PreparedVideoVersionPrefix), file.Path
            select new CanonicalPlayableFile(
                asset.Id,
                file.Id,
                asset.WorkId,
                asset.WorkEpisodeId,
                asset.WorkVersionId,
                file.Path,
                file.SizeBytes,
                file.LastWriteTimeUtc,
                version.Source,
                version.Notes))
        .Take(8)
        .ToListAsync(cancellationToken);

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

    private sealed record VideoEpisodeRow(Guid Id, long WorkId, int SeasonNumber, int EpisodeNumber);
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
