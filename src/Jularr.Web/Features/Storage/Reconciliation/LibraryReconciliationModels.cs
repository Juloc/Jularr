namespace Jularr.Web.Features.Storage.Reconciliation;

/// <summary>
/// The lifecycle of a persisted reconciliation plan. A plan remains non-destructive until a later
/// execution slice has produced and approved a complete filesystem preview.
/// </summary>
public enum LibraryReconciliationPlanStatus
{
    Draft = 1,
    Scanning = 2,
    Ready = 3,
    Stale = 4,
    Failed = 5,
    Executing = 6,
    Completed = 7
}

/// <summary>Physical-operation policy selected for a reconciliation plan after its mapping review is complete.</summary>
public enum LibraryReconciliationOrganizationMode
{
    AssignOnly = 1,
    RenameFiles = 2,
    RenameFoldersAndFiles = 3,
    OrganizeCanonical = 4
}

/// <summary>How confidently one scanned path is currently understood. This is evidence, never identity.</summary>
public enum LibraryReconciliationItemState
{
    Recognized = 1,
    Partial = 2,
    Unclear = 3,
    Error = 4,
    Ignored = 5
}

/// <summary>
/// The durable, root-scoped working state for the Admin Library Reconciliation wizard. Paths are
/// always stored relative to <see cref="LibraryRootId"/> so a plan never grants access outside its
/// configured library root.
/// </summary>
public sealed class LibraryReconciliationPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid LibraryRootId { get; set; }

    /// <summary>Optional, normalized folder below the configured root; null means the whole root.</summary>
    public string? StartFolder { get; set; }

    public bool IncludeSubfolders { get; set; } = true;

    public bool SkipConfidentAssignments { get; set; }

    public bool OnlyUnclearItems { get; set; }

    /// <summary>Controls optional, non-identifying title evidence from names during the scan.</summary>
    public bool AnalyzeFilenameEvidence { get; set; } = true;

    public LibraryReconciliationOrganizationMode OrganizationMode { get; set; } = LibraryReconciliationOrganizationMode.AssignOnly;

    /// <summary>Allows an administrator to remove only empty source directories below the reviewed scope after all approved moves succeed.</summary>
    public bool RemoveEmptySourceFolders { get; set; }

    public LibraryReconciliationPlanStatus Status { get; set; } = LibraryReconciliationPlanStatus.Draft;

    public int Revision { get; set; } = 1;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ScannedAtUtc { get; set; }

    /// <summary>Activity operation that most recently executed this plan, retained for progress and recovery handoff.</summary>
    public Guid? ExecutionOperationId { get; set; }

    /// <summary>A concise, safe failure summary; detailed diagnostics belong in the associated operation.</summary>
    public string? Failure { get; set; }
}

/// <summary>
/// A root-relative scan result. Future mapping steps can attach canonical Work/Structure/Edition/
/// Version targets, but this record intentionally has no such identity until an admin confirms it.
/// </summary>
public sealed class LibraryReconciliationPlanItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PlanId { get; set; }

    /// <summary>Normalized relative path below the plan's configured library root.</summary>
    public string RelativePath { get; set; } = "";

    public bool IsDirectory { get; set; }

    public LibraryReconciliationItemState State { get; set; } = LibraryReconciliationItemState.Unclear;

    /// <summary>Bounded 0–100 suggestion confidence, not a permission to create canonical identity.</summary>
    public int Confidence { get; set; }

    public int FileCount { get; set; }

    public int UnresolvedCount { get; set; }

    /// <summary>Observed source size at scan time for one file; null for directories and non-file inventory entries.</summary>
    public long? ObservedSizeBytes { get; set; }

    /// <summary>Observed UTC write time at scan time for one file; null for directories and non-file inventory entries.</summary>
    public DateTime? ObservedLastWriteTimeUtc { get; set; }

    /// <summary>Explicitly chosen canonical work; descendants derive a folder default without duplicating this direct choice.</summary>
    public long? AssignedWorkId { get; set; }

    /// <summary>Explicitly chosen canonical episode for one media file; folder defaults never infer a file-level unit.</summary>
    public Guid? AssignedWorkEpisodeId { get; set; }

    /// <summary>Explicitly chosen canonical volume for one media file; folder defaults never infer a file-level unit.</summary>
    public Guid? AssignedWorkVolumeId { get; set; }

    /// <summary>Explicitly chosen canonical chapter for one media file; it must remain inside the effective work and volume.</summary>
    public Guid? AssignedWorkChapterId { get; set; }

    /// <summary>Optional existing editorial variant selected for this plan entry; it must belong to the effective work.</summary>
    public Guid? AssignedWorkEditionId { get; set; }

    /// <summary>Optional existing concrete release selected for this plan entry; it must belong to the effective work and edition when present.</summary>
    public Guid? AssignedWorkVersionId { get; set; }

    /// <summary>Administrator-confirmed content language; null means inherited or unspecified rather than a filename inference.</summary>
    public string? Language { get; set; }

    /// <summary>Administrator-confirmed primary audio language; null means inherited or unspecified.</summary>
    public string? AudioLanguage { get; set; }

    /// <summary>Administrator-confirmed subtitle language preference; null means inherited or unspecified.</summary>
    public string? SubtitleLanguage { get; set; }

    /// <summary>Administrator-confirmed quality or source note, kept distinct from probed technical media analysis.</summary>
    public string? QualitySource { get; set; }

    /// <summary>Optional plan-only logical group used to split or merge physical folders without changing the filesystem hierarchy.</summary>
    public Guid? LogicalGroupId { get; set; }

    /// <summary>Marks a media file as deliberately excluded from this plan until an admin restores it.</summary>
    public bool IsExplicitlyIgnored { get; set; }

    public string? DetectionSummary { get; set; }

    public string? Error { get; set; }
}

/// <summary>A reversible logical mapping group that may contain files from one or more physical folders during reconciliation review.</summary>
public sealed class LibraryReconciliationLogicalGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PlanId { get; set; }

    /// <summary>Administrator-provided review label; it is never used as physical or canonical identity.</summary>
    public string Name { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>One canonical file link committed only after an administrator-approved reconciliation execution.</summary>
public sealed class LibraryReconciliationFileLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PlanId { get; set; }

    public Guid PlanItemId { get; set; }

    public Guid LibraryRootId { get; set; }

    public long WorkId { get; set; }

    public Guid? WorkEpisodeId { get; set; }

    public Guid? WorkVolumeId { get; set; }

    public Guid? WorkChapterId { get; set; }

    public Guid? WorkEditionId { get; set; }

    public Guid? WorkVersionId { get; set; }

    public string? Language { get; set; }

    public string? AudioLanguage { get; set; }

    public string? SubtitleLanguage { get; set; }

    public string? QualitySource { get; set; }

    /// <summary>Physical source path from the reviewed inventory, always relative to the configured library root.</summary>
    public string OriginalRelativePath { get; set; } = "";

    /// <summary>Physical path after the approved organization operation, always relative to the configured library root.</summary>
    public string RelativePath { get; set; } = "";

    public DateTime CommittedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Validated request for a root-bound, read-only reconciliation draft.</summary>
public sealed record LibraryReconciliationPlanRequest(
    Guid LibraryRootId,
    string? StartFolder = null,
    bool IncludeSubfolders = true,
    bool SkipConfidentAssignments = false,
    bool OnlyUnclearItems = false,
    bool AnalyzeFilenameEvidence = true);

public enum LibraryReconciliationPlanOutcome
{
    Created = 1,
    RootNotFound = 2,
    RootDisabled = 3,
    InvalidFolder = 4,
    StorageUnavailable = 5,
    PlanNotFound = 6,
    AlreadyScanned = 7,
    InvalidPlanState = 8,
    ScanFailed = 9,
    Scanned = 10
}

/// <summary>Result of attempting to create a bounded reconciliation draft.</summary>
public sealed record LibraryReconciliationPlanResult(LibraryReconciliationPlanOutcome Outcome, LibraryReconciliationPlan? Plan = null, string? Message = null)
{
    public bool Succeeded => Outcome == LibraryReconciliationPlanOutcome.Created;
}

/// <summary>Read-only scan result for the first wizard step. Every count is advisory until mapping is reviewed.</summary>
public sealed record LibraryReconciliationScanResult(
    LibraryReconciliationPlanOutcome Outcome,
    LibraryReconciliationPlan? Plan = null,
    int FilesFound = 0,
    int UnclearItems = 0,
    int IgnoredItems = 0,
    int HardErrors = 0,
    string? Message = null)
{
    public bool Succeeded => Outcome is LibraryReconciliationPlanOutcome.Scanned or LibraryReconciliationPlanOutcome.AlreadyScanned;
}

/// <summary>One canonical work result an admin may explicitly select while mapping a reconciliation plan.</summary>
public sealed record LibraryReconciliationWorkCandidate(long Id, string Title, int? Year);

/// <summary>One existing canonical episode available for explicit selection after a reconciliation file has an effective work.</summary>
public sealed record LibraryReconciliationEpisodeCandidate(Guid Id, int SeasonNumber, int EpisodeNumber, bool IsSpecial, string? Title, bool IsAssigned);

/// <summary>One existing canonical volume available for explicit selection after a reconciliation file has an effective work.</summary>
public sealed record LibraryReconciliationVolumeCandidate(Guid Id, int Number, string? Title, bool IsAssigned);

/// <summary>One existing canonical chapter available for explicit selection after a reconciliation file has an effective work.</summary>
public sealed record LibraryReconciliationChapterCandidate(Guid Id, double Number, string? Title, bool IsAssigned);

/// <summary>One existing editorial variant available after a reconciliation file resolves to a canonical work.</summary>
public sealed record LibraryReconciliationEditionCandidate(Guid Id, string EditionKey, string Language, string? Format, string? Title, bool IsAssigned);

/// <summary>One existing concrete release available after a reconciliation file resolves to a canonical work and optional edition.</summary>
public sealed record LibraryReconciliationVersionCandidate(Guid Id, string VersionKey, string? UnitKey, string? Quality, string? Source, bool IsAssigned);

/// <summary>The effective work of one plan entry, including whether it is inherited from an explicitly mapped parent folder.</summary>
public sealed record LibraryReconciliationResolvedWork(long Id, string Title, int? Year, bool IsInherited, string SourcePath);

/// <summary>One enabled configured library root available as the bounded starting point for a reconciliation plan.</summary>
public sealed record LibraryReconciliationRoot(Guid Id, string Name);

/// <summary>One persisted plan with its configured root name and complete root-relative inventory for admin review.</summary>
public sealed record LibraryReconciliationPlanReview(LibraryReconciliationPlan Plan, string LibraryRootName, IReadOnlyList<LibraryReconciliationPlanItem> Items);

/// <summary>Explicit admin mapping of one or more plan entries to an existing canonical work.</summary>
public sealed record LibraryReconciliationWorkAssignmentRequest(Guid PlanId, IReadOnlyCollection<Guid> ItemIds, long WorkId);

public enum LibraryReconciliationWorkAssignmentOutcome
{
    Assigned = 1,
    PlanNotFound = 2,
    InvalidPlanState = 3,
    WorkNotFound = 4,
    ItemsNotFound = 5,
    IgnoredItem = 6,
    Reset = 7,
    Ignored = 8,
    Restored = 9,
    DirectoryItem = 10,
    NotExplicitlyIgnored = 11,
    EpisodeNotFound = 12,
    EpisodeAssigned = 13,
    EpisodeReset = 14,
    EpisodeSequenceAssigned = 15,
    EpisodeSequenceOutOfRange = 16,
    VolumeNotFound = 17,
    VolumeAssigned = 18,
    VolumeReset = 19,
    ChapterNotFound = 20,
    ChapterAssigned = 21,
    ChapterReset = 22,
    OrganizationModeSaved = 23,
    LogicalGroupCreated = 24,
    LogicalGroupCleared = 25,
    ReleaseMetadataSaved = 26
}

/// <summary>Result of a non-destructive explicit work assignment; no asset or filesystem state is changed.</summary>
public sealed record LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome Outcome, string? Message = null)
{
    public bool Succeeded
    {
        get
        {
            return Outcome is
                LibraryReconciliationWorkAssignmentOutcome.Assigned or
                LibraryReconciliationWorkAssignmentOutcome.Reset or
                LibraryReconciliationWorkAssignmentOutcome.Ignored or
                LibraryReconciliationWorkAssignmentOutcome.Restored or
                LibraryReconciliationWorkAssignmentOutcome.EpisodeAssigned or
                LibraryReconciliationWorkAssignmentOutcome.EpisodeReset or
                LibraryReconciliationWorkAssignmentOutcome.EpisodeSequenceAssigned or
                LibraryReconciliationWorkAssignmentOutcome.VolumeAssigned or
                LibraryReconciliationWorkAssignmentOutcome.VolumeReset or
                LibraryReconciliationWorkAssignmentOutcome.ChapterAssigned or
                LibraryReconciliationWorkAssignmentOutcome.ChapterReset or
                LibraryReconciliationWorkAssignmentOutcome.OrganizationModeSaved or
                LibraryReconciliationWorkAssignmentOutcome.LogicalGroupCreated or
                LibraryReconciliationWorkAssignmentOutcome.LogicalGroupCleared or
                LibraryReconciliationWorkAssignmentOutcome.ReleaseMetadataSaved;
        }
    }
}

/// <summary>Request to remove one explicit work selection from a plan entry while preserving the physical inventory.</summary>
public sealed record LibraryReconciliationWorkResetRequest(Guid PlanId, Guid ItemId);

/// <summary>Request to remove all direct mapping overrides from selected plan entries while preserving their physical inventory.</summary>
public sealed record LibraryReconciliationBatchWorkResetRequest(Guid PlanId, IReadOnlyCollection<Guid> ItemIds);

/// <summary>Request to explicitly skip or restore only media files already contained by one reconciliation plan.</summary>
public sealed record LibraryReconciliationItemIgnoreRequest(Guid PlanId, IReadOnlyCollection<Guid> ItemIds, bool IsIgnored);

/// <summary>Request to apply or clear one existing canonical episode on a single scanned media file.</summary>
public sealed record LibraryReconciliationEpisodeAssignmentRequest(Guid PlanId, Guid ItemId, Guid? EpisodeId);

/// <summary>Request to map checked files to a consecutive existing episode range in their displayed, administrator-confirmed order.</summary>
public sealed record LibraryReconciliationEpisodeSequenceRequest(Guid PlanId, IReadOnlyList<Guid> ItemIds, Guid StartEpisodeId, Guid? EndEpisodeId = null);

/// <summary>Request to apply or clear one existing canonical volume on a single scanned media file.</summary>
public sealed record LibraryReconciliationVolumeAssignmentRequest(Guid PlanId, Guid ItemId, Guid? VolumeId);

/// <summary>Request to apply or clear one existing canonical chapter on a single scanned media file.</summary>
public sealed record LibraryReconciliationChapterAssignmentRequest(Guid PlanId, Guid ItemId, Guid? ChapterId);

/// <summary>Request to persist the mutually exclusive organization policy and explicit empty-source-folder cleanup choice for one ready reconciliation plan.</summary>
public sealed record LibraryReconciliationOrganizationModeRequest(Guid PlanId, LibraryReconciliationOrganizationMode OrganizationMode, bool RemoveEmptySourceFolders = false);

/// <summary>Assigns checked scanned files to a new, plan-only logical group for an explicit Split or Merge review decision.</summary>
public sealed record LibraryReconciliationLogicalGroupRequest(Guid PlanId, IReadOnlyCollection<Guid> ItemIds, string Name);

/// <summary>Removes the logical-group membership from checked files while preserving every physical path and canonical mapping.</summary>
public sealed record LibraryReconciliationLogicalGroupClearRequest(Guid PlanId, IReadOnlyCollection<Guid> ItemIds);

/// <summary>One plan-only split or merge group with the number of currently assigned media files.</summary>
public sealed record LibraryReconciliationLogicalGroupSummary(Guid Id, string Name, int FileCount);

/// <summary>Applies existing edition/version choices and manually confirmed release metadata to one scanned file.</summary>
public sealed record LibraryReconciliationReleaseMetadataRequest(Guid PlanId, Guid ItemId, Guid? EditionId, Guid? VersionId, string? Language, string? AudioLanguage, string? SubtitleLanguage, string? QualitySource);

/// <summary>Field-by-field release defaults resolved from the selected entry and its nearest mapped ancestors without copying them into child plan rows.</summary>
public sealed record LibraryReconciliationResolvedReleaseMetadata(
    Guid? EditionId,
    string? EditionSourcePath,
    Guid? VersionId,
    string? VersionSourcePath,
    string? Language,
    string? LanguageSourcePath,
    string? AudioLanguage,
    string? AudioLanguageSourcePath,
    string? SubtitleLanguage,
    string? SubtitleLanguageSourcePath,
    string? QualitySource,
    string? QualitySourcePath);

/// <summary>Applies manually confirmed non-identity release defaults to selected current media files without changing their canonical Work, edition, version or unit.</summary>
public sealed record LibraryReconciliationBatchReleaseMetadataRequest(Guid PlanId, IReadOnlyCollection<Guid> ItemIds, string? Language, string? AudioLanguage, string? SubtitleLanguage, string? QualitySource);

/// <summary>One filesystem and canonical-link outcome calculated before reconciliation execution is allowed.</summary>
public enum LibraryReconciliationPreviewAction
{
    AssignOnly = 1,
    Rename = 2,
    MoveAndRename = 3,
    KeepIgnored = 4,
    Blocked = 5,
    Conflict = 6
}

/// <summary>One root-relative file row in the non-destructive dry-run, including its exact intended destination when applicable.</summary>
public sealed record LibraryReconciliationPreviewItem(string RelativePath, string? TargetRelativePath, string? WorkTitle, LibraryReconciliationPreviewAction Action, bool IsInherited, string? Reason = null);

/// <summary>Complete fingerprinted dry-run whose exact current plan must be confirmed before execution can start.</summary>
public sealed record LibraryReconciliationPreview(IReadOnlyList<LibraryReconciliationPreviewItem> Items, string Fingerprint)
{
    public int AssignmentsOnly => Items.Count(x => x.Action == LibraryReconciliationPreviewAction.AssignOnly);

    public int Renames => Items.Count(x => x.Action == LibraryReconciliationPreviewAction.Rename);

    public int Moves => Items.Count(x => x.Action == LibraryReconciliationPreviewAction.MoveAndRename);

    public int Ignored => Items.Count(x => x.Action == LibraryReconciliationPreviewAction.KeepIgnored);

    public int Conflicts => Items.Count(x => x.Action == LibraryReconciliationPreviewAction.Conflict);

    public int Unresolved => Items.Count(x => x.Action == LibraryReconciliationPreviewAction.Blocked);

    public int Blockers => Items.Count(x => x.Action is LibraryReconciliationPreviewAction.Blocked or LibraryReconciliationPreviewAction.Conflict);

    public bool CanExecute => Blockers == 0 && Items.Any(x => x.Action is LibraryReconciliationPreviewAction.AssignOnly or LibraryReconciliationPreviewAction.Rename or LibraryReconciliationPreviewAction.MoveAndRename);
}

/// <summary>Explicit confirmation of the exact fingerprint shown by a current reconciliation preview.</summary>
public sealed record LibraryReconciliationExecutionRequest(Guid PlanId, string PreviewFingerprint);

/// <summary>Result of starting and completing one visible reconciliation operation.</summary>
public sealed record LibraryReconciliationExecutionResult(bool Succeeded, string Message, Guid? OperationId = null);
