using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Storage.FolderBrowse;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace Jularr.Web.Features.Storage.Reconciliation;

/// <summary>
/// Creates the safe, persisted starting point of an Admin Library Reconciliation. It does not scan,
/// assign, rename, move or delete files; those follow only after a plan has been reviewed.
/// </summary>
public sealed class LibraryReconciliationPlanService(AppDbContext db, FolderBrowseService folderBrowse)
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi", ".cb7", ".cbr", ".cbz", ".epub", ".flac", ".m4a", ".m4v", ".mka", ".mkv",
        ".mov", ".mp3", ".mp4", ".ogg", ".ogm", ".opus", ".pdf", ".wav", ".webm", ".zip"
    };

    /// <summary>Creates a persisted draft after proving that the selected scope stays inside one readable library root.</summary>
    public async Task<LibraryReconciliationPlanResult> CreateAsync(LibraryReconciliationPlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Root: every plan begins by binding itself to one configured library root.
        var root = await db.LibraryRoots.SingleOrDefaultAsync(x => x.Id == request.LibraryRootId, cancellationToken);
        if (root is null)
        {
            return new LibraryReconciliationPlanResult(LibraryReconciliationPlanOutcome.RootNotFound, Message: "The configured library root was not found.");
        }

        if (!root.IsEnabled)
        {
            return new LibraryReconciliationPlanResult(LibraryReconciliationPlanOutcome.RootDisabled, Message: "The configured library root is disabled.");
        }

        // Scope: normalize manual relative input or a folder-browser selection before retaining only the root-relative path.
        if (!TryNormalizeStartFolder(root.Path, request.StartFolder, out var startFolder))
        {
            return new LibraryReconciliationPlanResult(LibraryReconciliationPlanOutcome.InvalidFolder, Message: "The reconciliation folder must stay inside the selected library root.");
        }

        var scopePath = startFolder is null ? root.Path : Path.Combine(root.Path, startFolder);
        var path = await folderBrowse.CheckAsync(scopePath, directoryOnly: true, otherPath: null, cancellationToken);
        if (path.Problem != PathProblem.None || path.Kind != PathKind.Directory || !path.Access.Readable || !ResolvesInsideConfiguredRoot(root.Path, scopePath))
        {
            return new LibraryReconciliationPlanResult(LibraryReconciliationPlanOutcome.StorageUnavailable, Message: "The selected library folder is not currently available for a safe read-only scan.");
        }

        // Persistence: create only a draft; the filesystem is untouched until a later explicit execution step.
        var plan = new LibraryReconciliationPlan
        {
            LibraryRootId = root.Id,
            StartFolder = startFolder,
            IncludeSubfolders = request.IncludeSubfolders,
            SkipConfidentAssignments = request.SkipConfidentAssignments,
            OnlyUnclearItems = request.OnlyUnclearItems,
            AnalyzeFilenameEvidence = request.AnalyzeFilenameEvidence
        };
        db.LibraryReconciliationPlans.Add(plan);
        await db.SaveChangesAsync(cancellationToken);

        return new LibraryReconciliationPlanResult(LibraryReconciliationPlanOutcome.Created, plan);
    }

    /// <summary>Lists only enabled roots that an admin may select as the fixed boundary of a new reconciliation plan.</summary>
    public async Task<IReadOnlyList<LibraryReconciliationRoot>> GetEnabledRootsAsync(CancellationToken cancellationToken)
    {
        return await db.LibraryRoots.AsNoTracking().Where(x => x.IsEnabled).OrderBy(x => x.Name).Select(x => new LibraryReconciliationRoot(x.Id, x.Name)).ToListAsync(cancellationToken);
    }

    /// <summary>Finds the newest completed scan that is still ready for review so the wizard can resume it instead of forcing a rescan.</summary>
    public async Task<Guid?> GetLatestReadyPlanIdAsync(CancellationToken cancellationToken)
    {
        return await db.LibraryReconciliationPlans.AsNoTracking()
            .Where(x => x.Status == LibraryReconciliationPlanStatus.Ready)
            .OrderByDescending(x => x.ScannedAtUtc)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Persists one explicit organization policy for a ready plan without starting any filesystem operation.</summary>
    public async Task<LibraryReconciliationWorkAssignmentResult> SetOrganizationModeAsync(LibraryReconciliationOrganizationModeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Plan: a physical-operation policy is meaningful only after the root-bound inventory is stable.
        var plan = await db.LibraryReconciliationPlans.SingleOrDefaultAsync(x => x.Id == request.PlanId, cancellationToken);
        if (plan is null)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.PlanNotFound, "The reconciliation plan was not found.");
        }

        if (plan.Status != LibraryReconciliationPlanStatus.Ready || !Enum.IsDefined(request.OrganizationMode))
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.InvalidPlanState, "The organization policy can be changed only for a completed reconciliation scan.");
        }

        // Decision: persist intent only; preview and execution remain separate explicit workflow stages.
        plan.OrganizationMode = request.OrganizationMode;
        plan.RemoveEmptySourceFolders = request.RemoveEmptySourceFolders;
        plan.UpdatedAtUtc = DateTime.UtcNow;
        plan.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.OrganizationModeSaved);
    }

    /// <summary>Loads the complete persisted review state through the plan's configured root without exposing unrelated storage data.</summary>
    public async Task<LibraryReconciliationPlanReview?> GetReviewAsync(Guid planId, CancellationToken cancellationToken)
    {
        // Review: the plan and configured root are read together so the UI cannot choose a different storage boundary.
        var planWithRootQuery =
            from plan in db.LibraryReconciliationPlans.AsNoTracking()
            join root in db.LibraryRoots.AsNoTracking() on plan.LibraryRootId equals root.Id
            where plan.Id == planId
            select new { Plan = plan, RootName = root.Name };
        var planWithRoot = await planWithRootQuery.SingleOrDefaultAsync(cancellationToken);
        if (planWithRoot is null)
        {
            return null;
        }

        var items = await db.LibraryReconciliationPlanItems.AsNoTracking().Where(x => x.PlanId == planId).OrderBy(x => x.RelativePath).ToListAsync(cancellationToken);
        return new LibraryReconciliationPlanReview(planWithRoot.Plan, planWithRoot.RootName, items);
    }

    /// <summary>Resolves an entry's direct or inherited work choice without copying a parent default into the child record.</summary>
    public async Task<LibraryReconciliationResolvedWork?> GetResolvedWorkAsync(Guid planId, Guid itemId, CancellationToken cancellationToken)
    {
        var item = await db.LibraryReconciliationPlanItems.AsNoTracking().SingleOrDefaultAsync(x => x.PlanId == planId && x.Id == itemId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        // Inheritance: resolve the closest explicit source with ordinal path semantics, not SQL wildcard matching.
        var explicitAssignments = await db.LibraryReconciliationPlanItems.AsNoTracking().Where(x => x.PlanId == planId && x.AssignedWorkId != null).ToListAsync(cancellationToken);
        var source = FindEffectiveWorkSource(item.RelativePath, explicitAssignments);
        if (source?.AssignedWorkId is not { } workId)
        {
            return null;
        }

        var work = await db.Works.AsNoTracking().Where(x => x.Id == workId).Select(x => new LibraryReconciliationWorkCandidate(x.Id, x.CanonicalTitle, x.Year)).SingleOrDefaultAsync(cancellationToken);
        return work is null ? null : new LibraryReconciliationResolvedWork(work.Id, work.Title, work.Year, source.RelativePath != item.RelativePath, source.RelativePath);
    }

    /// <summary>Resolves the nearest direct release fields for one plan entry so folder defaults remain visible without becoming child overrides.</summary>
    public async Task<LibraryReconciliationResolvedReleaseMetadata?> GetResolvedReleaseMetadataAsync(Guid planId, Guid itemId, CancellationToken cancellationToken)
    {
        var item = await db.LibraryReconciliationPlanItems.AsNoTracking().SingleOrDefaultAsync(x => x.PlanId == planId && x.Id == itemId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        // Inheritance: field-level defaults use the same closest-parent path semantics as canonical work assignments.
        var items = await db.LibraryReconciliationPlanItems.AsNoTracking().Where(x => x.PlanId == planId).ToListAsync(cancellationToken);
        return ResolveReleaseMetadata(item, items);
    }

    /// <summary>Builds a complete root-bound dry-run with exact target paths and blockers without changing any file or canonical link.</summary>
    public async Task<LibraryReconciliationPreview?> BuildPreviewAsync(Guid planId, CancellationToken cancellationToken)
    {
        // Plan: a preview is meaningful only after one stable root-bound inventory has completed and its root remains available.
        var planWithRoot = await (from planEntity in db.LibraryReconciliationPlans.AsNoTracking()
                                  join rootEntity in db.LibraryRoots.AsNoTracking() on planEntity.LibraryRootId equals rootEntity.Id
                                  where planEntity.Id == planId
                                  select new { Plan = planEntity, Root = rootEntity }).SingleOrDefaultAsync(cancellationToken);
        if (planWithRoot?.Plan.Status != LibraryReconciliationPlanStatus.Ready)
        {
            return null;
        }

        // Evidence: load the complete plan once so inherited defaults are resolved with ordinal path semantics in memory.
        var plan = planWithRoot.Plan;
        var root = planWithRoot.Root;
        var items = await db.LibraryReconciliationPlanItems.AsNoTracking().Where(x => x.PlanId == plan.Id).OrderBy(x => x.RelativePath).ToListAsync(cancellationToken);
        var explicitAssignments = items.Where(x => x.AssignedWorkId is not null).ToArray();
        var workIds = explicitAssignments.Select(x => x.AssignedWorkId!.Value).Distinct().ToArray();
        var works = await db.Works.AsNoTracking().Where(x => workIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var unitLabels = await GetUnitLabelsAsync(items, cancellationToken);
        var previewItems = new List<LibraryReconciliationPreviewItem>();
        var physicalChanges = new List<(LibraryReconciliationPlanItem Item, string TargetRelativePath)>();
        var rootProblem = GetRootProblem(root);

        // Classification: each physical media file is either deliberately kept, visibly blocked, a canonical-link-only action or an exact filesystem action.
        foreach (var item in items.Where(x => !x.IsDirectory))
        {
            if (item.State == LibraryReconciliationItemState.Ignored)
            {
                previewItems.Add(new LibraryReconciliationPreviewItem(item.RelativePath, null, null, LibraryReconciliationPreviewAction.KeepIgnored, false));
                continue;
            }

            var source = FindEffectiveWorkSource(item.RelativePath, explicitAssignments);
            if (item.State == LibraryReconciliationItemState.Error || source?.AssignedWorkId is not { } workId || !works.TryGetValue(workId, out var work))
            {
                previewItems.Add(new LibraryReconciliationPreviewItem(item.RelativePath, null, null, LibraryReconciliationPreviewAction.Blocked, false, "A canonical work mapping is required."));
                continue;
            }

            if (rootProblem is not null)
            {
                previewItems.Add(new LibraryReconciliationPreviewItem(item.RelativePath, null, work.CanonicalTitle, LibraryReconciliationPreviewAction.Blocked, source.Id != item.Id, rootProblem));
                continue;
            }

            var sourcePath = TryGetAbsoluteRootPath(root.Path, item.RelativePath);
            if (sourcePath is null || !File.Exists(sourcePath))
            {
                previewItems.Add(new LibraryReconciliationPreviewItem(
                    item.RelativePath,
                    null,
                    work.CanonicalTitle,
                    LibraryReconciliationPreviewAction.Blocked,
                    source.Id != item.Id,
                    "The reviewed source file is no longer available."));
                continue;
            }

            var sourceInfo = new FileInfo(sourcePath);
            if (item.ObservedSizeBytes != sourceInfo.Length || item.ObservedLastWriteTimeUtc != sourceInfo.LastWriteTimeUtc)
            {
                previewItems.Add(new LibraryReconciliationPreviewItem(
                    item.RelativePath,
                    null,
                    work.CanonicalTitle,
                    LibraryReconciliationPreviewAction.Blocked,
                    source.Id != item.Id,
                    "The file changed since this scan and must be reviewed again."));
                continue;
            }

            var targetRelativePath = BuildTargetRelativePath(item, work, unitLabels, plan.OrganizationMode);
            var action = GetPreviewAction(item.RelativePath, targetRelativePath, plan.OrganizationMode);
            previewItems.Add(new LibraryReconciliationPreviewItem(item.RelativePath, targetRelativePath, work.CanonicalTitle, action, source.Id != item.Id));
            if (action is LibraryReconciliationPreviewAction.Rename or LibraryReconciliationPreviewAction.MoveAndRename)
            {
                physicalChanges.Add((item, targetRelativePath));
            }
        }

        // Canonical links: a prior completed plan may already own a root-relative destination; reopening that exact same source remains a correction, not a collision.
        var committedLinks = await db.LibraryReconciliationFileLinks.AsNoTracking().Where(x => x.LibraryRootId == root.Id).ToDictionaryAsync(x => x.RelativePath, StringComparer.OrdinalIgnoreCase, cancellationToken);
        for (var index = 0; index < previewItems.Count; index++)
        {
            var previewItem = previewItems[index];
            var existingLink = previewItem.TargetRelativePath is { } targetPath && committedLinks.TryGetValue(targetPath, out var link) ? link : null;
            var hasDifferentCommittedTarget = existingLink is not null && !string.Equals(existingLink.RelativePath, previewItem.RelativePath, StringComparison.OrdinalIgnoreCase);
            if (previewItem.Action is not LibraryReconciliationPreviewAction.KeepIgnored && hasDifferentCommittedTarget)
            {
                previewItems[index] = previewItem with { Action = LibraryReconciliationPreviewAction.Conflict, Reason = "The destination already has a committed canonical file link." };
            }
        }

        // Conflict detection: prevent overwriting a file, targeting another planned source or merging two sources into one destination.
        var plannedSourcePaths = physicalChanges.Select(x => x.Item.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rootWritable = physicalChanges.Count == 0 || (await folderBrowse.CheckAsync(root.Path, directoryOnly: true, otherPath: null, cancellationToken)).Access.Writable;
        foreach (var change in physicalChanges)
        {
            var sourcePath = TryGetAbsoluteRootPath(root.Path, change.Item.RelativePath)!;
            var targetPath = TryGetAbsoluteRootPath(root.Path, change.TargetRelativePath);
            var sameTargetChanges = physicalChanges.Where(x => string.Equals(x.TargetRelativePath, change.TargetRelativePath, StringComparison.OrdinalIgnoreCase));
            var hasDuplicateTarget = sameTargetChanges.Skip(1).Any();
            var targetIsPlannedSource = plannedSourcePaths.Contains(change.TargetRelativePath);
            var targetExists = targetPath is not null && File.Exists(targetPath) && !string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase);
            string? conflict = null;
            if (targetPath is null)
            {
                conflict = "The generated destination escapes the configured library root.";
            }
            else if (!rootWritable)
            {
                conflict = "The library root is read-only or not writable.";
            }
            else if (hasDuplicateTarget)
            {
                conflict = "Several reviewed files resolve to the same destination.";
            }
            else if (targetIsPlannedSource)
            {
                conflict = "The destination is another planned source file.";
            }
            else if (targetExists)
            {
                conflict = "The destination file already exists.";
            }
            if (conflict is not null)
            {
                var index = previewItems.FindIndex(x => string.Equals(x.RelativePath, change.Item.RelativePath, StringComparison.OrdinalIgnoreCase));
                var current = previewItems[index];
                previewItems[index] = current with { Action = LibraryReconciliationPreviewAction.Conflict, Reason = conflict };
            }
        }

        return new LibraryReconciliationPreview(previewItems, CreatePreviewFingerprint(plan, previewItems));
    }

    /// <summary>Loads display-safe unit labels from the existing canonical records selected during review.</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> GetUnitLabelsAsync(IReadOnlyList<LibraryReconciliationPlanItem> items, CancellationToken cancellationToken)
    {
        var labels = new Dictionary<Guid, string>();
        var episodeIds = items.Where(x => x.AssignedWorkEpisodeId is not null).Select(x => x.AssignedWorkEpisodeId!.Value).Distinct().ToArray();
        var volumeIds = items.Where(x => x.AssignedWorkVolumeId is not null).Select(x => x.AssignedWorkVolumeId!.Value).Distinct().ToArray();
        var chapterIds = items.Where(x => x.AssignedWorkChapterId is not null).Select(x => x.AssignedWorkChapterId!.Value).Distinct().ToArray();

        // Labels: canonical unit values provide readable output only after the administrator has already selected their identity.
        foreach (var episode in await db.WorkEpisodes.AsNoTracking().Where(x => episodeIds.Contains(x.Id)).ToListAsync(cancellationToken))
        {
            labels[episode.Id] = $"S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}";
        }

        foreach (var volume in await db.WorkVolumes.AsNoTracking().Where(x => volumeIds.Contains(x.Id)).ToListAsync(cancellationToken))
        {
            labels[volume.Id] = $"V{volume.Number:D2}";
        }

        foreach (var chapter in await db.WorkChapters.AsNoTracking().Where(x => chapterIds.Contains(x.Id)).ToListAsync(cancellationToken))
        {
            labels[chapter.Id] = $"C{chapter.Number:0.##}";
        }

        return labels;
    }

    /// <summary>Builds one root-relative target path from a confirmed work and unit without accepting a user-supplied server path.</summary>
    private static string BuildTargetRelativePath(LibraryReconciliationPlanItem item, Work work, IReadOnlyDictionary<Guid, string> unitLabels, LibraryReconciliationOrganizationMode mode)
    {
        if (mode == LibraryReconciliationOrganizationMode.AssignOnly)
        {
            return item.RelativePath;
        }

        var extension = Path.GetExtension(item.RelativePath);
        var unitLabel = item.AssignedWorkEpisodeId is { } episodeId && unitLabels.TryGetValue(episodeId, out var episodeLabel) ? $" - {episodeLabel}" :
            item.AssignedWorkVolumeId is { } volumeId && unitLabels.TryGetValue(volumeId, out var volumeLabel) ? $" - {volumeLabel}" :
            item.AssignedWorkChapterId is { } chapterId && unitLabels.TryGetValue(chapterId, out var chapterLabel) ? $" - {chapterLabel}" : string.Empty;
        var fileName = SanitizePathSegment(work.CanonicalTitle + unitLabel) + extension;
        if (mode == LibraryReconciliationOrganizationMode.RenameFiles)
        {
            var directory = Path.GetDirectoryName(item.RelativePath);
            return string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
        }

        if (mode == LibraryReconciliationOrganizationMode.RenameFoldersAndFiles)
        {
            var segments = item.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var remainingDirectory = segments.Length > 2 ? Path.Combine(segments.Skip(1).Take(segments.Length - 2).ToArray()) : string.Empty;
            return string.IsNullOrEmpty(remainingDirectory) ? Path.Combine(SanitizePathSegment(work.CanonicalTitle), fileName) : Path.Combine(SanitizePathSegment(work.CanonicalTitle), remainingDirectory, fileName);
        }

        return Path.Combine(GetCanonicalTypeFolder(work.MediaType), SanitizePathSegment(work.CanonicalTitle), fileName);
    }

    /// <summary>Maps the selected organization policy to the dry-run action visible to the administrator.</summary>
    private static LibraryReconciliationPreviewAction GetPreviewAction(string sourceRelativePath, string targetRelativePath, LibraryReconciliationOrganizationMode mode)
    {
        if (mode == LibraryReconciliationOrganizationMode.AssignOnly || string.Equals(sourceRelativePath, targetRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            return LibraryReconciliationPreviewAction.AssignOnly;
        }

        return mode == LibraryReconciliationOrganizationMode.RenameFiles ? LibraryReconciliationPreviewAction.Rename : LibraryReconciliationPreviewAction.MoveAndRename;
    }

    /// <summary>Returns a safe canonical top-level category derived from the work's persisted media type.</summary>
    private static string GetCanonicalTypeFolder(WorkMediaType mediaType)
    {
        return mediaType switch
        {
            WorkMediaType.Movie => "Movies",
            WorkMediaType.Series or WorkMediaType.Anime => "Series",
            WorkMediaType.Manga => "Manga",
            WorkMediaType.LightNovel => "LightNovels",
            WorkMediaType.Book => "Books",
            _ => "Media"
        };
    }

    /// <summary>Normalizes a generated file or folder segment without allowing separators, reserved characters or an empty target.</summary>
    private static string SanitizePathSegment(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().Append(Path.DirectorySeparatorChar).Append(Path.AltDirectorySeparatorChar).ToHashSet();
        var normalized = new string(value.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(normalized) ? "Unsorted" : normalized.Length > 180 ? normalized[..180] : normalized;
    }

    /// <summary>Converts one root-relative plan path to an absolute path only when normalization proves it remains inside the configured root.</summary>
    private static string? TryGetAbsoluteRootPath(string rootPath, string relativePath)
    {
        try
        {
            var absolutePath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
            return ResolvesInsideConfiguredRoot(rootPath, absolutePath) ? absolutePath : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Returns a concise operational blocker when the configured root cannot safely accept a reconciliation operation.</summary>
    private static string? GetRootProblem(LibraryRoot root)
    {
        if (!root.IsEnabled)
        {
            return "The configured library root is disabled.";
        }

        return !Directory.Exists(root.Path) ? "The configured library root is unavailable." : null;
    }

    /// <summary>Hashes the reviewed revision, organization policy and complete change list so execution rejects a stale preview.</summary>
    private static string CreatePreviewFingerprint(LibraryReconciliationPlan plan, IReadOnlyList<LibraryReconciliationPreviewItem> items)
    {
        var fingerprintInput = $"{plan.Id:N}|{plan.Revision}|{(int)plan.OrganizationMode}|" + string.Join("|", items.Select(x => $"{x.RelativePath}>{x.TargetRelativePath}>{(int)x.Action}>{x.Reason}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput)))[..32];
    }

    /// <summary>Executes only the exact current conflict-free preview as a visible Activity operation and commits its confirmed canonical file links.</summary>
    public async Task<LibraryReconciliationExecutionResult> ExecuteAsync(LibraryReconciliationExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Confirmation: rebuild against current disk and plan state so a stale browser preview cannot drive a filesystem change.
        var preview = await BuildPreviewAsync(request.PlanId, cancellationToken);
        if (preview is null)
        {
            return new LibraryReconciliationExecutionResult(false, "The reconciliation plan is no longer ready for execution.");
        }

        if (!string.Equals(preview.Fingerprint, request.PreviewFingerprint, StringComparison.Ordinal))
        {
            return new LibraryReconciliationExecutionResult(false, "The plan or filesystem changed since the preview. Review the current preview before executing it.");
        }

        if (!preview.CanExecute)
        {
            return new LibraryReconciliationExecutionResult(false, "Resolve every unresolved mapping and destination conflict before execution.");
        }

        // Activity: create a durable visible operation before any physical or canonical state changes.
        var store = new OperationStore(db);
        var operationId = await store.CreateAsync(new OperationDescriptor("library-reconciliation", "Library", "Reconcile library files", Subject: request.PlanId.ToString("N"), Retryable: false), cancellationToken);
        await store.MarkRunningAsync(operationId, cancellationToken);
        try
        {
            await ExecutePreviewAsync(request.PlanId, preview, store, operationId, cancellationToken);
            await store.MarkSucceededAsync(operationId, "Library reconciliation completed.", CancellationToken.None);
            return new LibraryReconciliationExecutionResult(true, "Library reconciliation completed.", operationId);
        }
        catch (OperationCanceledException)
        {
            await store.MarkCancelledAsync(operationId, "Library reconciliation was cancelled.", CancellationToken.None);
            return new LibraryReconciliationExecutionResult(false, "Library reconciliation was cancelled.", operationId);
        }
        catch (Exception exception)
        {
            await store.MarkFailedAsync(operationId, exception.Message, CancellationToken.None);
            return new LibraryReconciliationExecutionResult(false, exception.Message, operationId);
        }
    }

    /// <summary>Moves the approved files with rollback protection, then persists the final root-relative links in the same confirmed plan lifecycle.</summary>
    private async Task ExecutePreviewAsync(Guid planId, LibraryReconciliationPreview preview, OperationStore store, Guid operationId, CancellationToken cancellationToken)
    {
        var plan = await db.LibraryReconciliationPlans.SingleOrDefaultAsync(x => x.Id == planId, cancellationToken) ?? throw new InvalidOperationException("The reconciliation plan was not found.");
        var root = await db.LibraryRoots.SingleOrDefaultAsync(x => x.Id == plan.LibraryRootId, cancellationToken) ?? throw new InvalidOperationException("The configured library root was not found.");
        var rootProblem = GetRootProblem(root);
        if (plan.Status != LibraryReconciliationPlanStatus.Ready || rootProblem is not null)
        {
            throw new InvalidOperationException(rootProblem ?? "The reconciliation plan is no longer ready for execution.");
        }

        // Scope: all persisted evidence is reloaded from this plan, never trusted from a form post or a browser-provided path.
        var items = await db.LibraryReconciliationPlanItems.Where(x => x.PlanId == plan.Id).ToListAsync(cancellationToken);
        var itemByPath = items.Where(x => !x.IsDirectory).ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        var changes = preview.Items.Where(x => x.Action is LibraryReconciliationPreviewAction.Rename or LibraryReconciliationPreviewAction.MoveAndRename).ToArray();
        ValidateExecutionSources(root.Path, changes, itemByPath);
        plan.Status = LibraryReconciliationPlanStatus.Executing;
        plan.Failure = null;
        plan.ExecutionOperationId = operationId;
        plan.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        // Filesystem: record each completed rename so a later move or persistence failure restores the original reviewed layout.
        var journal = new List<(string SourcePath, string TargetPath)>();
        try
        {
            for (var index = 0; index < changes.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var change = changes[index];
                var sourcePath = TryGetAbsoluteRootPath(root.Path, change.RelativePath)!;
                var targetPath = TryGetAbsoluteRootPath(root.Path, change.TargetRelativePath!)!;
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Move(sourcePath, targetPath);
                journal.Add((sourcePath, targetPath));
                var progress = changes.Length == 0 ? 80 : 10 + (index + 1) * 70 / changes.Length;
                await store.ReportProgressAsync(operationId, progress, $"Organized {index + 1} of {changes.Length} file(s).", cancellationToken: CancellationToken.None);
            }

            // Canonical links: persist only explicit or inherited Work choices and their already validated file-level units after every physical action succeeded.
            await CommitFileLinksAsync(plan, items, preview, cancellationToken);
            if (plan.RemoveEmptySourceFolders && journal.Count > 0)
            {
                // Cleanup: remove only now-empty ancestors of approved moved sources and retain the root and reviewed scope themselves.
                var cleanup = RemoveEmptySourceFolders(root.Path, plan.StartFolder, journal);
                if (cleanup.RemovedCount > 0)
                {
                    await store.AppendLogAsync(operationId, OperationLogLevel.Information, "Reconciliation", $"Removed {cleanup.RemovedCount} empty source folder(s).", CancellationToken.None);
                }

                foreach (var failure in cleanup.Failures)
                {
                    await store.AppendLogAsync(operationId, OperationLogLevel.Warning, "Reconciliation", failure, CancellationToken.None);
                }
            }

            plan.Status = LibraryReconciliationPlanStatus.Completed;
            plan.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await store.ReportProgressAsync(operationId, 100, "Library reconciliation completed.", cancellationToken: CancellationToken.None);
        }
        catch
        {
            await RollBackMovesAsync(journal, store, operationId);
            db.ChangeTracker.Clear();
            var failedPlan = await db.LibraryReconciliationPlans.SingleAsync(x => x.Id == planId, CancellationToken.None);
            failedPlan.Status = LibraryReconciliationPlanStatus.Failed;
            failedPlan.Failure = "Execution failed; completed file moves were rolled back where possible.";
            failedPlan.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>Rejects every changed source or destination immediately before the first move, preserving the reviewed root boundary and no-overwrite guarantee.</summary>
    private static void ValidateExecutionSources(string rootPath, IReadOnlyList<LibraryReconciliationPreviewItem> changes, IReadOnlyDictionary<string, LibraryReconciliationPlanItem> itemByPath)
    {
        foreach (var change in changes)
        {
            if (!itemByPath.ContainsKey(change.RelativePath))
            {
                throw new InvalidOperationException("The reviewed file is no longer part of this reconciliation plan.");
            }

            var sourcePath = TryGetAbsoluteRootPath(rootPath, change.RelativePath);
            var targetPath = change.TargetRelativePath is null ? null : TryGetAbsoluteRootPath(rootPath, change.TargetRelativePath);
            if (sourcePath is null || !File.Exists(sourcePath) || targetPath is null)
            {
                throw new InvalidOperationException("The filesystem changed since the reviewed preview.");
            }

            if (File.Exists(targetPath) && !string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The destination now exists: {change.TargetRelativePath}.");
            }
        }
    }

    /// <summary>Persists one durable final link per executable preview row after resolving folder inheritance from the same reviewed plan state.</summary>
    private async Task CommitFileLinksAsync(LibraryReconciliationPlan plan, IReadOnlyList<LibraryReconciliationPlanItem> items, LibraryReconciliationPreview preview, CancellationToken cancellationToken)
    {
        var executableItems = preview.Items.Where(x => x.Action is LibraryReconciliationPreviewAction.AssignOnly or LibraryReconciliationPreviewAction.Rename or LibraryReconciliationPreviewAction.MoveAndRename).ToArray();
        var itemByPath = items.Where(x => !x.IsDirectory).ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        var explicitAssignments = items.Where(x => x.AssignedWorkId is not null).ToArray();
        var existingLinks = await db.LibraryReconciliationFileLinks.Where(x => x.LibraryRootId == plan.LibraryRootId).ToDictionaryAsync(x => x.RelativePath, StringComparer.OrdinalIgnoreCase, cancellationToken);

        // Identity: each persisted link resolves its Work from the closest explicit folder or file mapping without copying inherited defaults into plan rows.
        foreach (var previewItem in executableItems)
        {
            var item = itemByPath[previewItem.RelativePath];
            var source = FindEffectiveWorkSource(item.RelativePath, explicitAssignments) ?? throw new InvalidOperationException("A reviewed file lost its canonical work mapping.");
            var targetRelativePath = previewItem.TargetRelativePath ?? item.RelativePath;
            if (!existingLinks.TryGetValue(targetRelativePath, out var link))
            {
                link = new LibraryReconciliationFileLink { LibraryRootId = plan.LibraryRootId, RelativePath = targetRelativePath };
                db.LibraryReconciliationFileLinks.Add(link);
            }

            link.PlanId = plan.Id;
            link.PlanItemId = item.Id;
            link.WorkId = source.AssignedWorkId!.Value;
            link.WorkEpisodeId = item.AssignedWorkEpisodeId;
            link.WorkVolumeId = item.AssignedWorkVolumeId;
            link.WorkChapterId = item.AssignedWorkChapterId;
            var releaseMetadata = ResolveReleaseMetadata(item, items);
            link.WorkEditionId = releaseMetadata.EditionId;
            link.WorkVersionId = releaseMetadata.VersionId;
            link.Language = releaseMetadata.Language;
            link.AudioLanguage = releaseMetadata.AudioLanguage;
            link.SubtitleLanguage = releaseMetadata.SubtitleLanguage;
            link.QualitySource = releaseMetadata.QualitySource;
            link.OriginalRelativePath = item.RelativePath;
            link.RelativePath = targetRelativePath;
            link.CommittedAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Reverses completed file moves in reverse order and records any path that needs manual recovery.</summary>
    private static async Task RollBackMovesAsync(IReadOnlyList<(string SourcePath, string TargetPath)> journal, OperationStore store, Guid operationId)
    {
        foreach (var move in journal.Reverse())
        {
            try
            {
                if (File.Exists(move.TargetPath) && !File.Exists(move.SourcePath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(move.SourcePath)!);
                    File.Move(move.TargetPath, move.SourcePath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await store.AppendLogAsync(operationId, OperationLogLevel.Error, "Reconciliation", $"Rollback failed for '{move.TargetPath}': {exception.Message}", CancellationToken.None);
            }
        }
    }

    /// <summary>Deletes only empty ancestors of successfully moved sources, never the configured root or the reviewed starting scope.</summary>
    private static (int RemovedCount, IReadOnlyList<string> Failures) RemoveEmptySourceFolders(string rootPath, string? startFolder, IReadOnlyList<(string SourcePath, string TargetPath)> journal)
    {
        var scopePath = Path.GetFullPath(startFolder is null ? rootPath : Path.Combine(rootPath, startFolder));
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var move in journal)
        {
            var currentDirectory = Path.GetDirectoryName(move.SourcePath);
            while (!string.IsNullOrWhiteSpace(currentDirectory) && !string.Equals(currentDirectory, scopePath, StringComparison.OrdinalIgnoreCase))
            {
                if (!ResolvesInsideConfiguredRoot(rootPath, currentDirectory) || !IsWithinScope(scopePath, currentDirectory))
                {
                    break;
                }

                candidates.Add(currentDirectory);
                currentDirectory = Path.GetDirectoryName(currentDirectory);
            }
        }

        var removedCount = 0;
        var failures = new List<string>();
        foreach (var directory in candidates.OrderByDescending(x => x.Length))
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                    removedCount++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add($"Could not remove an empty reconciliation source folder: {exception.Message}");
            }
        }

        return (removedCount, failures);
    }

    /// <summary>Confirms that a cleanup candidate remains within the immutable reviewed scope without relying on string-prefix matching.</summary>
    private static bool IsWithinScope(string scopePath, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(scopePath, candidatePath);
        return relativePath != ".." && !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Takes a root-bound, read-only inventory for step 1 of the wizard. It never derives canonical
    /// identity, follows reparse points, modifies media files or deletes previous user decisions.
    /// </summary>
    public async Task<LibraryReconciliationScanResult> ScanAsync(Guid planId, CancellationToken cancellationToken)
    {
        var plan = await db.LibraryReconciliationPlans.SingleOrDefaultAsync(x => x.Id == planId, cancellationToken);
        if (plan is null)
        {
            return new LibraryReconciliationScanResult(LibraryReconciliationPlanOutcome.PlanNotFound, Message: "The reconciliation plan was not found.");
        }

        if (plan.Status == LibraryReconciliationPlanStatus.Ready)
        {
            return await ExistingScanAsync(plan, cancellationToken);
        }

        if (plan.Status is not LibraryReconciliationPlanStatus.Draft and not LibraryReconciliationPlanStatus.Failed)
        {
            return new LibraryReconciliationScanResult(LibraryReconciliationPlanOutcome.InvalidPlanState, plan, Message: "This reconciliation plan cannot be scanned in its current state.");
        }

        // Root: revalidate the plan's configured root just before touching the filesystem.
        var root = await db.LibraryRoots.SingleOrDefaultAsync(x => x.Id == plan.LibraryRootId, cancellationToken);
        if (root is null)
        {
            return new LibraryReconciliationScanResult(LibraryReconciliationPlanOutcome.RootNotFound, plan, Message: "The configured library root was not found.");
        }

        if (!root.IsEnabled)
        {
            return new LibraryReconciliationScanResult(LibraryReconciliationPlanOutcome.RootDisabled, plan, Message: "The configured library root is disabled.");
        }

        var scopePath = plan.StartFolder is null ? root.Path : Path.Combine(root.Path, plan.StartFolder);
        var path = await folderBrowse.CheckAsync(scopePath, directoryOnly: true, otherPath: null, cancellationToken);
        if (path.Problem != PathProblem.None || path.Kind != PathKind.Directory || !path.Access.Readable || !ResolvesInsideConfiguredRoot(root.Path, scopePath))
        {
            return new LibraryReconciliationScanResult(LibraryReconciliationPlanOutcome.StorageUnavailable, plan, Message: "The selected library folder is not currently available for a safe read-only scan.");
        }

        // Lifecycle: persist that the inventory is running before enumeration starts.
        plan.Status = LibraryReconciliationPlanStatus.Scanning;
        plan.Failure = null;
        plan.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var recognizedTitles = plan.AnalyzeFilenameEvidence ? await GetUniqueWorkTitleEvidenceAsync(cancellationToken) : new Dictionary<string, string>(StringComparer.Ordinal);
            var items = ScanScope(plan.Id, root.Path, scopePath, plan.IncludeSubfolders, recognizedTitles, cancellationToken);

            if (plan.SkipConfidentAssignments)
            {
                await ApplyCommittedLinksAsync(plan.LibraryRootId, items, cancellationToken);

                // Rollups: restored links change file states after the folder counts were derived, so parents must be recomputed.
                PopulateFolderCounts(items);
            }

            // Persistence: replace scan evidence and advance the plan state atomically after the read completes.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var oldItems = db.LibraryReconciliationPlanItems.Where(x => x.PlanId == plan.Id);
            db.LibraryReconciliationPlanItems.RemoveRange(oldItems);
            await db.LibraryReconciliationPlanItems.AddRangeAsync(items, cancellationToken);

            // Lifecycle: expose the reviewed inventory and make the next non-destructive wizard step available.
            plan.Status = LibraryReconciliationPlanStatus.Ready;
            plan.ScannedAtUtc = DateTime.UtcNow;
            plan.UpdatedAtUtc = plan.ScannedAtUtc.Value;
            plan.Revision++;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return Summarize(plan, items, LibraryReconciliationPlanOutcome.Scanned);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            plan.Status = LibraryReconciliationPlanStatus.Draft;
            plan.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or DbUpdateException)
        {
            plan.Status = LibraryReconciliationPlanStatus.Failed;
            plan.Failure = "The library folder could not be completely scanned.";
            plan.UpdatedAtUtc = DateTime.UtcNow;
            plan.Revision++;
            await db.SaveChangesAsync(cancellationToken);
            return new LibraryReconciliationScanResult(LibraryReconciliationPlanOutcome.ScanFailed, plan, HardErrors: 1, Message: plan.Failure);
        }
    }

    /// <summary>Enumerates a scope without following reparse points and records evidence only, never canonical identity.</summary>
    private static List<LibraryReconciliationPlanItem> ScanScope(Guid planId, string rootPath, string scopePath, bool includeSubfolders, IReadOnlyDictionary<string, string> recognizedTitles, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        var items = new List<LibraryReconciliationPlanItem>();

        // Inventory: enumerate only real filesystem entries; reparse points are excluded by the options above.
        foreach (var entry in Directory.EnumerateFileSystemEntries(scopePath, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Safety: a symlink or mount boundary must never add an entry outside the configured root.
            if (!ResolvesInsideConfiguredRoot(rootPath, entry))
            {
                continue;
            }

            // Persistence: database paths are always normalized relative to the selected root.
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(rootPath, entry));
            if (relativePath is null)
            {
                continue;
            }

            // Classification: a scan exposes evidence only. It must not infer canonical media identity.
            var attributes = File.GetAttributes(entry);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var isMediaFile = MediaExtensions.Contains(Path.GetExtension(entry));
            var fileInfo = !isDirectory ? new FileInfo(entry) : null;
            var state = LibraryReconciliationItemState.Ignored;
            var confidence = 0;
            var detectionSummary = "Non-media file is kept in place.";
            if (isDirectory)
            {
                state = LibraryReconciliationItemState.Unclear;
                detectionSummary = "Folder has not been assigned yet.";
            }
            else if (isMediaFile)
            {
                state = LibraryReconciliationItemState.Unclear;
                detectionSummary = "File needs an explicit canonical assignment.";
            }

            // Evidence: a unique normalized title match informs review but never sets AssignedWorkId or changes the unresolved state.
            var evidenceName = NormalizeEvidenceName(isDirectory ? Path.GetFileName(entry) : Path.GetFileNameWithoutExtension(entry));
            var folderEvidenceName = NormalizeEvidenceName(Path.GetFileName(Path.GetDirectoryName(entry) ?? string.Empty));
            if ((isDirectory || isMediaFile) && (recognizedTitles.TryGetValue(evidenceName, out var title) || recognizedTitles.TryGetValue(folderEvidenceName, out title)))
            {
                confidence = 60;
                detectionSummary = $"Possible existing work: {title}. Confirm or correct this evidence before mapping.";
            }

            items.Add(new LibraryReconciliationPlanItem
            {
                PlanId = planId,
                RelativePath = relativePath,
                IsDirectory = isDirectory,
                State = state,
                Confidence = confidence,
                ObservedSizeBytes = fileInfo?.Length,
                ObservedLastWriteTimeUtc = fileInfo?.LastWriteTimeUtc,
                DetectionSummary = detectionSummary
            });
        }

        // Presentation: folders receive rollups from their children before the stable tree order is returned.
        PopulateFolderCounts(items);
        return items.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Normalizes a folder or filename title to conservative alphanumeric evidence; it never produces canonical identity.</summary>
    private static string NormalizeEvidenceName(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    /// <summary>Loads unambiguous title hints for optional scan evidence; hints never create canonical identity or mappings.</summary>
    private async Task<IReadOnlyDictionary<string, string>> GetUniqueWorkTitleEvidenceAsync(CancellationToken cancellationToken)
    {
        var workTitles = await db.Works.AsNoTracking().Select(x => x.CanonicalTitle).ToListAsync(cancellationToken);
        var titleGroups = workTitles.GroupBy(NormalizeEvidenceName, StringComparer.Ordinal);
        return titleGroups.Where(x => !string.IsNullOrEmpty(x.Key) && x.Count() == 1).ToDictionary(x => x.Key, x => x.Single(), StringComparer.Ordinal);
    }

    /// <summary>Restores only previously committed same-root links as visible confident evidence when the administrator requested that scan shortcut.</summary>
    private async Task ApplyCommittedLinksAsync(Guid libraryRootId, IReadOnlyList<LibraryReconciliationPlanItem> items, CancellationToken cancellationToken)
    {
        var paths = items.Where(x => !x.IsDirectory).Select(x => x.RelativePath).ToArray();
        var committedLinks = db.LibraryReconciliationFileLinks.AsNoTracking().Where(x => x.LibraryRootId == libraryRootId && paths.Contains(x.RelativePath));
        var links = await committedLinks.ToDictionaryAsync(x => x.RelativePath, StringComparer.OrdinalIgnoreCase, cancellationToken);

        // Existing identity: this is an admin-confirmed historical link, not a title or filename heuristic; keep it visible and reversible in the new plan.
        foreach (var item in items.Where(x => !x.IsDirectory && links.TryGetValue(x.RelativePath, out _)))
        {
            var link = links[item.RelativePath];
            item.AssignedWorkId = link.WorkId;
            item.AssignedWorkEpisodeId = link.WorkEpisodeId;
            item.AssignedWorkVolumeId = link.WorkVolumeId;
            item.AssignedWorkChapterId = link.WorkChapterId;
            item.AssignedWorkEditionId = link.WorkEditionId;
            item.AssignedWorkVersionId = link.WorkVersionId;
            item.Language = link.Language;
            item.AudioLanguage = link.AudioLanguage;
            item.SubtitleLanguage = link.SubtitleLanguage;
            item.QualitySource = link.QualitySource;
            item.State = LibraryReconciliationItemState.Recognized;
            item.Confidence = 100;
            item.DetectionSummary = "Existing administrator-confirmed canonical link; visible for correction but skipped from unresolved review.";
        }
    }

    /// <summary>Returns the persisted inventory instead of rescanning a plan whose review is already ready.</summary>
    private async Task<LibraryReconciliationScanResult> ExistingScanAsync(LibraryReconciliationPlan plan, CancellationToken cancellationToken)
    {
        var items = await db.LibraryReconciliationPlanItems.Where(x => x.PlanId == plan.Id).ToListAsync(cancellationToken);
        return Summarize(plan, items, LibraryReconciliationPlanOutcome.AlreadyScanned);
    }

    /// <summary>Derives safe display counts from scanned entries; directory rollups do not inflate file totals.</summary>
    private static LibraryReconciliationScanResult Summarize(LibraryReconciliationPlan plan, IReadOnlyList<LibraryReconciliationPlanItem> scannedItems, LibraryReconciliationPlanOutcome outcome)
    {
        var items = scannedItems.Count > 0 ? scannedItems : [];
        var files = items.Where(x => !x.IsDirectory).ToArray();
        var unclearItems = files.Count(x => x.State == LibraryReconciliationItemState.Unclear);
        var ignoredItems = files.Count(x => x.State == LibraryReconciliationItemState.Ignored);
        return new LibraryReconciliationScanResult(outcome, plan, files.Length, unclearItems, ignoredItems);
    }

    /// <summary>Recomputes inherited file evidence so direct folder defaults remain visible and reversible.</summary>
    private static void RefreshEffectiveWorkStates(IReadOnlyList<LibraryReconciliationPlanItem> items)
    {
        var explicitAssignments = items.Where(x => x.AssignedWorkId is not null).ToArray();
        foreach (var item in items.Where(x => !x.IsDirectory && x.State is not LibraryReconciliationItemState.Ignored and not LibraryReconciliationItemState.Error))
        {
            var source = FindEffectiveWorkSource(item.RelativePath, explicitAssignments);
            if (source is null)
            {
                item.State = LibraryReconciliationItemState.Unclear;
                item.Confidence = 0;
                item.DetectionSummary = "File needs an explicit canonical assignment.";
                continue;
            }

            item.State = LibraryReconciliationItemState.Recognized;
            item.Confidence = 100;
            item.DetectionSummary = source.Id == item.Id ? "Explicitly mapped by an administrator." : $"Inherits an explicit canonical mapping from {source.RelativePath}.";
        }
    }

    /// <summary>Clears file-level units that no longer belong to the effective work or selected volume after a folder mapping changes.</summary>
    private async Task ClearIncompatibleUnitsAsync(IReadOnlyList<LibraryReconciliationPlanItem> items, CancellationToken cancellationToken)
    {
        var episodeIds = items.Where(x => x.AssignedWorkEpisodeId is not null).Select(x => x.AssignedWorkEpisodeId!.Value).Distinct().ToArray();
        var volumeIds = items.Where(x => x.AssignedWorkVolumeId is not null).Select(x => x.AssignedWorkVolumeId!.Value).Distinct().ToArray();
        var chapterIds = items.Where(x => x.AssignedWorkChapterId is not null).Select(x => x.AssignedWorkChapterId!.Value).Distinct().ToArray();

        // Canonical units: read only the ownership fields needed to prove every persisted file-level selection still fits its effective work.
        var episodeWorkIds = await db.WorkEpisodes.Where(x => episodeIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.WorkId, cancellationToken);
        var volumeWorkIds = await db.WorkVolumes.Where(x => volumeIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.WorkId, cancellationToken);
        var chapterOwnership = await db.WorkChapters.Where(x => chapterIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => new { x.WorkId, x.VolumeId }, cancellationToken);
        var explicitAssignments = items.Where(x => x.AssignedWorkId is not null).ToArray();

        // Validation: a parent default may change inherited work, but never silently preserve an incompatible episode, volume or chapter.
        foreach (var item in items.Where(x => !x.IsDirectory))
        {
            var effectiveWorkId = FindEffectiveWorkSource(item.RelativePath, explicitAssignments)?.AssignedWorkId;
            if (item.AssignedWorkEpisodeId is { } episodeId && (!effectiveWorkId.HasValue || !episodeWorkIds.TryGetValue(episodeId, out var episodeWorkId) || episodeWorkId != effectiveWorkId.Value))
            {
                item.AssignedWorkEpisodeId = null;
            }

            if (item.AssignedWorkVolumeId is { } volumeId && (!effectiveWorkId.HasValue || !volumeWorkIds.TryGetValue(volumeId, out var volumeWorkId) || volumeWorkId != effectiveWorkId.Value))
            {
                item.AssignedWorkVolumeId = null;
            }

            var hasCompatibleChapter = item.AssignedWorkChapterId is not { } chapterId ||
                effectiveWorkId.HasValue && chapterOwnership.TryGetValue(chapterId, out var chapter) && chapter.WorkId == effectiveWorkId.Value &&
                (item.AssignedWorkVolumeId is not { } selectedVolumeId || chapter.VolumeId == selectedVolumeId);
            if (!hasCompatibleChapter)
            {
                item.AssignedWorkChapterId = null;
            }
        }
    }

    /// <summary>Finds the closest explicitly mapped folder or file that applies to one root-relative plan path.</summary>
    private static LibraryReconciliationPlanItem? FindEffectiveWorkSource(string relativePath, IReadOnlyList<LibraryReconciliationPlanItem> explicitAssignments)
    {
        return explicitAssignments.Where(x => x.RelativePath == relativePath || relativePath.StartsWith(x.RelativePath + "/", StringComparison.Ordinal)).OrderByDescending(x => x.RelativePath.Length).FirstOrDefault();
    }

    /// <summary>Finds the closest direct field source for a path, preserving child overrides while allowing folder defaults to flow downward.</summary>
    private static LibraryReconciliationPlanItem? FindEffectiveReleaseValueSource(string relativePath, IReadOnlyList<LibraryReconciliationPlanItem> items, Func<LibraryReconciliationPlanItem, bool> hasValue)
    {
        return items.Where(x => hasValue(x) && (x.RelativePath == relativePath || relativePath.StartsWith(x.RelativePath + "/", StringComparison.Ordinal))).OrderByDescending(x => x.RelativePath.Length).FirstOrDefault();
    }

    /// <summary>Resolves every independently overridable release field from one entry and its closest ancestor defaults without changing the plan state.</summary>
    private static LibraryReconciliationResolvedReleaseMetadata ResolveReleaseMetadata(LibraryReconciliationPlanItem item, IReadOnlyList<LibraryReconciliationPlanItem> items)
    {
        var edition = FindEffectiveReleaseValueSource(item.RelativePath, items, x => x.AssignedWorkEditionId is not null);
        var version = FindEffectiveReleaseValueSource(item.RelativePath, items, x => x.AssignedWorkVersionId is not null);
        var language = FindEffectiveReleaseValueSource(item.RelativePath, items, x => x.Language is not null);
        var audioLanguage = FindEffectiveReleaseValueSource(item.RelativePath, items, x => x.AudioLanguage is not null);
        var subtitleLanguage = FindEffectiveReleaseValueSource(item.RelativePath, items, x => x.SubtitleLanguage is not null);
        var qualitySource = FindEffectiveReleaseValueSource(item.RelativePath, items, x => x.QualitySource is not null);
        return new LibraryReconciliationResolvedReleaseMetadata(
            edition?.AssignedWorkEditionId,
            edition?.RelativePath,
            version?.AssignedWorkVersionId,
            version?.RelativePath,
            language?.Language,
            language?.RelativePath,
            audioLanguage?.AudioLanguage,
            audioLanguage?.RelativePath,
            subtitleLanguage?.SubtitleLanguage,
            subtitleLanguage?.RelativePath,
            qualitySource?.QualitySource,
            qualitySource?.RelativePath);
    }

    /// <summary>Rolls visible child evidence up so a parent cannot conceal unresolved or conflicting media mappings.</summary>
    private static void PopulateFolderCounts(IReadOnlyList<LibraryReconciliationPlanItem> items)
    {
        var files = items.Where(x => !x.IsDirectory).ToArray();
        var explicitAssignments = items.Where(x => x.AssignedWorkId is not null).ToArray();
        foreach (var folder in items.Where(x => x.IsDirectory))
        {
            var prefix = folder.RelativePath + "/";
            var descendants = files.Where(x => x.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            var reviewableFiles = descendants.Where(x => x.State != LibraryReconciliationItemState.Ignored).ToArray();
            var assignedWorkIds = reviewableFiles.Select(x => FindEffectiveWorkSource(x.RelativePath, explicitAssignments)?.AssignedWorkId).Where(x => x is not null).Select(x => x!.Value).ToHashSet();
            if (FindEffectiveWorkSource(folder.RelativePath, explicitAssignments)?.AssignedWorkId is { } folderWorkId)
            {
                assignedWorkIds.Add(folderWorkId);
            }

            folder.FileCount = descendants.Length;
            folder.UnresolvedCount = reviewableFiles.Count(x => x.State is LibraryReconciliationItemState.Unclear or LibraryReconciliationItemState.Error);
            if (reviewableFiles.Length == 0)
            {
                folder.State = folder.AssignedWorkId is null ? LibraryReconciliationItemState.Ignored : LibraryReconciliationItemState.Recognized;
            }
            else if (reviewableFiles.Any(x => x.State == LibraryReconciliationItemState.Error))
            {
                folder.State = LibraryReconciliationItemState.Error;
            }
            else if (folder.UnresolvedCount > 0)
            {
                folder.State = assignedWorkIds.Count > 0 ? LibraryReconciliationItemState.Partial : LibraryReconciliationItemState.Unclear;
            }
            else if (assignedWorkIds.Count == 1)
            {
                folder.State = LibraryReconciliationItemState.Recognized;
            }
            else
            {
                folder.State = LibraryReconciliationItemState.Partial;
            }
        }
    }

    /// <summary>Normalizes a generated root-relative path to the stable slash-separated database representation.</summary>
    private static string? NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "." || Path.IsPathRooted(path))
        {
            return null;
        }

        var segments = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            return null;
        }

        return string.Join('/', segments);
    }

    /// <summary>Resolves symlinks before confirming that the scope cannot escape its configured library root.</summary>
    private static bool ResolvesInsideConfiguredRoot(string rootPath, string scopePath)
    {
        if (SafePath.TryNormalize(rootPath, out var normalizedRoot) != PathProblem.None || SafePath.TryNormalize(scopePath, out var normalizedScope) != PathProblem.None)
        {
            return false;
        }

        var realRoot = SafePath.ResolveReal(normalizedRoot, out var rootProblem);
        var realScope = SafePath.ResolveReal(normalizedScope, out var scopeProblem);
        return rootProblem == PathProblem.None && scopeProblem == PathProblem.None && realRoot is not null && realScope is not null && SafePath.IsWithin(realRoot, realScope);
    }

    /// <summary>Accepts only a non-rooted, non-traversing path below the selected configured library root.</summary>
    internal static bool TryNormalizeRelativeFolder(string? input, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        var value = input.Trim();
        if (Path.IsPathRooted(value) || value.Any(char.IsControl))
        {
            return false;
        }

        var segments = value.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            return false;
        }

        normalized = Path.Combine(segments);
        return true;
    }

    /// <summary>Accepts manual relative input or an existing folder-browser selection only when it resolves inside the configured library root.</summary>
    private static bool TryNormalizeStartFolder(string rootPath, string? input, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        var value = input.Trim();
        if (!Path.IsPathRooted(value))
        {
            return TryNormalizeRelativeFolder(value, out normalized);
        }

        try
        {
            // Browser selection: retain only a normalized relative path after the selected existing directory passes the real-path boundary check.
            var selectedPath = Path.GetFullPath(value);
            if (!ResolvesInsideConfiguredRoot(rootPath, selectedPath))
            {
                return false;
            }

            var rootFullPath = Path.GetFullPath(rootPath);
            var relativePath = Path.GetRelativePath(rootFullPath, selectedPath);
            return relativePath == "." || TryNormalizeRelativeFolder(relativePath, out normalized);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Finds existing canonical works for explicit owner selection; it never maps a filename by itself.</summary>
    public async Task<IReadOnlyList<LibraryReconciliationWorkCandidate>> SearchWorksAsync(Guid planId, string? query, CancellationToken cancellationToken)
    {
        var planExists = await db.LibraryReconciliationPlans.AnyAsync(x => x.Id == planId, cancellationToken);
        if (!planExists)
        {
            return [];
        }

        // Lookup: a blank query lists the first works alphabetically so a picker is never empty; text matching stays case-insensitive and never infers a mapping.
        var matchingWorks = db.Works.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var searchText = query.Trim().ToUpperInvariant();
            matchingWorks = matchingWorks.Where(x => x.CanonicalTitle.ToUpper().Contains(searchText));
        }

        return await matchingWorks.OrderBy(x => x.CanonicalTitle).ThenBy(x => x.Year).Take(20).Select(x => new LibraryReconciliationWorkCandidate(x.Id, x.CanonicalTitle, x.Year)).ToListAsync(cancellationToken);
    }

    /// <summary>Lists only existing canonical episodes for a mapped review entry; it never infers an episode from a filename.</summary>
    public async Task<IReadOnlyList<LibraryReconciliationEpisodeCandidate>> GetEpisodeCandidatesAsync(Guid planId, Guid itemId, CancellationToken cancellationToken)
    {
        // Scope: an episode list belongs only to one non-ignored physical entry in the selected plan.
        var item = await db.LibraryReconciliationPlanItems.AsNoTracking().SingleOrDefaultAsync(x => x.PlanId == planId && x.Id == itemId, cancellationToken);
        if (item is null || item.State == LibraryReconciliationItemState.Ignored)
        {
            return [];
        }

        // Work: resolve the closest explicit work source before looking up its existing canonical structure.
        var explicitAssignments = await db.LibraryReconciliationPlanItems.AsNoTracking().Where(x => x.PlanId == planId && x.AssignedWorkId != null).ToListAsync(cancellationToken);
        if (FindEffectiveWorkSource(item.RelativePath, explicitAssignments)?.AssignedWorkId is not { } workId)
        {
            return [];
        }

        var episodesQuery = db.Set<WorkEpisode>().AsNoTracking().Where(x => x.WorkId == workId);
        return await episodesQuery
            .OrderBy(x => x.SeasonNumber)
            .ThenBy(x => x.EpisodeNumber)
            .Select(x => new LibraryReconciliationEpisodeCandidate(x.Id, x.SeasonNumber, x.EpisodeNumber, x.IsSpecial, x.Title, x.Id == item.AssignedWorkEpisodeId))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Lists only existing canonical volumes for a mapped media file; it never infers a volume from a filename.</summary>
    public async Task<IReadOnlyList<LibraryReconciliationVolumeCandidate>> GetVolumeCandidatesAsync(Guid planId, Guid itemId, CancellationToken cancellationToken)
    {
        // Scope: a volume list belongs only to one non-ignored physical file in the selected plan.
        var item = await db.LibraryReconciliationPlanItems.AsNoTracking().SingleOrDefaultAsync(x => x.PlanId == planId && x.Id == itemId, cancellationToken);
        if (item is null || item.IsDirectory || item.State == LibraryReconciliationItemState.Ignored)
        {
            return [];
        }

        // Work: resolve the closest explicit work source before looking up its existing canonical structure.
        var explicitAssignments = await db.LibraryReconciliationPlanItems.AsNoTracking().Where(x => x.PlanId == planId && x.AssignedWorkId != null).ToListAsync(cancellationToken);
        if (FindEffectiveWorkSource(item.RelativePath, explicitAssignments)?.AssignedWorkId is not { } workId)
        {
            return [];
        }

        var volumesQuery = db.Set<WorkVolume>().AsNoTracking().Where(x => x.WorkId == workId);
        return await volumesQuery
            .OrderBy(x => x.Number)
            .Select(x => new LibraryReconciliationVolumeCandidate(x.Id, x.Number, x.Title, x.Id == item.AssignedWorkVolumeId))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Lists existing canonical chapters for a mapped media file, constrained to its explicit volume when one is selected.</summary>
    public async Task<IReadOnlyList<LibraryReconciliationChapterCandidate>> GetChapterCandidatesAsync(Guid planId, Guid itemId, CancellationToken cancellationToken)
    {
        // Scope and work: resolve only a reviewable file and its effective existing work.
        var item = await db.LibraryReconciliationPlanItems.AsNoTracking().SingleOrDefaultAsync(x => x.PlanId == planId && x.Id == itemId, cancellationToken);
        if (item is null || item.IsDirectory || item.State == LibraryReconciliationItemState.Ignored)
        {
            return [];
        }

        var assignments = await db.LibraryReconciliationPlanItems.AsNoTracking().Where(x => x.PlanId == planId && x.AssignedWorkId != null).ToListAsync(cancellationToken);
        if (FindEffectiveWorkSource(item.RelativePath, assignments)?.AssignedWorkId is not { } workId)
        {
            return [];
        }

        // Structure: constrain chapters to the selected volume when present, otherwise retain the work-wide canonical list.
        var chaptersQuery = db.Set<WorkChapter>().AsNoTracking().Where(x => x.WorkId == workId && (item.AssignedWorkVolumeId == null || x.VolumeId == item.AssignedWorkVolumeId));
        return await chaptersQuery.OrderBy(x => x.Number).Select(x => new LibraryReconciliationChapterCandidate(x.Id, x.Number, x.Title, x.Id == item.AssignedWorkChapterId)).ToListAsync(cancellationToken);
    }

    /// <summary>Lists existing editions belonging to the effective work of one non-ignored folder or file without creating new editorial identity.</summary>
    public async Task<IReadOnlyList<LibraryReconciliationEditionCandidate>> GetEditionCandidatesAsync(Guid planId, Guid itemId, CancellationToken cancellationToken)
    {
        var item = await db.LibraryReconciliationPlanItems.AsNoTracking().SingleOrDefaultAsync(x => x.PlanId == planId && x.Id == itemId, cancellationToken);
        if (item is null || item.State == LibraryReconciliationItemState.Ignored)
        {
            return [];
        }

        // Work: edition identity is available only after the file already has an explicit or inherited canonical work.
        var assignments = await db.LibraryReconciliationPlanItems.AsNoTracking().Where(x => x.PlanId == planId && x.AssignedWorkId != null).ToListAsync(cancellationToken);
        if (FindEffectiveWorkSource(item.RelativePath, assignments)?.AssignedWorkId is not { } workId)
        {
            return [];
        }

        var editions = db.WorkEditions.AsNoTracking().Where(x => x.WorkId == workId);
        return await editions.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.EditionKey).Select(x => new LibraryReconciliationEditionCandidate(
            x.Id,
            x.EditionKey,
            x.Language,
            x.Format,
            x.Title,
            x.Id == item.AssignedWorkEditionId)).ToListAsync(cancellationToken);
    }

    /// <summary>Lists existing versions belonging to the effective work and selected edition of one reconciliation folder or file without creating release identity.</summary>
    public async Task<IReadOnlyList<LibraryReconciliationVersionCandidate>> GetVersionCandidatesAsync(Guid planId, Guid itemId, CancellationToken cancellationToken)
    {
        var item = await db.LibraryReconciliationPlanItems.AsNoTracking().SingleOrDefaultAsync(x => x.PlanId == planId && x.Id == itemId, cancellationToken);
        if (item is null || item.State == LibraryReconciliationItemState.Ignored)
        {
            return [];
        }

        // Work and edition: do not expose a version list across canonical works or editorial variants.
        var assignments = await db.LibraryReconciliationPlanItems.AsNoTracking().Where(x => x.PlanId == planId && x.AssignedWorkId != null).ToListAsync(cancellationToken);
        if (FindEffectiveWorkSource(item.RelativePath, assignments)?.AssignedWorkId is not { } workId)
        {
            return [];
        }

        var planItems = await db.LibraryReconciliationPlanItems.AsNoTracking().Where(x => x.PlanId == planId).ToListAsync(cancellationToken);
        var effectiveMetadata = ResolveReleaseMetadata(item, planItems);
        var versions = db.WorkVersions.AsNoTracking().Where(x => x.WorkId == workId && (effectiveMetadata.EditionId == null || x.EditionId == effectiveMetadata.EditionId));
        return await versions.OrderBy(x => x.UnitKey).ThenBy(x => x.VersionKey).Select(x => new LibraryReconciliationVersionCandidate(
            x.Id,
            x.VersionKey,
            x.UnitKey,
            x.Quality,
            x.Source,
            x.Id == item.AssignedWorkVersionId)).ToListAsync(cancellationToken);
    }

    /// <summary>Records an explicit owner mapping to an existing work without creating assets, files or filesystem changes.</summary>
    public async Task<LibraryReconciliationWorkAssignmentResult> AssignWorkAsync(LibraryReconciliationWorkAssignmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Transaction: validation and plan evidence must either all persist or all remain unchanged.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Plan: mapping is only allowed after a stable root-bound inventory exists.
        var plan = await db.LibraryReconciliationPlans.SingleOrDefaultAsync(x => x.Id == request.PlanId, cancellationToken);
        if (plan is null)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.PlanNotFound, "The reconciliation plan was not found.");
        }

        if (plan.Status != LibraryReconciliationPlanStatus.Ready)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.InvalidPlanState, "The reconciliation plan must have a completed scan before mapping.");
        }

        // Canonical identity: only a pre-existing work explicitly chosen by the admin may be assigned.
        var workExists = await db.Works.AnyAsync(x => x.Id == request.WorkId, cancellationToken);
        if (!workExists)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.WorkNotFound, "The selected canonical work was not found.");
        }

        var itemIds = request.ItemIds.Distinct().ToArray();
        if (itemIds.Length == 0)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ItemsNotFound, "Choose at least one scanned folder or file to map.");
        }

        // Scope: all selected entries must belong to this plan, so identifiers cannot cross a root boundary.
        var items = await db.LibraryReconciliationPlanItems.Where(x => x.PlanId == plan.Id && itemIds.Contains(x.Id)).ToListAsync(cancellationToken);
        if (items.Count != itemIds.Length)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ItemsNotFound, "One or more selected entries are no longer part of this reconciliation plan.");
        }

        if (items.Any(x => x.State == LibraryReconciliationItemState.Ignored))
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.IgnoredItem, "Ignored entries must be restored before they can be mapped.");
        }

        // Mapping: persist reviewable plan evidence only; canonical asset creation belongs to a later execution step.
        foreach (var item in items)
        {
            item.AssignedWorkId = request.WorkId;
            item.AssignedWorkEpisodeId = null;
            item.AssignedWorkVolumeId = null;
            item.AssignedWorkChapterId = null;
            item.AssignedWorkEditionId = null;
            item.AssignedWorkVersionId = null;
            item.State = LibraryReconciliationItemState.Recognized;
            item.Confidence = 100;
            item.DetectionSummary = "Explicitly mapped by an administrator.";
            item.Error = null;
        }

        // Hierarchy: parent states remain derived from visible child evidence, never from a hidden match.
        var planItems = await db.LibraryReconciliationPlanItems.Where(x => x.PlanId == plan.Id).ToListAsync(cancellationToken);
        await ClearIncompatibleUnitsAsync(planItems, cancellationToken);
        RefreshEffectiveWorkStates(planItems);
        PopulateFolderCounts(planItems);
        plan.UpdatedAtUtc = DateTime.UtcNow;
        plan.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.Assigned);
    }

    /// <summary>Applies existing edition/version choices and bounded manual release metadata to one mapped folder or media file without touching canonical records or paths.</summary>
    public async Task<LibraryReconciliationWorkAssignmentResult> AssignReleaseMetadataAsync(LibraryReconciliationReleaseMetadataRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Transaction: release identity and manual descriptive values form one reversible review decision.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var plan = await db.LibraryReconciliationPlans.SingleOrDefaultAsync(x => x.Id == request.PlanId, cancellationToken);
        var item = await db.LibraryReconciliationPlanItems.SingleOrDefaultAsync(x => x.PlanId == request.PlanId && x.Id == request.ItemId, cancellationToken);
        if (plan?.Status != LibraryReconciliationPlanStatus.Ready || item is null || item.State == LibraryReconciliationItemState.Ignored)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ItemsNotFound, "Choose one current, non-ignored folder or media file from a ready reconciliation plan.");
        }

        // Work: editions and versions remain references to existing canonical records belonging to this entry's effective work.
        var planItems = await db.LibraryReconciliationPlanItems.Where(x => x.PlanId == plan.Id).ToListAsync(cancellationToken);
        var assignments = planItems.Where(x => x.AssignedWorkId is not null).ToArray();
        if (FindEffectiveWorkSource(item.RelativePath, assignments)?.AssignedWorkId is not { } workId)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.WorkNotFound, "Map the file or a parent folder to an existing work before selecting release metadata.");
        }

        if (request.EditionId is { } editionId && !await db.WorkEditions.AnyAsync(x => x.Id == editionId && x.WorkId == workId, cancellationToken))
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.WorkNotFound, "The selected edition does not belong to the effective canonical work.");
        }

        var ancestorItems = planItems.Where(x => x.Id != item.Id).ToArray();
        var inheritedEditionId = FindEffectiveReleaseValueSource(item.RelativePath, ancestorItems, x => x.AssignedWorkEditionId is not null)?.AssignedWorkEditionId;
        var effectiveEditionId = request.EditionId ?? inheritedEditionId;
        if (request.VersionId is { } versionId && !await db.WorkVersions.AnyAsync(x => x.Id == versionId && x.WorkId == workId && (effectiveEditionId == null || x.EditionId == effectiveEditionId), cancellationToken))
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.WorkNotFound, "The selected version does not belong to the effective work and edition.");
        }

        // Metadata: values are bounded review annotations, not parser-derived canonical identity.
        string? language;
        string? audioLanguage;
        string? subtitleLanguage;
        string? qualitySource;
        try
        {
            language = NormalizeReleaseValue(request.Language, 32);
            audioLanguage = NormalizeReleaseValue(request.AudioLanguage, 32);
            subtitleLanguage = NormalizeReleaseValue(request.SubtitleLanguage, 32);
            qualitySource = NormalizeReleaseValue(request.QualitySource, 240);
        }
        catch (ArgumentException)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ItemsNotFound, "Release metadata contains an invalid value.");
        }

        item.AssignedWorkEditionId = request.EditionId;
        item.AssignedWorkVersionId = request.VersionId;
        item.Language = language;
        item.AudioLanguage = audioLanguage;
        item.SubtitleLanguage = subtitleLanguage;
        item.QualitySource = qualitySource;
        plan.UpdatedAtUtc = DateTime.UtcNow;
        plan.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ReleaseMetadataSaved);
    }

    /// <summary>Trims one optional manually entered release value and rejects control characters or values beyond its storage boundary.</summary>
    private static string? NormalizeReleaseValue(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Release metadata contains an invalid value.", nameof(value));
        }

        return normalized;
    }

    /// <summary>Applies bounded manual language, audio, subtitle and quality defaults to a selected file batch without changing canonical identity fields.</summary>
    public async Task<LibraryReconciliationWorkAssignmentResult> AssignBatchReleaseMetadataAsync(LibraryReconciliationBatchReleaseMetadataRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Validation: normalize every submitted value before tracking an item so a rejected post cannot leave partial context changes behind.
        string? language;
        string? audioLanguage;
        string? subtitleLanguage;
        string? qualitySource;
        try
        {
            language = NormalizeReleaseValue(request.Language, 32);
            audioLanguage = NormalizeReleaseValue(request.AudioLanguage, 32);
            subtitleLanguage = NormalizeReleaseValue(request.SubtitleLanguage, 32);
            qualitySource = NormalizeReleaseValue(request.QualitySource, 240);
        }
        catch (ArgumentException)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ItemsNotFound, "Release metadata contains an invalid value.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var plan = await db.LibraryReconciliationPlans.SingleOrDefaultAsync(x => x.Id == request.PlanId, cancellationToken);
        var itemIds = request.ItemIds.Distinct().ToArray();
        var items = await db.LibraryReconciliationPlanItems.Where(x => x.PlanId == request.PlanId && itemIds.Contains(x.Id)).ToListAsync(cancellationToken);
        if (plan?.Status != LibraryReconciliationPlanStatus.Ready || itemIds.Length == 0 || items.Count != itemIds.Length || items.Any(x => x.IsDirectory || x.State == LibraryReconciliationItemState.Ignored))
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ItemsNotFound, "Select current, non-ignored media files from a ready reconciliation plan.");
        }

        // Batch defaults: do not replace any selected edition, version or structural unit while applying common descriptive fields.
        foreach (var item in items)
        {
            item.Language = language;
            item.AudioLanguage = audioLanguage;
            item.SubtitleLanguage = subtitleLanguage;
            item.QualitySource = qualitySource;
        }

        plan.UpdatedAtUtc = DateTime.UtcNow;
        plan.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ReleaseMetadataSaved);
    }

    /// <summary>Creates one reversible plan-only logical group for selected current media files without changing paths, canonical records or the filesystem.</summary>
    public async Task<LibraryReconciliationWorkAssignmentResult> CreateLogicalGroupAsync(LibraryReconciliationLogicalGroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Transaction: group membership and the plan revision describe one review decision.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var plan = await db.LibraryReconciliationPlans.SingleOrDefaultAsync(x => x.Id == request.PlanId, cancellationToken);
        if (plan is null)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.PlanNotFound, "The reconciliation plan was not found.");
        }

        if (plan.Status != LibraryReconciliationPlanStatus.Ready)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.InvalidPlanState, "The reconciliation plan must have a completed scan before logical groups can change.");
        }

        var name = request.Name.Trim();
        var itemIds = request.ItemIds.Distinct().ToArray();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160 || itemIds.Length == 0)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ItemsNotFound, "Provide a short group name and select at least one media file.");
        }

        // Scope: groups never accept folders, ignored entries or identifiers outside this one persisted plan.
        var items = await db.LibraryReconciliationPlanItems.Where(x => x.PlanId == plan.Id && itemIds.Contains(x.Id)).ToListAsync(cancellationToken);
        if (items.Count != itemIds.Length || items.Any(x => x.IsDirectory || x.State == LibraryReconciliationItemState.Ignored))
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ItemsNotFound, "Select only current, non-ignored media files from this reconciliation plan.");
        }

        if (await db.LibraryReconciliationLogicalGroups.AnyAsync(x => x.PlanId == plan.Id && x.Name == name, cancellationToken))
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ItemsNotFound, "A logical group with that name already exists in this plan.");
        }

        // Grouping: setting membership is reversible plan evidence; it never rewrites physical folders or canonical Work identity.
        var group = new LibraryReconciliationLogicalGroup { PlanId = plan.Id, Name = name };
        db.LibraryReconciliationLogicalGroups.Add(group);
        foreach (var item in items)
        {
            item.LogicalGroupId = group.Id;
        }

        plan.UpdatedAtUtc = DateTime.UtcNow;
        plan.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.LogicalGroupCreated);
    }

    /// <summary>Clears checked media files from their logical groups while retaining their physical paths and all existing mapping decisions.</summary>
    public async Task<LibraryReconciliationWorkAssignmentResult> ClearLogicalGroupAsync(LibraryReconciliationLogicalGroupClearRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Transaction: removing one split or merge decision must leave the rest of the plan unchanged.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var plan = await db.LibraryReconciliationPlans.SingleOrDefaultAsync(x => x.Id == request.PlanId, cancellationToken);
        if (plan is null)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.PlanNotFound, "The reconciliation plan was not found.");
        }

        var itemIds = request.ItemIds.Distinct().ToArray();
        var items = await db.LibraryReconciliationPlanItems.Where(x => x.PlanId == plan.Id && itemIds.Contains(x.Id)).ToListAsync(cancellationToken);
        if (plan.Status != LibraryReconciliationPlanStatus.Ready || itemIds.Length == 0 || items.Count != itemIds.Length || items.Any(x => x.IsDirectory))
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ItemsNotFound, "Select current media files from a ready reconciliation plan.");
        }

        // Reversal: ungroup only the selected files, then remove orphaned plan-only groups.
        var affectedGroupIds = items.Where(x => x.LogicalGroupId is not null).Select(x => x.LogicalGroupId!.Value).Distinct().ToArray();
        foreach (var item in items)
        {
            item.LogicalGroupId = null;
        }

        if (affectedGroupIds.Length > 0)
        {
            var remainingGroupMembers = db.LibraryReconciliationPlanItems.Where(item => !itemIds.Contains(item.Id));
            var orphanedGroups = await db.LibraryReconciliationLogicalGroups.Where(x => affectedGroupIds.Contains(x.Id) && !remainingGroupMembers.Any(item => item.LogicalGroupId == x.Id)).ToListAsync(cancellationToken);
            db.LibraryReconciliationLogicalGroups.RemoveRange(orphanedGroups);
        }

        plan.UpdatedAtUtc = DateTime.UtcNow;
        plan.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.LogicalGroupCleared);
    }

    /// <summary>Lists the plan-only groups and their membership counts without exposing or modifying physical filesystem hierarchy.</summary>
    public async Task<IReadOnlyList<LibraryReconciliationLogicalGroupSummary>> GetLogicalGroupsAsync(Guid planId, CancellationToken cancellationToken)
    {
        // Projection: calculate membership from persisted plan items so an empty or removed group is never displayed as active review evidence.
        var groups = db.LibraryReconciliationLogicalGroups.AsNoTracking().Where(x => x.PlanId == planId);
        return await groups.OrderBy(x => x.Name).Select(x => new LibraryReconciliationLogicalGroupSummary(x.Id, x.Name, db.LibraryReconciliationPlanItems.Count(item => item.LogicalGroupId == x.Id))).ToListAsync(cancellationToken);
    }

    /// <summary>Applies or clears one existing canonical episode for a mapped media file without creating canonical records or touching the filesystem.</summary>
    public async Task<LibraryReconciliationWorkAssignmentResult> AssignEpisodeAsync(LibraryReconciliationEpisodeAssignmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Transaction: the file-level unit and plan revision remain one reviewable, reversible decision.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Scope: only a ready plan can change an existing non-ignored media file inside its root-bound inventory.
        var plan = await db.LibraryReconciliationPlans.SingleOrDefaultAsync(x => x.Id == request.PlanId, cancellationToken);
        if (plan is null)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.PlanNotFound, "The reconciliation plan was not found.");
        }

        if (plan.Status != LibraryReconciliationPlanStatus.Ready)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.InvalidPlanState, "The reconciliation plan must have a completed scan before an episode can be selected.");
        }

        var item = await db.LibraryReconciliationPlanItems.SingleOrDefaultAsync(x => x.PlanId == plan.Id && x.Id == request.ItemId, cancellationToken);
        if (item is null || item.IsDirectory)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ItemsNotFound, "Choose one scanned media file.");
        }

        if (item.State == LibraryReconciliationItemState.Ignored)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.IgnoredItem, "Ignored entries must be restored before they can be mapped.");
        }

        if (request.EpisodeId is null)
        {
            // Reversal: retain the effective work while reopening only this file's explicit episode choice.
            item.AssignedWorkEpisodeId = null;
            plan.UpdatedAtUtc = DateTime.UtcNow;
            plan.Revision++;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.EpisodeReset);
        }

        // Work and unit: selected episodes must already belong to this file's effective canonical work.
        var explicitAssignments = await db.LibraryReconciliationPlanItems.Where(x => x.PlanId == plan.Id && x.AssignedWorkId != null).ToListAsync(cancellationToken);
        if (FindEffectiveWorkSource(item.RelativePath, explicitAssignments)?.AssignedWorkId is not { } workId)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.WorkNotFound, "Map the file or a parent folder to an existing work before selecting an episode.");
        }

        var episodeExists = await db.Set<WorkEpisode>().AnyAsync(x => x.Id == request.EpisodeId && x.WorkId == workId, cancellationToken);
        if (!episodeExists)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.EpisodeNotFound, "The selected episode does not belong to the effective canonical work.");
        }

        item.AssignedWorkEpisodeId = request.EpisodeId;
        item.AssignedWorkVolumeId = null;
        item.AssignedWorkChapterId = null;
        plan.UpdatedAtUtc = DateTime.UtcNow;
        plan.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.EpisodeAssigned);
    }

    /// <summary>Maps checked files to consecutive existing episodes in their displayed order without inferring canonical units from filenames.</summary>
    public async Task<LibraryReconciliationWorkAssignmentResult> AssignEpisodeSequenceAsync(LibraryReconciliationEpisodeSequenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Transaction: all file-level episode decisions and the plan revision either persist together or remain unchanged.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Plan and file scope: the sequence applies only to distinct, reviewable files from one ready inventory.
        var plan = await db.LibraryReconciliationPlans.SingleOrDefaultAsync(x => x.Id == request.PlanId, cancellationToken);
        if (plan is null)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.PlanNotFound, "The reconciliation plan was not found.");
        }

        if (plan.Status != LibraryReconciliationPlanStatus.Ready)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.InvalidPlanState, "The reconciliation plan must have a completed scan before an episode sequence can be selected.");
        }

        var itemIds = request.ItemIds.Distinct().ToArray();
        if (itemIds.Length == 0)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ItemsNotFound, "Choose at least one media file for the episode sequence.");
        }

        var items = await db.LibraryReconciliationPlanItems.Where(x => x.PlanId == plan.Id && itemIds.Contains(x.Id)).ToListAsync(cancellationToken);
        if (items.Count != itemIds.Length || items.Any(x => x.IsDirectory || x.State == LibraryReconciliationItemState.Ignored))
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ItemsNotFound, "Choose only current, non-ignored media files from this reconciliation plan.");
        }

        var itemsById = items.ToDictionary(x => x.Id);
        var orderedItems = itemIds.Select(x => itemsById[x]).ToArray();

        // Work: every selected file must resolve to one existing work before its canonical episode sequence is available.
        var explicitAssignments = await db.LibraryReconciliationPlanItems.Where(x => x.PlanId == plan.Id && x.AssignedWorkId != null).ToListAsync(cancellationToken);
        var sources = orderedItems.Select(x => FindEffectiveWorkSource(x.RelativePath, explicitAssignments)).ToArray();
        if (sources.Any(x => x?.AssignedWorkId is null))
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.WorkNotFound, "Map every selected file or its parent folder to one existing work before assigning an episode sequence.");
        }

        var workIds = sources.Select(x => x!.AssignedWorkId!.Value).Distinct().ToArray();
        if (workIds.Length != 1)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.WorkNotFound, "All files in one episode sequence must resolve to the same existing work.");
        }

        // Units: validate the explicit start and optional end against the checked file count before changing any plan item.
        var episodes = await db.Set<WorkEpisode>().Where(x => x.WorkId == workIds[0]).OrderBy(x => x.SeasonNumber).ThenBy(x => x.EpisodeNumber).ToListAsync(cancellationToken);
        var startIndex = episodes.FindIndex(x => x.Id == request.StartEpisodeId);
        if (startIndex < 0)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.EpisodeNotFound, "The selected starting episode does not belong to the effective canonical work.");
        }

        var endIndex = startIndex + orderedItems.Length - 1;
        if (request.EndEpisodeId is { } endEpisodeId)
        {
            endIndex = episodes.FindIndex(x => x.Id == endEpisodeId);
        }

        if (endIndex < startIndex || endIndex >= episodes.Count || endIndex - startIndex + 1 != orderedItems.Length)
        {
            const string rangeErrorMessage = "The selected episode range must contain exactly one consecutive episode for every checked file.";
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.EpisodeSequenceOutOfRange, rangeErrorMessage);
        }

        // Mapping: file order is displayed and explicitly confirmed by the administrator; no filename parser makes this decision.
        for (var index = 0; index < orderedItems.Length; index++)
        {
            orderedItems[index].AssignedWorkEpisodeId = episodes[startIndex + index].Id;
            orderedItems[index].AssignedWorkVolumeId = null;
            orderedItems[index].AssignedWorkChapterId = null;
        }

        plan.UpdatedAtUtc = DateTime.UtcNow;
        plan.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.EpisodeSequenceAssigned);
    }

    /// <summary>Applies or clears one existing canonical volume for a mapped media file without creating canonical records or touching the filesystem.</summary>
    public async Task<LibraryReconciliationWorkAssignmentResult> AssignVolumeAsync(LibraryReconciliationVolumeAssignmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Transaction: the file-level volume and plan revision remain one reviewable, reversible decision.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Scope: only a ready plan can change an existing non-ignored media file inside its root-bound inventory.
        var plan = await db.LibraryReconciliationPlans.SingleOrDefaultAsync(x => x.Id == request.PlanId, cancellationToken);
        if (plan is null)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.PlanNotFound, "The reconciliation plan was not found.");
        }

        if (plan.Status != LibraryReconciliationPlanStatus.Ready)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.InvalidPlanState, "The reconciliation plan must have a completed scan before a volume can be selected.");
        }

        var item = await db.LibraryReconciliationPlanItems.SingleOrDefaultAsync(x => x.PlanId == plan.Id && x.Id == request.ItemId, cancellationToken);
        if (item is null || item.IsDirectory)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ItemsNotFound, "Choose one scanned media file.");
        }

        if (item.State == LibraryReconciliationItemState.Ignored)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.IgnoredItem, "Ignored entries must be restored before they can be mapped.");
        }

        if (request.VolumeId is null)
        {
            // Reversal: retain the effective work while reopening only this file's explicit volume choice.
            item.AssignedWorkVolumeId = null;
            plan.UpdatedAtUtc = DateTime.UtcNow;
            plan.Revision++;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.VolumeReset);
        }

        // Work and unit: selected volumes must already belong to this file's effective canonical work.
        var explicitAssignments = await db.LibraryReconciliationPlanItems.Where(x => x.PlanId == plan.Id && x.AssignedWorkId != null).ToListAsync(cancellationToken);
        if (FindEffectiveWorkSource(item.RelativePath, explicitAssignments)?.AssignedWorkId is not { } workId)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.WorkNotFound, "Map the file or a parent folder to an existing work before selecting a volume.");
        }

        var volumeExists = await db.Set<WorkVolume>().AnyAsync(x => x.Id == request.VolumeId && x.WorkId == workId, cancellationToken);
        if (!volumeExists)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.VolumeNotFound, "The selected volume does not belong to the effective canonical work.");
        }

        item.AssignedWorkVolumeId = request.VolumeId;
        item.AssignedWorkEpisodeId = null;
        item.AssignedWorkChapterId = null;
        plan.UpdatedAtUtc = DateTime.UtcNow;
        plan.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.VolumeAssigned);
    }

    /// <summary>Applies or clears one existing canonical chapter for a mapped media file without creating canonical records or touching the filesystem.</summary>
    public async Task<LibraryReconciliationWorkAssignmentResult> AssignChapterAsync(LibraryReconciliationChapterAssignmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Transaction: the file-level chapter and plan revision remain one reversible decision.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Scope: only a ready plan can change one current non-ignored media file from its root-bound inventory.
        var plan = await db.LibraryReconciliationPlans.SingleOrDefaultAsync(x => x.Id == request.PlanId, cancellationToken);
        if (plan is null)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.PlanNotFound, "The reconciliation plan was not found.");
        }

        var item = await db.LibraryReconciliationPlanItems.SingleOrDefaultAsync(x => x.PlanId == plan.Id && x.Id == request.ItemId, cancellationToken);
        if (plan.Status != LibraryReconciliationPlanStatus.Ready || item is null || item.IsDirectory || item.State == LibraryReconciliationItemState.Ignored)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ItemsNotFound, "Choose one current, non-ignored media file.");
        }
        if (request.ChapterId is null)
        {
            item.AssignedWorkChapterId = null;
            plan.UpdatedAtUtc = DateTime.UtcNow;
            plan.Revision++;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ChapterReset);
        }

        // Work and unit: chapter must belong to the effective work and, when selected, the exact canonical volume.
        var assignments = await db.LibraryReconciliationPlanItems.Where(x => x.PlanId == plan.Id && x.AssignedWorkId != null).ToListAsync(cancellationToken);
        if (FindEffectiveWorkSource(item.RelativePath, assignments)?.AssignedWorkId is not { } workId)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.WorkNotFound, "Map the file or a parent folder to an existing work before selecting a chapter.");
        }

        var chapterExists = await db.Set<WorkChapter>().AnyAsync(x => x.Id == request.ChapterId && x.WorkId == workId && (item.AssignedWorkVolumeId == null || x.VolumeId == item.AssignedWorkVolumeId), cancellationToken);
        if (!chapterExists)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ChapterNotFound, "The selected chapter does not belong to the effective canonical work and volume.");
        }
        item.AssignedWorkChapterId = request.ChapterId;
        item.AssignedWorkEpisodeId = null;
        plan.UpdatedAtUtc = DateTime.UtcNow;
        plan.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ChapterAssigned);
    }

    /// <summary>Explicitly skips or restores selected media files in one plan without changing their filesystem or canonical media state.</summary>
    public async Task<LibraryReconciliationWorkAssignmentResult> SetIgnoredAsync(LibraryReconciliationItemIgnoreRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Transaction: item decisions and every affected folder summary must become visible together.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Plan and scope: reject stale plans and identifiers that do not belong to the selected root-bound inventory.
        var plan = await db.LibraryReconciliationPlans.SingleOrDefaultAsync(x => x.Id == request.PlanId, cancellationToken);
        if (plan is null)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.PlanNotFound, "The reconciliation plan was not found.");
        }

        if (plan.Status != LibraryReconciliationPlanStatus.Ready)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.InvalidPlanState, "The reconciliation plan must have a completed scan before items can be skipped.");
        }

        var itemIds = request.ItemIds.Distinct().ToArray();
        if (itemIds.Length == 0)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ItemsNotFound, "Choose at least one media file.");
        }

        var items = await db.LibraryReconciliationPlanItems.Where(x => x.PlanId == plan.Id && itemIds.Contains(x.Id)).ToListAsync(cancellationToken);
        if (items.Count != itemIds.Length)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ItemsNotFound, "One or more selected entries are no longer part of this reconciliation plan.");
        }

        if (items.Any(x => x.IsDirectory))
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.DirectoryItem, "Folders stay visible for review and cannot be skipped as a batch.");
        }

        if (!request.IsIgnored && items.Any(x => !x.IsExplicitlyIgnored))
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.NotExplicitlyIgnored, "Only files explicitly skipped by an administrator can be restored.");
        }

        // Decision: skip removes direct plan evidence; restoring lets a visible parent default apply again.
        foreach (var item in items)
        {
            item.AssignedWorkId = null;
            item.AssignedWorkEpisodeId = null;
            item.AssignedWorkVolumeId = null;
            item.AssignedWorkChapterId = null;
            item.IsExplicitlyIgnored = request.IsIgnored;
            item.State = request.IsIgnored ? LibraryReconciliationItemState.Ignored : LibraryReconciliationItemState.Unclear;
            item.Confidence = 0;
            item.DetectionSummary = request.IsIgnored ? "Explicitly skipped by an administrator." : "Supported media file; canonical mapping requires review.";
            item.Error = null;
        }

        // Hierarchy: recompute effective inheritance and folder counts after every explicit skip decision.
        var planItems = await db.LibraryReconciliationPlanItems.Where(x => x.PlanId == plan.Id).ToListAsync(cancellationToken);
        RefreshEffectiveWorkStates(planItems);
        PopulateFolderCounts(planItems);
        plan.UpdatedAtUtc = DateTime.UtcNow;
        plan.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LibraryReconciliationWorkAssignmentResult(request.IsIgnored ? LibraryReconciliationWorkAssignmentOutcome.Ignored : LibraryReconciliationWorkAssignmentOutcome.Restored);
    }

    /// <summary>Removes one explicit work selection and returns the scanned entry to unresolved plan evidence.</summary>
    public async Task<LibraryReconciliationWorkAssignmentResult> ResetWorkAsync(LibraryReconciliationWorkResetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ResetWorksAsync(new LibraryReconciliationBatchWorkResetRequest(request.PlanId, [request.ItemId]), cancellationToken);
    }

    /// <summary>Removes direct work, unit and release overrides from selected plan entries without changing their physical inventory.</summary>
    public async Task<LibraryReconciliationWorkAssignmentResult> ResetWorksAsync(LibraryReconciliationBatchWorkResetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Transaction: resetting direct evidence and its parent summaries must remain one reversible operation.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Plan and scope: only a completed inventory may be edited, and its entry must remain root-bound.
        var plan = await db.LibraryReconciliationPlans.SingleOrDefaultAsync(x => x.Id == request.PlanId, cancellationToken);
        if (plan is null)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.PlanNotFound, "The reconciliation plan was not found.");
        }

        if (plan.Status != LibraryReconciliationPlanStatus.Ready)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.InvalidPlanState, "The reconciliation plan must have a completed scan before mapping.");
        }

        var requestedItemIds = request.ItemIds.Distinct().ToArray();
        var items = await db.LibraryReconciliationPlanItems.Where(x => x.PlanId == plan.Id && requestedItemIds.Contains(x.Id)).ToListAsync(cancellationToken);
        if (requestedItemIds.Length == 0 || items.Count != requestedItemIds.Length)
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.ItemsNotFound, "One or more selected entries are no longer part of this reconciliation plan.");
        }

        if (items.Any(x => x.State == LibraryReconciliationItemState.Ignored))
        {
            return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.IgnoredItem, "Ignored entries must be restored before they can be mapped.");
        }

        // Reversal: restore unresolved scan evidence only; no canonical media or filesystem state is touched.
        foreach (var item in items)
        {
            item.AssignedWorkId = null;
            item.AssignedWorkEpisodeId = null;
            item.AssignedWorkVolumeId = null;
            item.AssignedWorkChapterId = null;
            item.AssignedWorkEditionId = null;
            item.AssignedWorkVersionId = null;
            item.Language = null;
            item.AudioLanguage = null;
            item.SubtitleLanguage = null;
            item.QualitySource = null;
            item.State = LibraryReconciliationItemState.Unclear;
            item.Confidence = 0;
            item.DetectionSummary = item.IsDirectory ? "Folder requires an explicit canonical mapping." : "Supported media file; canonical mapping requires review.";
            item.Error = null;
        }

        // Hierarchy: reopening an entry also reopens each affected folder's derived review state.
        var planItems = await db.LibraryReconciliationPlanItems.Where(x => x.PlanId == plan.Id).ToListAsync(cancellationToken);
        RefreshEffectiveWorkStates(planItems);
        PopulateFolderCounts(planItems);
        plan.UpdatedAtUtc = DateTime.UtcNow;
        plan.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LibraryReconciliationWorkAssignmentResult(LibraryReconciliationWorkAssignmentOutcome.Reset);
    }
}
