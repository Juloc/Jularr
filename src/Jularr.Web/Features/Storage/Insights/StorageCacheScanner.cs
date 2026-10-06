using System.Text.RegularExpressions;
using Jularr.Web.Data;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.MediaSegments;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Playback.Transcoding;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Storage.Insights;

// Where each Jularr-owned cache area lives. The defaults are the paths the owning features
// already use; tests point them at temporary folders.
public sealed record StorageCacheLayout(
    string PlaybackCacheRoot,
    string HlsRoot,
    string TrickplayRoot,
    string ArtworkRoot,
    string FingerprintRoot,
    string MangaCacheRoot)
{
    public static StorageCacheLayout Default { get; } = new(
        PlaybackCache.RootPath,
        PlaybackTranscodingSettings.DefaultHlsCachePath,
        TrickplayGenerator.DefaultRootPath,
        Path.GetDirectoryName(AnimeArtworkCache.DefaultRootPath)!,
        AudioFingerprintMediaSegmentDetector.DefaultCacheRoot,
        MangaImportService.CacheRoot);

    public string RootOf(StorageCacheAreaKind area) =>
        area switch
        {
            StorageCacheAreaKind.PreparedPlayback => PlaybackCacheRoot,
            StorageCacheAreaKind.HlsSessions => HlsRoot,
            StorageCacheAreaKind.Trickplay => TrickplayRoot,
            StorageCacheAreaKind.Artwork => ArtworkRoot,
            StorageCacheAreaKind.Fingerprints => FingerprintRoot,
            StorageCacheAreaKind.MangaPages => MangaCacheRoot,
            _ => throw new ArgumentOutOfRangeException(nameof(area))
        };
}

// Measures the cache areas and finds what in them can no longer be used. It reads the Jularr
// data volume and the database only; it never opens a library root, so a sleeping NAS is left
// alone. An entry is only reclaimable when the database proves its source is gone or changed,
// or when it is an interrupted work file or a long-idle playback session.
public sealed partial class StorageCacheScanner(
    AppDbContext db,
    StorageCacheLayout layout,
    TimeProvider time)
{
    // The longest a playback preparation may run is 6 hours; a work file older than this is
    // left over from an interrupted job.
    public static readonly TimeSpan PreparedWorkMaxAge = TimeSpan.FromHours(8);

    // A live HLS session keeps writing segments; well past its idle lifetime nothing reads it.
    public static readonly TimeSpan HlsIdleAge = HlsPlaybackSessionManager.IdleLifetime * 6;

    private const string TrickplayWorkDirectory = ".tmp";

    [GeneratedRegex("^(?<id>[0-9a-f]{32})-(?<size>[0-9]+)-(?<ticks>[0-9]+)-(compatible|device-hevc|server-h264)\\.mp4$", RegexOptions.CultureInvariant)]
    private static partial Regex PreparedFilePattern();

    [GeneratedRegex("^(?<id>[0-9a-f]{32})-", RegexOptions.CultureInvariant)]
    private static partial Regex MediaPrefixPattern();

    public async Task<StorageCacheReport> ScanAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<ReclaimCandidate>();
        var usage = new List<StorageCacheUsage>();

        foreach (var area in StorageCacheAreas.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var measured = await Task.Run(() => Measure(area, cancellationToken), cancellationToken);
            IReadOnlyList<ReclaimCandidate> found = area switch
            {
                StorageCacheAreaKind.PreparedPlayback => await FindPreparedAsync(cancellationToken),
                StorageCacheAreaKind.HlsSessions => FindIdleSessions(),
                StorageCacheAreaKind.Trickplay => await FindTrickplayAsync(cancellationToken),
                _ => []
            };

            candidates.AddRange(found);
            usage.Add(new StorageCacheUsage(
                area,
                measured.Files,
                measured.Bytes,
                found.Sum(x => x.Files),
                found.Sum(x => x.Bytes)));
        }

        return new StorageCacheReport(usage, new StorageCleanupPlan(candidates));
    }

    private (long Files, long Bytes) Measure(StorageCacheAreaKind area, CancellationToken cancellationToken)
    {
        var root = layout.RootOf(area);
        if (!Directory.Exists(root))
        {
            return (0, 0);
        }

        // The playback cache folder also holds the HLS and trickplay folders, which are their own areas.
        return area == StorageCacheAreaKind.PreparedPlayback
            ? MeasureFiles(Directory.EnumerateFiles(root), cancellationToken)
            : MeasureTree(root, cancellationToken);
    }

    private async Task<IReadOnlyList<ReclaimCandidate>> FindPreparedAsync(CancellationToken cancellationToken)
    {
        var root = layout.PlaybackCacheRoot;
        if (!Directory.Exists(root))
        {
            return [];
        }

        var now = time.GetUtcNow();
        var found = new List<ReclaimCandidate>();
        var prepared = new List<(string Path, Guid MediaFileId, long Size, long Ticks, long Bytes)>();

        foreach (var path in Directory.EnumerateFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (!TryInspectFile(path, out var info))
            {
                continue;
            }

            if (name.EndsWith(".mp4.tmp", StringComparison.Ordinal))
            {
                if (now - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) > PreparedWorkMaxAge)
                {
                    found.Add(new ReclaimCandidate(
                        StorageCacheAreaKind.PreparedPlayback, path, false, info.Length, 1,
                        ReclaimReason.InterruptedWork));
                }

                continue;
            }

            var match = PreparedFilePattern().Match(name);
            if (match.Success &&
                Guid.TryParseExact(match.Groups["id"].Value, "N", out var mediaFileId) &&
                long.TryParse(match.Groups["size"].ValueSpan, out var size) &&
                long.TryParse(match.Groups["ticks"].ValueSpan, out var ticks))
            {
                prepared.Add((path, mediaFileId, size, ticks, info.Length));
            }
        }

        var current = await LoadMediaAsync(prepared.Select(x => x.MediaFileId), cancellationToken);
        foreach (var entry in prepared)
        {
            if (!current.TryGetValue(entry.MediaFileId, out var media))
            {
                found.Add(new ReclaimCandidate(
                    StorageCacheAreaKind.PreparedPlayback, entry.Path, false, entry.Bytes, 1,
                    ReclaimReason.SourceRemoved));
            }
            else if (media.SizeBytes != entry.Size || media.LastWriteTimeUtc.Ticks != entry.Ticks)
            {
                found.Add(new ReclaimCandidate(
                    StorageCacheAreaKind.PreparedPlayback, entry.Path, false, entry.Bytes, 1,
                    ReclaimReason.SourceChanged));
            }
        }

        return found;
    }

    private async Task<IReadOnlyList<ReclaimCandidate>> FindTrickplayAsync(CancellationToken cancellationToken)
    {
        var root = layout.TrickplayRoot;
        if (!Directory.Exists(root))
        {
            return [];
        }

        var now = time.GetUtcNow();
        var found = new List<ReclaimCandidate>();
        var generations = new List<(string Path, Guid MediaFileId)>();

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (name == TrickplayWorkDirectory)
            {
                foreach (var work in Directory.EnumerateDirectories(directory))
                {
                    if (!IsPlainDirectory(work) ||
                        now - new DateTimeOffset(Directory.GetCreationTimeUtc(work), TimeSpan.Zero) <= TrickplayGenerator.PendingExpiry)
                    {
                        continue;
                    }

                    var (files, bytes) = MeasureTree(work, cancellationToken);
                    found.Add(new ReclaimCandidate(
                        StorageCacheAreaKind.Trickplay, work, true, bytes, files, ReclaimReason.InterruptedWork));
                }

                continue;
            }

            var match = MediaPrefixPattern().Match(name);
            if (match.Success &&
                IsPlainDirectory(directory) &&
                Guid.TryParseExact(match.Groups["id"].Value, "N", out var mediaFileId))
            {
                generations.Add((directory, mediaFileId));
            }
        }

        var current = await LoadMediaAsync(generations.Select(x => x.MediaFileId), cancellationToken);
        foreach (var generation in generations.Where(x => !current.ContainsKey(x.MediaFileId)))
        {
            var (files, bytes) = MeasureTree(generation.Path, cancellationToken);
            found.Add(new ReclaimCandidate(
                StorageCacheAreaKind.Trickplay, generation.Path, true, bytes, files, ReclaimReason.SourceRemoved));
        }

        return found;
    }

    private IReadOnlyList<ReclaimCandidate> FindIdleSessions()
    {
        var root = layout.HlsRoot;
        if (!Directory.Exists(root))
        {
            return [];
        }

        var now = time.GetUtcNow();
        var found = new List<ReclaimCandidate>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!PlaybackCacheOwnership.IsDeletableSession(root, directory))
            {
                continue;
            }

            var newest = NewestWriteUtc(directory);
            if (now - new DateTimeOffset(newest, TimeSpan.Zero) > HlsIdleAge)
            {
                var (files, bytes) = MeasureTree(directory, CancellationToken.None);
                found.Add(new ReclaimCandidate(
                    StorageCacheAreaKind.HlsSessions, directory, true, bytes, files, ReclaimReason.IdleSession));
            }
        }

        return found;
    }

    private async Task<Dictionary<Guid, MediaIdentity>> LoadMediaAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken)
    {
        var wanted = ids.Distinct().ToArray();
        if (wanted.Length == 0)
        {
            return [];
        }

        return await db.MediaFiles
            .AsNoTracking()
            .Where(x => wanted.Contains(x.Id))
            .Select(x => new MediaIdentity(x.Id, x.SizeBytes, x.LastWriteTimeUtc))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
    }

    private sealed record MediaIdentity(Guid Id, long SizeBytes, DateTime LastWriteTimeUtc);

    // Links are never followed or offered: only real files and folders inside the cache.
    private static bool IsPlainDirectory(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryInspectFile(string path, out FileInfo info)
    {
        info = new FileInfo(path);
        try
        {
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static DateTime NewestWriteUtc(string directory)
    {
        var newest = Directory.GetLastWriteTimeUtc(directory);
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var written = File.GetLastWriteTimeUtc(file);
                if (written > newest)
                {
                    newest = written;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read is treated as freshly written, so it is never offered.
            return DateTime.UtcNow;
        }

        return newest;
    }

    private static (long Files, long Bytes) MeasureTree(string root, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        try
        {
            return MeasureFiles(Directory.EnumerateFiles(root, "*", options), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    private static (long Files, long Bytes) MeasureFiles(
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        long files = 0;
        long bytes = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                files++;
                bytes += info.Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file that vanished or cannot be read is not counted.
            }
        }

        return (files, bytes);
    }
}
