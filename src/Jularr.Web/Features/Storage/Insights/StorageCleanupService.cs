using Jularr.Web.Data;
using Jularr.Web.Features.Playback.Transcoding;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Storage.Insights;

// The safe cleanup of Jularr-owned cache leftovers. It previews before it deletes and can never
// reach library media:
//  - it acts only on entries the scanner proves unusable, recomputed at execution time, so a
//    stale or tampered submission cannot name a path;
//  - every entry must lie strictly inside its own cache area;
//  - an entry that is, contains or sits inside a library root or a known media path is refused
//    even if the cache layout were misconfigured onto library storage;
//  - it never opens a library root, so a sleeping NAS stays asleep.
public sealed class StorageCleanupService(
    AppDbContext db,
    StorageCacheScanner scanner,
    StorageCacheLayout layout,
    ILogger<StorageCleanupService> logger)
{
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    public Task<StorageCacheReport> PreviewAsync(CancellationToken cancellationToken) =>
        scanner.ScanAsync(cancellationToken);

    public async Task<StorageCleanupResult> CleanAsync(
        IReadOnlyCollection<StorageCacheAreaKind> areas,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(areas);
        var selected = areas.Where(StorageCacheAreas.IsCleanable).ToHashSet();
        if (selected.Count == 0)
        {
            return new StorageCleanupResult(0, 0, 0);
        }

        await RunGate.WaitAsync(cancellationToken);
        try
        {
            var plan = (await scanner.ScanAsync(cancellationToken)).Plan;
            var libraryRoots = await db.LibraryRoots
                .AsNoTracking()
                .Select(x => x.Path)
                .ToListAsync(cancellationToken);

            var removed = 0;
            var skipped = 0;
            long freed = 0;
            foreach (var candidate in plan.Candidates.Where(x => selected.Contains(x.Area)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await IsSafeToDeleteAsync(candidate, libraryRoots, cancellationToken) ||
                    !TryDelete(candidate))
                {
                    skipped++;
                    continue;
                }

                removed += (int)Math.Min(candidate.Files, int.MaxValue);
                freed += candidate.Bytes;
            }

            logger.LogInformation(
                "Storage cleanup removed {Files} files ({Bytes} bytes) and skipped {Skipped} entries.",
                removed,
                freed,
                skipped);
            return new StorageCleanupResult(removed, freed, skipped);
        }
        finally
        {
            RunGate.Release();
        }
    }

    private async Task<bool> IsSafeToDeleteAsync(
        ReclaimCandidate candidate,
        IReadOnlyList<string> libraryRoots,
        CancellationToken cancellationToken)
    {
        if (!StorageCacheAreas.IsCleanable(candidate.Area))
        {
            return false;
        }

        var target = FullPath(candidate.Path);
        var areaRoot = FullPath(layout.RootOf(candidate.Area));
        if (target is null || areaRoot is null || !IsStrictlyInside(target, areaRoot))
        {
            return false;
        }

        // HLS session directories are deleted by one policy everywhere (see PlaybackCacheOwnership).
        if (candidate.Area == StorageCacheAreaKind.HlsSessions && !PlaybackCacheOwnership.IsDeletableSession(areaRoot, target))
        {
            return false;
        }

        foreach (var root in libraryRoots)
        {
            var libraryRoot = FullPath(root);
            if (libraryRoot is null ||
                IsInsideOrSame(target, libraryRoot) ||
                IsInsideOrSame(libraryRoot, target))
            {
                return false;
            }
        }

        // Canonical media outside any library root (audiobook and book files) is protected the same way.
        var prefix = target + Path.DirectorySeparatorChar;
        var isMedia =
            await db.MediaFiles.AnyAsync(x => x.Path == target || x.Path.StartsWith(prefix), cancellationToken) ||
            await db.AudiobookFiles.AnyAsync(x => x.StoragePath == target || x.StoragePath.StartsWith(prefix), cancellationToken) ||
            await db.BookFiles.AnyAsync(x => x.StoragePath == target || (x.StoragePath != null && x.StoragePath.StartsWith(prefix)), cancellationToken);
        return !isMedia;
    }

    private bool TryDelete(ReclaimCandidate candidate)
    {
        try
        {
            if (candidate.IsDirectory)
            {
                if (!Directory.Exists(candidate.Path))
                {
                    return false;
                }

                Directory.Delete(candidate.Path, recursive: true);
            }
            else
            {
                if (!File.Exists(candidate.Path))
                {
                    return false;
                }

                File.Delete(candidate.Path);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Storage cleanup could not remove {Path}.", candidate.Path);
            return false;
        }
    }

    private static string? FullPath(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path)
                ? null
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsStrictlyInside(string path, string root) =>
        path.StartsWith(
            root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
            PathComparison);

    private static bool IsInsideOrSame(string path, string root) =>
        string.Equals(path, root, PathComparison) || IsStrictlyInside(path, root);
}
