namespace Jularr.Web.Features.Storage.Insights;

// Jularr-owned data under the /data volume that Jularr can rebuild on demand. Library media is
// never part of this list: it lives in the configured library roots and is only ever removed
// by an explicit, separate action.
public enum StorageCacheAreaKind
{
    PreparedPlayback,
    HlsSessions,
    Trickplay,
    Artwork,
    Fingerprints,
    MangaPages
}

public static class StorageCacheAreas
{
    // Areas whose leftovers the safe cleanup may remove. The others are shown for their size only;
    // their owners already manage their lifetime, so removing files there would fight them.
    public static bool IsCleanable(StorageCacheAreaKind kind) =>
        kind is StorageCacheAreaKind.PreparedPlayback
            or StorageCacheAreaKind.HlsSessions
            or StorageCacheAreaKind.Trickplay;

    public static IReadOnlyList<StorageCacheAreaKind> All { get; } =
        Enum.GetValues<StorageCacheAreaKind>();
}

// Why a cache entry is safe to remove.
public enum ReclaimReason
{
    // The media file it was made from is no longer in the library.
    SourceRemoved,

    // The media file was replaced or changed since the entry was made; the entry can no longer be used.
    SourceChanged,

    // A work file left behind by an interrupted job.
    InterruptedWork,

    // A playback session that stopped long ago.
    IdleSession
}

public sealed record ReclaimCandidate(
    StorageCacheAreaKind Area,
    string Path,
    bool IsDirectory,
    long Bytes,
    long Files,
    ReclaimReason Reason);

public sealed record StorageCacheUsage(
    StorageCacheAreaKind Area,
    long Files,
    long Bytes,
    long ReclaimableFiles,
    long ReclaimableBytes)
{
    public bool Cleanable => StorageCacheAreas.IsCleanable(Area);
}

// What the safe cleanup would remove right now. Building it only reads; the cleanup builds it
// again at execution time and never trusts a previously shown or submitted list.
public sealed record StorageCleanupPlan(IReadOnlyList<ReclaimCandidate> Candidates)
{
    public static readonly StorageCleanupPlan Empty = new([]);

    public long TotalBytes => Candidates.Sum(x => x.Bytes);

    public long TotalFiles => Candidates.Sum(x => x.Files);

    public IReadOnlyList<ReclaimCandidate> For(StorageCacheAreaKind area) =>
        [.. Candidates.Where(x => x.Area == area)];
}

public sealed record StorageCacheReport(
    IReadOnlyList<StorageCacheUsage> Usage,
    StorageCleanupPlan Plan);

public sealed record StorageCleanupResult(int Removed, long BytesFreed, int Skipped);

public sealed record StorageRootUsage(
    Guid RootId,
    string Name,
    string Path,
    // Null when the root's state has not been observed yet. Analytics never probe a root that
    // could be a sleeping Wake-on-LAN NAS just to find out.
    StorageHealthState? Health,
    long FileCount,
    long Bytes,
    long? FreeBytes,
    int DuplicateEpisodes,
    DateTime? LastScannedAtUtc)
{
    // The numbers come from the last reconciliation in every case; this says whether the storage
    // was seen readable right now, so an offline root's figures read as "as of the last scan".
    public bool IsOnline => Health == StorageHealthState.Online;
}

public enum StorageMediaKind
{
    Movies,
    Episodes,
    UnmatchedVideo,
    Audiobooks,
    Books
}

public sealed record StorageMediaTypeUsage(StorageMediaKind Kind, long FileCount, long Bytes);

public sealed record StorageLargestItem(
    Guid MediaFileId,
    string Title,
    // Both null for a movie, which has no season or episode.
    int? SeasonNumber,
    int? EpisodeNumber,
    Guid RootId,
    string RootName,
    long Bytes,
    StorageHealthState? RootHealth);

public sealed record StorageUsageReport(
    IReadOnlyList<StorageRootUsage> Roots,
    IReadOnlyList<StorageMediaTypeUsage> MediaTypes,
    IReadOnlyList<StorageLargestItem> LargestItems)
{
    public long TotalMediaBytes => MediaTypes.Sum(x => x.Bytes);
}
