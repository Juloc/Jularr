using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Storage.Reconciliation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin.Reconciliation;

/// <summary>Serves the root-bound, non-destructive review stages of the library reconciliation wizard.</summary>
[Authorize(Policy = JularrPolicies.AdminSystem)]
public sealed class IndexModel(AppDbContext db, LibraryReconciliationPlanService reconciliation) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public int CurrentStep { get; private set; } = 1;

    public IReadOnlyList<LibraryReconciliationRoot> Roots { get; private set; } = [];

    public ReconciliationPlanView? Plan { get; private set; }

    public IReadOnlyList<LibraryReconciliationPlanItem> Items { get; private set; } = [];

    public IReadOnlyList<ReconciliationTreeNode> Tree { get; private set; } = [];

    public LibraryReconciliationPlanItem? SelectedItem { get; private set; }

    public IReadOnlyList<LibraryReconciliationWorkCandidate> WorkCandidates { get; private set; } = [];

    public LibraryReconciliationResolvedWork? SelectedWork { get; private set; }

    public LibraryReconciliationResolvedReleaseMetadata? ResolvedReleaseMetadata { get; private set; }

    public IReadOnlyList<LibraryReconciliationEpisodeCandidate> EpisodeCandidates { get; private set; } = [];

    public IReadOnlyList<LibraryReconciliationVolumeCandidate> VolumeCandidates { get; private set; } = [];

    public IReadOnlyList<LibraryReconciliationChapterCandidate> ChapterCandidates { get; private set; } = [];

    public IReadOnlyList<LibraryReconciliationEditionCandidate> EditionCandidates { get; private set; } = [];

    public IReadOnlyList<LibraryReconciliationVersionCandidate> VersionCandidates { get; private set; } = [];

    public IReadOnlyList<LibraryReconciliationPlanItem> MappingItems { get; private set; } = [];

    public IReadOnlyList<LibraryReconciliationLogicalGroupSummary> LogicalGroups { get; private set; } = [];

    public LibraryReconciliationPreview? Preview { get; private set; }

    public LibraryReconciliationPreviewItem? OrganizationPreviewItem { get; private set; }

    /// <summary>The folder whose files form the step-3 table: the selected folder, or the parent folder of a selected file.</summary>
    public LibraryReconciliationPlanItem? ScopeItem { get; private set; }

    /// <summary>Media files below <see cref="ScopeItem"/>, including skipped ones so that a skip stays reversible.</summary>
    public IReadOnlyList<LibraryReconciliationPlanItem> ScopeFiles { get; private set; } = [];

    /// <summary>Direct media files of the folder selected in the structure step.</summary>
    public IReadOnlyList<LibraryReconciliationPlanItem> SelectedFolderFiles { get; private set; } = [];

    /// <summary>Display labels of the canonical units already chosen for scope files, keyed by canonical unit id.</summary>
    public IReadOnlyDictionary<Guid, string> UnitLabels { get; private set; } = new Dictionary<Guid, string>();

    public int FileCount => Items.Count(x => !x.IsDirectory);

    public int UnclearCount => Items.Count(x => !x.IsDirectory && x.State == LibraryReconciliationItemState.Unclear);

    public int RecognizedCount => Items.Count(x => !x.IsDirectory && x.State == LibraryReconciliationItemState.Recognized);

    public int HardErrorCount => Items.Count(x => !x.IsDirectory && x.State == LibraryReconciliationItemState.Error);

    public int Percent(int count) => FileCount == 0 ? 0 : (int)Math.Round(count * 100d / FileCount);

    [BindProperty(SupportsGet = true)]
    public Guid LibraryRootId { get; set; }

    [BindProperty]
    public string? StartFolder { get; set; }

    [BindProperty]
    public bool IncludeSubfolders { get; set; } = true;

    [BindProperty]
    public bool SkipConfidentAssignments { get; set; }

    [BindProperty]
    public bool OnlyUnclearItems { get; set; }

    [BindProperty]
    public bool AnalyzeFilenameEvidence { get; set; } = true;

    [BindProperty(SupportsGet = true)]
    public string? WorkQuery { get; set; }

    /// <summary>Loads either a new plan form or an existing root-bound scan, selected entry and optional read-only preview.</summary>
    public async Task<IActionResult> OnGetAsync(Guid? planId, string? selected, bool preview, int? step, CancellationToken cancellationToken)
    {
        // Navigation: view selection never mutates the durable plan and remains bounded to its five approved stages.
        CurrentStep = Math.Clamp(step ?? (preview ? 5 : string.IsNullOrWhiteSpace(selected) ? 1 : 3), 1, 5);

        // Resume: a valid ready scan is offered again instead of forcing the administrator to rescan.
        planId ??= await reconciliation.GetLatestReadyPlanIdAsync(cancellationToken);
        await LoadAsync(planId, selected, WorkQuery, preview || CurrentStep == 5, cancellationToken);
        if (Plan is not null)
        {
            LibraryRootId = Plan.LibraryRootId;
            StartFolder = Plan.StartFolder;
            IncludeSubfolders = Plan.IncludeSubfolders;
            SkipConfidentAssignments = Plan.SkipConfidentAssignments;
            OnlyUnclearItems = Plan.OnlyUnclearItems;
            AnalyzeFilenameEvidence = Plan.AnalyzeFilenameEvidence;
        }

        return Page();
    }

    /// <summary>Validates and persists a draft, then runs its read-only inventory; the request never assigns, renames, moves or deletes any file.</summary>
    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        await LoadRootsAsync(cancellationToken);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await reconciliation.CreateAsync(new LibraryReconciliationPlanRequest(LibraryRootId, StartFolder, IncludeSubfolders, SkipConfidentAssignments, OnlyUnclearItems, AnalyzeFilenameEvidence), cancellationToken);
        if (!result.Succeeded || result.Plan is null)
        {
            ModelState.AddModelError(string.Empty, result.Message ?? Ui["admin.reconciliation.error"]);
            return Page();
        }

        var scan = await reconciliation.ScanAsync(result.Plan.Id, cancellationToken);
        TempData["Status"] = scan.Message ?? (scan.Succeeded ? $"{scan.FilesFound} {Ui["admin.reconciliation.files"].ToLowerInvariant()}." : Ui["admin.reconciliation.error"]);
        return RedirectToPage(new { planId = result.Plan.Id });
    }

    /// <summary>Runs the root-bound read-only inventory and returns to the same draft for review.</summary>
    public async Task<IActionResult> OnPostScanAsync(Guid planId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var result = await reconciliation.ScanAsync(planId, cancellationToken);
        TempData["Status"] = result.Message ?? (result.Succeeded ? $"{result.FilesFound} {Ui["admin.reconciliation.files"].ToLowerInvariant()}." : Ui["admin.reconciliation.error"]);
        return RedirectToPage(new { planId });
    }

    /// <summary>Persists one explicit work choice for the server-rendered selection without creating filesystem or canonical media state.</summary>
    public async Task<IActionResult> OnPostAssignWorkAsync(Guid planId, Guid workId, Guid[]? itemIds, string? selected, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var request = new LibraryReconciliationWorkAssignmentRequest(planId, itemIds ?? [], workId);
        var result = await reconciliation.AssignWorkAsync(request, cancellationToken);
        TempData["Status"] = result.Succeeded ? Ui["admin.reconciliation.mappingSaved"] : result.Message ?? Ui["admin.reconciliation.error"];
        return RedirectToPage(new { planId, selected, step = 3 });
    }

    /// <summary>Persists a reversible skip decision for selected media files without changing their filesystem or canonical media state.</summary>
    public async Task<IActionResult> OnPostSetIgnoredAsync(Guid planId, Guid[]? itemIds, bool isIgnored, string? selected, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var result = await reconciliation.SetIgnoredAsync(new LibraryReconciliationItemIgnoreRequest(planId, itemIds ?? [], isIgnored), cancellationToken);
        TempData["Status"] = result.Succeeded ? Ui[isIgnored ? "admin.reconciliation.skipSaved" : "admin.reconciliation.skipRestored"] : result.Message ?? Ui["admin.reconciliation.error"];
        return RedirectToPage(new { planId, selected, step = 3 });
    }

    /// <summary>Creates a reversible logical split or merge group from checked media files in the current reconciliation plan.</summary>
    public async Task<IActionResult> OnPostCreateLogicalGroupAsync(Guid planId, string groupName, Guid[]? itemIds, string? selected, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var result = await reconciliation.CreateLogicalGroupAsync(new LibraryReconciliationLogicalGroupRequest(planId, itemIds ?? [], groupName), cancellationToken);
        TempData["Status"] = result.Succeeded ? Ui["admin.reconciliation.groupSaved"] : result.Message ?? Ui["admin.reconciliation.error"];
        return RedirectToPage(new { planId, selected, step = 3 });
    }

    /// <summary>Removes checked media files from their plan-only split or merge groups without changing their mappings or paths.</summary>
    public async Task<IActionResult> OnPostClearLogicalGroupAsync(Guid planId, Guid[]? itemIds, string? selected, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var result = await reconciliation.ClearLogicalGroupAsync(new LibraryReconciliationLogicalGroupClearRequest(planId, itemIds ?? []), cancellationToken);
        TempData["Status"] = result.Succeeded ? Ui["admin.reconciliation.groupCleared"] : result.Message ?? Ui["admin.reconciliation.error"];
        return RedirectToPage(new { planId, selected, step = 3 });
    }

    /// <summary>Persists or clears one explicit canonical episode for a selected media file without changing filesystem or media records.</summary>
    public async Task<IActionResult> OnPostAssignEpisodeAsync(Guid planId, Guid itemId, Guid? episodeId, string? selected, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var result = await reconciliation.AssignEpisodeAsync(new LibraryReconciliationEpisodeAssignmentRequest(planId, itemId, episodeId), cancellationToken);
        TempData["Status"] = result.Succeeded ? Ui[episodeId is null ? "admin.reconciliation.episodeReset" : "admin.reconciliation.episodeSaved"] : result.Message ?? Ui["admin.reconciliation.error"];
        return RedirectToPage(new { planId, selected, step = 3 });
    }

    /// <summary>Persists or clears one explicit canonical volume for a selected media file without changing filesystem or media records.</summary>
    public async Task<IActionResult> OnPostAssignVolumeAsync(Guid planId, Guid itemId, Guid? volumeId, string? selected, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var result = await reconciliation.AssignVolumeAsync(new LibraryReconciliationVolumeAssignmentRequest(planId, itemId, volumeId), cancellationToken);
        TempData["Status"] = result.Succeeded ? Ui[volumeId is null ? "admin.reconciliation.volumeReset" : "admin.reconciliation.volumeSaved"] : result.Message ?? Ui["admin.reconciliation.error"];
        return RedirectToPage(new { planId, selected, step = 3 });
    }

    /// <summary>Persists or clears one explicit canonical chapter for a selected media file without changing filesystem or media records.</summary>
    public async Task<IActionResult> OnPostAssignChapterAsync(Guid planId, Guid itemId, Guid? chapterId, string? selected, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var result = await reconciliation.AssignChapterAsync(new LibraryReconciliationChapterAssignmentRequest(planId, itemId, chapterId), cancellationToken);
        TempData["Status"] = result.Succeeded ? Ui[chapterId is null ? "admin.reconciliation.chapterReset" : "admin.reconciliation.chapterSaved"] : result.Message ?? Ui["admin.reconciliation.error"];
        return RedirectToPage(new { planId, selected, step = 3 });
    }

    /// <summary>Persists existing edition/version choices and manual release metadata for one selected file without creating canonical records or changing paths.</summary>
    public async Task<IActionResult> OnPostAssignReleaseMetadataAsync(
        Guid planId,
        Guid itemId,
        Guid? editionId,
        Guid? versionId,
        string? language,
        string? audioLanguage,
        string? subtitleLanguage,
        string? qualitySource,
        string? selected,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var request = new LibraryReconciliationReleaseMetadataRequest(
            planId,
            itemId,
            editionId,
            versionId,
            language,
            audioLanguage,
            subtitleLanguage,
            qualitySource);
        var result = await reconciliation.AssignReleaseMetadataAsync(request, cancellationToken);
        TempData["Status"] = result.Succeeded ? Ui["admin.reconciliation.releaseMetadataSaved"] : result.Message ?? Ui["admin.reconciliation.error"];
        return RedirectToPage(new { planId, selected, step = 3 });
    }

    /// <summary>Applies manually confirmed non-identity release defaults to checked media files without replacing their canonical work, edition, version or unit.</summary>
    public async Task<IActionResult> OnPostAssignBatchReleaseMetadataAsync(
        Guid planId,
        Guid[]? itemIds,
        string? language,
        string? audioLanguage,
        string? subtitleLanguage,
        string? qualitySource,
        string? selected,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var request = new LibraryReconciliationBatchReleaseMetadataRequest(planId, itemIds ?? [], language, audioLanguage, subtitleLanguage, qualitySource);
        var result = await reconciliation.AssignBatchReleaseMetadataAsync(request, cancellationToken);
        TempData["Status"] = result.Succeeded ? Ui["admin.reconciliation.batchReleaseMetadataSaved"] : result.Message ?? Ui["admin.reconciliation.error"];
        return RedirectToPage(new { planId, selected, step = 3 });
    }

    /// <summary>Persists one mutually exclusive organization policy without starting a filesystem operation.</summary>
    public async Task<IActionResult> OnPostSetOrganizationModeAsync(Guid planId, LibraryReconciliationOrganizationMode organizationMode, bool removeEmptySourceFolders, int? returnStep, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var result = await reconciliation.SetOrganizationModeAsync(new LibraryReconciliationOrganizationModeRequest(planId, organizationMode, removeEmptySourceFolders), cancellationToken);
        TempData["Status"] = result.Succeeded ? Ui["admin.reconciliation.organizationSaved"] : result.Message ?? Ui["admin.reconciliation.error"];
        return RedirectToPage(new { planId, step = returnStep == 5 ? 5 : 4 });
    }

    /// <summary>Confirms and starts only the exact current conflict-free preview as an Activity-visible reconciliation operation.</summary>
    public async Task<IActionResult> OnPostExecuteAsync(Guid planId, string previewFingerprint, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var result = await reconciliation.ExecuteAsync(new LibraryReconciliationExecutionRequest(planId, previewFingerprint), cancellationToken);
        TempData["Status"] = result.Succeeded ? Ui["admin.reconciliation.executionCompleted"] : result.Message;
        return RedirectToPage(new { planId, step = 5, preview = true });
    }

    /// <summary>Maps checked media files to consecutive existing episodes in their displayed order without changing filesystem or media records.</summary>
    public async Task<IActionResult> OnPostAssignEpisodeSequenceAsync(Guid planId, Guid startEpisodeId, Guid? endEpisodeId, Guid[]? itemIds, string? selected, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var request = new LibraryReconciliationEpisodeSequenceRequest(planId, itemIds ?? [], startEpisodeId, endEpisodeId);
        var result = await reconciliation.AssignEpisodeSequenceAsync(request, cancellationToken);
        TempData["Status"] = result.Succeeded ? Ui["admin.reconciliation.episodeSequenceSaved"] : result.Message ?? Ui["admin.reconciliation.error"];
        return RedirectToPage(new { planId, selected, step = 3 });
    }

    /// <summary>Removes the selected entry's explicit work choice without changing the filesystem or canonical media state.</summary>
    public async Task<IActionResult> OnPostResetWorkAsync(Guid planId, Guid itemId, string? selected, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var result = await reconciliation.ResetWorkAsync(new LibraryReconciliationWorkResetRequest(planId, itemId), cancellationToken);
        TempData["Status"] = result.Outcome == LibraryReconciliationWorkAssignmentOutcome.Reset ? Ui["admin.reconciliation.mappingReset"] : result.Message ?? Ui["admin.reconciliation.error"];
        return RedirectToPage(new { planId, selected, step = 3 });
    }

    /// <summary>Removes direct work, unit and release overrides from checked entries without changing files or canonical media records.</summary>
    public async Task<IActionResult> OnPostResetWorksAsync(Guid planId, Guid[]? itemIds, string? selected, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var result = await reconciliation.ResetWorksAsync(new LibraryReconciliationBatchWorkResetRequest(planId, itemIds ?? []), cancellationToken);
        TempData["Status"] = result.Outcome == LibraryReconciliationWorkAssignmentOutcome.Reset ? Ui["admin.reconciliation.mappingReset"] : result.Message ?? Ui["admin.reconciliation.error"];
        return RedirectToPage(new { planId, selected, step = 3 });
    }

    /// <summary>Loads a plan only through its configured root and exposes its persisted scan state and optional dry-run for the page.</summary>
    private async Task LoadAsync(Guid? planId, string? selected, string? workQuery, bool includePreview, CancellationToken cancellationToken)
    {
        await LoadRootsAsync(cancellationToken);
        if (planId is not { } id)
        {
            return;
        }

        // Load the persisted scan only through the reconciliation backend boundary.
        var review = await reconciliation.GetReviewAsync(id, cancellationToken);
        if (review is null)
        {
            return;
        }

        // Derive the page-only hierarchy and selected detail from the immutable review result.
        Plan = new ReconciliationPlanView(
            review.Plan.Id,
            review.Plan.LibraryRootId,
            review.LibraryRootName,
            review.Plan.StartFolder,
            review.Plan.IncludeSubfolders,
            review.Plan.SkipConfidentAssignments,
            review.Plan.AnalyzeFilenameEvidence,
            review.Plan.ScannedAtUtc,
            review.Plan.Status,
            review.Plan.Failure,
            review.Plan.OnlyUnclearItems,
            review.Plan.OrganizationMode,
            review.Plan.RemoveEmptySourceFolders,
            review.Plan.ExecutionOperationId);
        Items = review.Items;
        Tree = BuildTree(Plan.OnlyUnclearItems ? FilterToUnclear(Items) : Items);
        LogicalGroups = await reconciliation.GetLogicalGroupsAsync(id, cancellationToken);
        if (string.IsNullOrWhiteSpace(selected) && CurrentStep is 2 or 3)
        {
            selected = FindFirstFolderNeedingReview(Items);
        }

        SelectedItem = string.IsNullOrWhiteSpace(selected) ? null :Items.SingleOrDefault(x => string.Equals(x.RelativePath, selected, StringComparison.Ordinal));
        if (SelectedItem is not null)
        {
            SelectedWork = await reconciliation.GetResolvedWorkAsync(id, SelectedItem.Id, cancellationToken);
            ResolvedReleaseMetadata = await reconciliation.GetResolvedReleaseMetadataAsync(id, SelectedItem.Id, cancellationToken);
            MappingItems = GetMappingItems(Items, SelectedItem);
            var scopePath = SelectedItem.IsDirectory ? SelectedItem.RelativePath : GetParentPath(SelectedItem.RelativePath);
            var scopePrefix = scopePath + "/";
            ScopeItem = Items.SingleOrDefault(x => x.IsDirectory && string.Equals(x.RelativePath, scopePath, StringComparison.Ordinal)) ?? SelectedItem;
            ScopeFiles = Items.Where(x => !x.IsDirectory && (scopePath.Length == 0 || x.RelativePath.StartsWith(scopePrefix, StringComparison.Ordinal))).ToArray();
            SelectedFolderFiles = SelectedItem.IsDirectory ? ScopeFiles.Where(x => string.Equals(GetParentPath(x.RelativePath), scopePath, StringComparison.Ordinal)).ToArray() : [];
            if (Plan.Status == LibraryReconciliationPlanStatus.Ready && SelectedWork is not null)
            {
                EpisodeCandidates = await reconciliation.GetEpisodeCandidatesAsync(Plan.Id, SelectedItem.Id, cancellationToken);
                VolumeCandidates = await reconciliation.GetVolumeCandidatesAsync(Plan.Id, SelectedItem.Id, cancellationToken);
                ChapterCandidates = await reconciliation.GetChapterCandidatesAsync(Plan.Id, SelectedItem.Id, cancellationToken);
                EditionCandidates = await reconciliation.GetEditionCandidatesAsync(Plan.Id, SelectedItem.Id, cancellationToken);
                VersionCandidates = await reconciliation.GetVersionCandidatesAsync(Plan.Id, SelectedItem.Id, cancellationToken);
                var labels = new Dictionary<Guid, string>();
                foreach (var episode in EpisodeCandidates)
                {
                    labels[episode.Id] = $"S{episode.SeasonNumber:D2} E{episode.EpisodeNumber:D2}";
                }

                foreach (var volume in VolumeCandidates)
                {
                    labels[volume.Id] = $"V{volume.Number:D2}";
                }

                foreach (var chapter in ChapterCandidates)
                {
                    labels[chapter.Id] = $"C{chapter.Number:0.##}";
                }

                UnitLabels = labels;
            }
        }

        if (Plan.Status == LibraryReconciliationPlanStatus.Ready && SelectedItem is not null && CurrentStep is 2 or 3)
        {
            // The picker lists existing canonical works only; a typed query narrows the list and never infers a mapping.
            WorkCandidates = await reconciliation.SearchWorksAsync(Plan.Id, workQuery, cancellationToken);
        }

        if (Plan.Status == LibraryReconciliationPlanStatus.Ready)
        {
            // Build the same read-only result for the organization example and full confirmation screen; it cannot execute any operation.
            var organizationPreview = await reconciliation.BuildPreviewAsync(Plan.Id, cancellationToken);
            OrganizationPreviewItem = organizationPreview?.Items.FirstOrDefault(x =>
                x.Action is LibraryReconciliationPreviewAction.AssignOnly or
                LibraryReconciliationPreviewAction.Rename or
                LibraryReconciliationPreviewAction.MoveAndRename);
            if (includePreview)
            {
                Preview = organizationPreview;
            }
        }
    }

    /// <summary>Loads only enabled library roots and preserves a valid preselected root from the entry link.</summary>
    private async Task LoadRootsAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Roots = await reconciliation.GetEnabledRootsAsync(cancellationToken);
        if (LibraryRootId == Guid.Empty && Roots.Count > 0)
        {
            LibraryRootId = Roots[0].Id;
        }
    }

    /// <summary>Minimal plan data rendered by the page without duplicating persistence ownership.</summary>
    public sealed record ReconciliationPlanView(
        Guid Id,
        Guid LibraryRootId,
        string RootName,
        string? StartFolder,
        bool IncludeSubfolders,
        bool SkipConfidentAssignments,
        bool AnalyzeFilenameEvidence,
        DateTime? ScannedAtUtc,
        LibraryReconciliationPlanStatus Status,
        string? Failure,
        bool OnlyUnclearItems,
        LibraryReconciliationOrganizationMode OrganizationMode,
        bool RemoveEmptySourceFolders,
        Guid? ExecutionOperationId);

    /// <summary>One physical inventory entry with its recursively rendered child entries.</summary>
    public sealed class ReconciliationTreeNode(LibraryReconciliationPlanItem item)
    {
        public LibraryReconciliationPlanItem Item { get; } = item;

        public List<ReconciliationTreeNode> Children { get; } = [];
    }

    /// <summary>Render context for one tree partial, including its plan and selected physical path.</summary>
    public sealed record ReconciliationTreeNodeRender(
        ReconciliationTreeNode Node,
        Guid PlanId,
        string? SelectedPath,
        Func<LibraryReconciliationItemState, ReconciliationStatePresentation> Present);

    /// <summary>Returns localized text and a stable CSS token for every persisted reconciliation state.</summary>
    public ReconciliationStatePresentation GetStatePresentation(LibraryReconciliationItemState state)
    {
        var textKey = state switch
        {
            LibraryReconciliationItemState.Recognized => "admin.reconciliation.recognized",
            LibraryReconciliationItemState.Partial => "admin.reconciliation.partial",
            LibraryReconciliationItemState.Unclear => "admin.reconciliation.unassigned",
            LibraryReconciliationItemState.Error => "admin.reconciliation.errorState",
            _ => "admin.reconciliation.kept"
        };
        return new ReconciliationStatePresentation(Ui[textKey], state.ToString().ToLowerInvariant());
    }

    /// <summary>Returns localized, semantically styled text for every root-bound dry-run outcome.</summary>
    public ReconciliationStatePresentation GetPreviewActionPresentation(LibraryReconciliationPreviewAction action)
    {
        var textKey = action switch
        {
            LibraryReconciliationPreviewAction.AssignOnly => "admin.reconciliation.previewAssign",
            LibraryReconciliationPreviewAction.Rename => "admin.reconciliation.previewRename",
            LibraryReconciliationPreviewAction.MoveAndRename => "admin.reconciliation.previewMove",
            LibraryReconciliationPreviewAction.KeepIgnored => "admin.reconciliation.previewIgnored",
            LibraryReconciliationPreviewAction.Conflict => "admin.reconciliation.previewConflict",
            _ => "admin.reconciliation.previewBlocked"
        };
        var cssClass = action switch
        {
            LibraryReconciliationPreviewAction.AssignOnly => "recognized",
            LibraryReconciliationPreviewAction.Rename or LibraryReconciliationPreviewAction.MoveAndRename => "partial",
            LibraryReconciliationPreviewAction.KeepIgnored => "ignored",
            _ => "error"
        };
        return new ReconciliationStatePresentation(Ui[textKey], cssClass);
    }

    /// <summary>Localized state text paired with a semantic CSS token for the current theme.</summary>
    public sealed record ReconciliationStatePresentation(string Text, string CssClass);

    /// <summary>Returns the selected physical entry and its reviewable media descendants as a bounded batch-mapping scope.</summary>
    private static IReadOnlyList<LibraryReconciliationPlanItem> GetMappingItems(IReadOnlyList<LibraryReconciliationPlanItem> items, LibraryReconciliationPlanItem selectedItem)
    {
        if (!selectedItem.IsDirectory)
        {
            return selectedItem.State == LibraryReconciliationItemState.Ignored ? [] : [selectedItem];
        }

        // Keep the selected folder as the inheritable default and enumerate only its reviewable file descendants.
        var prefix = selectedItem.RelativePath + "/";
        var childFiles = items.Where(x => !x.IsDirectory && x.State != LibraryReconciliationItemState.Ignored && x.RelativePath.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        return [selectedItem, .. childFiles];
    }

    /// <summary>Preselects the first folder, in path order, that directly holds unresolved files; falls back to the first folder so the detail pane is never empty.</summary>
    private static string? FindFirstFolderNeedingReview(IReadOnlyList<LibraryReconciliationPlanItem> items)
    {
        var folders = items.Where(x => x.IsDirectory).OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var directFilePaths = items.Where(x => !x.IsDirectory && x.State != LibraryReconciliationItemState.Recognized && x.State != LibraryReconciliationItemState.Ignored).Select(x => GetParentPath(x.RelativePath)).ToHashSet(StringComparer.Ordinal);
        return (folders.FirstOrDefault(x => directFilePaths.Contains(x.RelativePath)) ?? folders.FirstOrDefault())?.RelativePath;
    }

    private static string GetParentPath(string relativePath)
    {
        var separator = relativePath.LastIndexOf('/');
        return separator < 0 ? string.Empty : relativePath[..separator];
    }

    /// <summary>Keeps unclear or failed entries together with the folders that lead to them so the filtered tree stays navigable.</summary>
    private static IReadOnlyList<LibraryReconciliationPlanItem> FilterToUnclear(IReadOnlyList<LibraryReconciliationPlanItem> items)
    {
        var visiblePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.Where(x => x.State is LibraryReconciliationItemState.Unclear or LibraryReconciliationItemState.Error))
        {
            for (var path = item.RelativePath; path.Length > 0; path = GetParentPath(path))
            {
                visiblePaths.Add(path);
            }
        }

        return items.Where(x => visiblePaths.Contains(x.RelativePath)).ToArray();
    }

    /// <summary>Builds a physical tree from root-relative persisted paths without treating a path as canonical identity.</summary>
    private static IReadOnlyList<ReconciliationTreeNode> BuildTree(IReadOnlyList<LibraryReconciliationPlanItem> items)
    {
        // Create one lookup node for each persisted physical path.
        var nodes = items.ToDictionary(x => x.RelativePath, x => new ReconciliationTreeNode(x), StringComparer.Ordinal);
        var roots = new List<ReconciliationTreeNode>();

        // Attach entries to their direct physical parent, preserving disconnected roots for review.
        foreach (var node in nodes.Values.OrderBy(x => x.Item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var separator = node.Item.RelativePath.LastIndexOf('/');
            var parentPath = separator < 0 ? null : node.Item.RelativePath[..separator];
            if (parentPath is not null && nodes.TryGetValue(parentPath, out var parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        // Render folders first and preserve a stable path ordering within each level.
        foreach (var node in nodes.Values)
        {
            node.Children.Sort((left, right) =>
            {
                var kindOrder = right.Item.IsDirectory.CompareTo(left.Item.IsDirectory);
                return kindOrder != 0 ? kindOrder : StringComparer.OrdinalIgnoreCase.Compare(left.Item.RelativePath, right.Item.RelativePath);
            });
        }

        return roots;
    }
}
