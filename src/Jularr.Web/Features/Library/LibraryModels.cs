namespace Jularr.Web.Features.Library;

public enum LibraryContentType
{
    Anime = 1,
    Manga = 2,
    LightNovel = 3,
    Book = 4,
    Movie = 5,
    Tv = 6,
    Audiobook = 7,
    Game = 8,
    Music = 9
}

public enum LibraryPlacementPolicy
{
    HardlinkOrCopy = 0,
    Hardlink = 1,
    Copy = 2,
    Move = 3
}

public sealed class LibraryRoot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public LibraryPlacementPolicy PlacementPolicy { get; set; } = LibraryPlacementPolicy.HardlinkOrCopy;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastScannedAt { get; set; }
    public bool WakeOnLanEnabled { get; set; }
    public string? WakeMacAddress { get; set; }
    public string? WakeBroadcastAddress { get; set; }

    // Periodic safety reconciliation of this root; 0 disables it.
    public int ReconciliationIntervalMinutes { get; set; } = DefaultReconciliationIntervalMinutes;

    public const int DefaultReconciliationIntervalMinutes = 30;
    public const int MinimumReconciliationIntervalMinutes = 5;
    public const int MaximumReconciliationIntervalMinutes = 24 * 60;
}

public sealed class LibraryRootContentAssignment
{
    public Guid LibraryRootId { get; set; }
    public LibraryContentType ContentType { get; set; }
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Anime
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Episode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AnimeId { get; set; }
    public int SeasonNumber { get; set; } = 1;
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
}

public sealed class MediaOptions
{
    public const string SectionName = "Media";
    public string? BootstrapRoot { get; set; }
}

public sealed record ScanResult(int Discovered, int Updated, int Skipped, int SubtitleFiles)
{
    public int Removed { get; init; }

    // Known files found again under a new path (moved or renamed outside Jularr).
    public int Relinked { get; init; }

    // NFO files that were present but rejected (malformed, oversized, unreadable or unsupported).
    public int MetadataWarnings { get; init; }

    public MediaInventoryReconciliation MediaInventory { get; init; } = new(0, 0, 0, 0);

    // Media files enumerated in the scanned scope.
    public int MediaFiles { get; init; }

    public int ArtworkImported { get; init; }

    // Per-item failures that did not stop the scan (for example provider matching).
    public int Errors { get; init; }

    // Item-level warnings with root-relative paths; bounded to MaxRecordedWarnings.
    public IReadOnlyList<ScanWarning> Warnings { get; init; } = [];

    // Total number of warnings including those beyond the recorded bound.
    public int WarningCount { get; init; }

    public const int MaxRecordedWarnings = 200;

    // The result of one run that reconciled several folders one after another.
    public static ScanResult Combine(IReadOnlyCollection<ScanResult> results) =>
        new(
            results.Sum(x => x.Discovered),
            results.Sum(x => x.Updated),
            results.Sum(x => x.Skipped),
            results.Sum(x => x.SubtitleFiles))
        {
            Removed = results.Sum(x => x.Removed),
            Relinked = results.Sum(x => x.Relinked),
            MetadataWarnings = results.Sum(x => x.MetadataWarnings),
            MediaInventory = new MediaInventoryReconciliation(
                results.Sum(x => x.MediaInventory.Unchanged),
                results.Sum(x => x.MediaInventory.Analyzed),
                results.Sum(x => x.MediaInventory.Failed),
                results.Sum(x => x.MediaInventory.Deferred)),
            MediaFiles = results.Sum(x => x.MediaFiles),
            ArtworkImported = results.Sum(x => x.ArtworkImported),
            Errors = results.Sum(x => x.Errors),
            Warnings = results.SelectMany(x => x.Warnings).Take(MaxRecordedWarnings).ToArray(),
            WarningCount = results.Sum(x => x.WarningCount)
        };
}

// A root-relative path only: scan diagnostics never carry the host path of the root.
public sealed record ScanWarning(string Reason, string RelativePath);

public enum LibraryScanPhase
{
    Queued = 0,
    Enumerating = 1,
    Reconciling = 2,
    Metadata = 3,
    Artwork = 4,
    Subtitles = 5,
    Completed = 6,

    // Technical media analysis (MediaInventoryService); runs between artwork and subtitles.
    Analyzing = 7
}

public sealed record LibraryScanProgress(LibraryScanPhase Phase, int Processed, int Total);

public delegate Task LibraryScanProgressHandler(
    LibraryScanProgress progress,
    CancellationToken cancellationToken);
