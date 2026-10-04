using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Storage.FolderBrowse;
using Jularr.Web.Features.Storage.Reconciliation;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class LibraryReconciliationPlanServiceTests
{
    /// <summary>Proves that a valid relative path persists only within its selected configured root.</summary>
    [TestMethod]
    public async Task CreatesARootBoundPlanForANormalizedRelativeFolder()
    {
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        Directory.CreateDirectory(Path.Combine(root.Path, "anime", "season-01"));
        await scope.Db.SaveChangesAsync();

        var result = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id, "anime/season-01", SkipConfidentAssignments: true), CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(result.Plan);
        Assert.AreEqual(Path.Combine("anime", "season-01"), result.Plan.StartFolder);
        Assert.AreEqual(LibraryReconciliationPlanStatus.Draft, result.Plan.Status);
        Assert.AreEqual(root.Id, result.Plan.LibraryRootId);
        Assert.IsTrue(result.Plan.SkipConfidentAssignments);
        Assert.AreEqual(1, await scope.Db.LibraryReconciliationPlans.CountAsync());
    }

    /// <summary>Stores a folder-browser selection as a root-relative path and rejects a rooted selection outside that root.</summary>
    [TestMethod]
    public async Task AcceptsOnlyAnInRootAbsoluteFolderBrowserSelection()
    {
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        var selectedFolder = Path.Combine(root.Path, "anime", "season-01");
        Directory.CreateDirectory(selectedFolder);
        await scope.Db.SaveChangesAsync();

        var accepted = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id, selectedFolder), CancellationToken.None);
        var rejected = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id, Path.GetTempPath()), CancellationToken.None);

        Assert.IsTrue(accepted.Succeeded);
        Assert.IsNotNull(accepted.Plan);
        Assert.AreEqual(Path.Combine("anime", "season-01"), accepted.Plan.StartFolder);
        Assert.AreEqual(LibraryReconciliationPlanOutcome.InvalidFolder, rejected.Outcome);
        Assert.AreEqual(1, await scope.Db.LibraryReconciliationPlans.CountAsync());
    }

    /// <summary>Rejects every traversal or rooted path outside the library root before the service can persist a plan.</summary>
    [TestMethod]
    public async Task RefusesTraversalAndRootedFoldersWithoutCreatingAPlan()
    {
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        await scope.Db.SaveChangesAsync();

        foreach (var folder in new[] { "../outside", "child/../outside", Path.GetTempPath() })
        {
            var result = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id, folder), CancellationToken.None);

            Assert.AreEqual(LibraryReconciliationPlanOutcome.InvalidFolder, result.Outcome, folder);
        }

        Assert.AreEqual(0, await scope.Db.LibraryReconciliationPlans.CountAsync());
    }

    /// <summary>Refuses disabled and unavailable roots before any reconciliation state is created.</summary>
    [TestMethod]
    public async Task RefusesUnavailableOrDisabledLibraryRootsWithoutCreatingAPlan()
    {
        using var scope = await Scope.CreateAsync();
        var unavailable = scope.AddRoot("Offline", createDirectory: false);
        var disabled = scope.AddRoot("Disabled", enabled: false);
        await scope.Db.SaveChangesAsync();

        var unavailableResult = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(unavailable.Id), CancellationToken.None);
        var disabledResult = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(disabled.Id), CancellationToken.None);

        Assert.AreEqual(LibraryReconciliationPlanOutcome.StorageUnavailable, unavailableResult.Outcome);
        Assert.AreEqual(LibraryReconciliationPlanOutcome.RootDisabled, disabledResult.Outcome);
        Assert.AreEqual(0, await scope.Db.LibraryReconciliationPlans.CountAsync());
    }

    /// <summary>Proves that scanning records an inventory but never creates a canonical media assignment.</summary>
    [TestMethod]
    public async Task ScanPersistsOnlyAReadOnlyRootBoundInventory()
    {
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        var series = Path.Combine(root.Path, "series");
        Directory.CreateDirectory(series);
        await File.WriteAllTextAsync(Path.Combine(series, "episode-01.mkv"), "media");
        await File.WriteAllTextAsync(Path.Combine(series, "notes.txt"), "notes");
        await scope.Db.SaveChangesAsync();

        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        var scan = await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);

        Assert.AreEqual(LibraryReconciliationPlanOutcome.Scanned, scan.Outcome);
        Assert.AreEqual(2, scan.FilesFound);
        Assert.AreEqual(1, scan.UnclearItems);
        Assert.AreEqual(1, scan.IgnoredItems);
        Assert.AreEqual(LibraryReconciliationPlanStatus.Ready, created.Plan.Status);

        var folder = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "series");
        Assert.IsTrue(folder.IsDirectory);
        Assert.AreEqual(2, folder.FileCount);
        Assert.AreEqual(1, folder.UnresolvedCount);
        Assert.AreEqual(LibraryReconciliationItemState.Unclear, folder.State);
        Assert.IsTrue(await scope.Db.LibraryReconciliationPlanItems.AnyAsync(x => x.RelativePath == "series/episode-01.mkv"));
        Assert.AreEqual(0, await scope.Db.MediaFiles.CountAsync(), "A scan must not create canonical media assignments.");

        var existing = await scope.Service.ScanAsync(created.Plan.Id, CancellationToken.None);
        Assert.AreEqual(LibraryReconciliationPlanOutcome.AlreadyScanned, existing.Outcome);
        Assert.AreEqual(2, existing.FilesFound);
        Assert.AreEqual(1, existing.UnclearItems);
    }

    /// <summary>Persists an explicit canonical work choice as plan evidence without creating media files or assets.</summary>
    [TestMethod]
    public async Task ExplicitWorkMappingChangesOnlyTheReconciliationPlan()
    {
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        Directory.CreateDirectory(Path.Combine(root.Path, "series"));
        await File.WriteAllTextAsync(Path.Combine(root.Path, "series", "episode-01.mkv"), "media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "series", "episode-02.mkv"), "media");
        var work = new Work { CanonicalTitle = "Example series", MediaType = WorkMediaType.Series };
        scope.Db.Works.Add(work);
        await scope.Db.SaveChangesAsync();

        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var item = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "series/episode-01.mkv");

        var result = await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, [item.Id], work.Id), CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        var mapped = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.Id == item.Id);
        Assert.AreEqual(work.Id, mapped.AssignedWorkId);
        Assert.AreEqual(LibraryReconciliationItemState.Recognized, mapped.State);
        Assert.AreEqual(100, mapped.Confidence);
        var mappedFolder = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "series");
        Assert.AreEqual(LibraryReconciliationItemState.Partial, mappedFolder.State);
        Assert.AreEqual(0, await scope.Db.MediaFiles.CountAsync(), "Mapping a plan must not create a local media file.");

        var reset = await scope.Service.ResetWorkAsync(new LibraryReconciliationWorkResetRequest(created.Plan.Id, item.Id), CancellationToken.None);

        Assert.AreEqual(LibraryReconciliationWorkAssignmentOutcome.Reset, reset.Outcome);
        var restored = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.Id == item.Id);
        Assert.IsNull(restored.AssignedWorkId);
        Assert.AreEqual(LibraryReconciliationItemState.Unclear, restored.State);
        var restoredFolder = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "series");
        Assert.AreEqual(LibraryReconciliationItemState.Unclear, restoredFolder.State);
        Assert.AreEqual(0, await scope.Db.MediaFiles.CountAsync(), "Resetting a plan mapping must not change local media files.");
    }

    /// <summary>Applies a folder work as a visible child default while preserving and restoring a direct child override.</summary>
    [TestMethod]
    public async Task FolderWorkMappingIsInheritedAndChildOverridesRemainReversible()
    {
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        Directory.CreateDirectory(Path.Combine(root.Path, "series"));
        await File.WriteAllTextAsync(Path.Combine(root.Path, "series", "episode-01.mkv"), "media");
        var folderWork = new Work { CanonicalTitle = "Folder default", MediaType = WorkMediaType.Series };
        var overrideWork = new Work { CanonicalTitle = "Child override", MediaType = WorkMediaType.Series };
        scope.Db.Works.AddRange(folderWork, overrideWork);
        await scope.Db.SaveChangesAsync();

        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var folder = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "series");
        var file = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "series/episode-01.mkv");

        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, [folder.Id], folderWork.Id), CancellationToken.None);

        var inherited = await scope.Service.GetResolvedWorkAsync(created.Plan.Id, file.Id, CancellationToken.None);
        var inheritedFile = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.Id == file.Id);
        Assert.IsNotNull(inherited);
        Assert.IsTrue(inherited.IsInherited);
        Assert.AreEqual(folderWork.Id, inherited.Id);
        Assert.AreEqual("series", inherited.SourcePath);
        Assert.IsNull(inheritedFile.AssignedWorkId);
        Assert.AreEqual(LibraryReconciliationItemState.Recognized, inheritedFile.State);

        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, [file.Id], overrideWork.Id), CancellationToken.None);

        var overridden = await scope.Service.GetResolvedWorkAsync(created.Plan.Id, file.Id, CancellationToken.None);
        Assert.IsNotNull(overridden);
        Assert.IsFalse(overridden.IsInherited);
        Assert.AreEqual(overrideWork.Id, overridden.Id);

        await scope.Service.ResetWorkAsync(new LibraryReconciliationWorkResetRequest(created.Plan.Id, file.Id), CancellationToken.None);

        var restoredInheritance = await scope.Service.GetResolvedWorkAsync(created.Plan.Id, file.Id, CancellationToken.None);
        Assert.IsNotNull(restoredInheritance);
        Assert.IsTrue(restoredInheritance.IsInherited);
        Assert.AreEqual(folderWork.Id, restoredInheritance.Id);
        Assert.AreEqual(0, await scope.Db.MediaFiles.CountAsync(), "Inherited mapping must not create a local media file.");
    }

    /// <summary>Applies one explicit existing work to a checked file batch without creating canonical media records.</summary>
    [TestMethod]
    public async Task BatchWorkMappingChangesOnlyTheSelectedPlanItems()
    {
        // Prepare three visible media files so only a checked subset may receive the plan decision.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        Directory.CreateDirectory(Path.Combine(root.Path, "series"));
        await File.WriteAllTextAsync(Path.Combine(root.Path, "series", "episode-01.mkv"), "media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "series", "episode-02.mkv"), "media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "series", "episode-03.mkv"), "media");
        var work = new Work { CanonicalTitle = "Batch target", MediaType = WorkMediaType.Series };
        scope.Db.Works.Add(work);
        await scope.Db.SaveChangesAsync();

        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var selectedItems = await scope.Db.LibraryReconciliationPlanItems.Where(x => x.RelativePath == "series/episode-01.mkv" || x.RelativePath == "series/episode-02.mkv").OrderBy(x => x.RelativePath).ToListAsync();
        var selectedItemIds = selectedItems.Select(x => x.Id).ToArray();

        // Apply an explicit work only to the checked plan entries.
        var result = await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, selectedItemIds, work.Id), CancellationToken.None);
        var mappedItems = await scope.Db.LibraryReconciliationPlanItems.Where(x => selectedItemIds.Contains(x.Id)).ToListAsync();

        // Verify that neither the unchecked file nor canonical media persistence was altered.
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(selectedItemIds.Length, mappedItems.Count);
        Assert.IsTrue(mappedItems.All(x => x.AssignedWorkId == work.Id));
        var unselected = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "series/episode-03.mkv");
        Assert.IsNull(unselected.AssignedWorkId);
        Assert.AreEqual(LibraryReconciliationItemState.Unclear, unselected.State);
        Assert.AreEqual(0, await scope.Db.MediaFiles.CountAsync(), "Batch mapping must not create local media files.");
    }

    /// <summary>Skips and restores one mapped media file as reversible plan evidence without touching canonical media records.</summary>
    [TestMethod]
    public async Task ExplicitSkipClearsDirectMappingAndRestoresTheFileForReview()
    {
        // Prepare one scanned media file and one existing canonical work selected by the administrator.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "episode-01.mkv"), "media");
        var work = new Work { CanonicalTitle = "Skip target", MediaType = WorkMediaType.Series };
        scope.Db.Works.Add(work);
        await scope.Db.SaveChangesAsync();
        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var item = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "episode-01.mkv");
        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, [item.Id], work.Id), CancellationToken.None);

        // Skip the file and prove that direct canonical plan evidence was removed without creating media state.
        var skipped = await scope.Service.SetIgnoredAsync(new LibraryReconciliationItemIgnoreRequest(created.Plan.Id, [item.Id], true), CancellationToken.None);
        var skippedItem = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.Id == item.Id);
        Assert.IsTrue(skipped.Succeeded);
        Assert.IsTrue(skippedItem.IsExplicitlyIgnored);
        Assert.IsNull(skippedItem.AssignedWorkId);
        Assert.AreEqual(LibraryReconciliationItemState.Ignored, skippedItem.State);

        // Restore the same plan entry and prove that it returns to the unresolved review queue.
        var restored = await scope.Service.SetIgnoredAsync(new LibraryReconciliationItemIgnoreRequest(created.Plan.Id, [item.Id], false), CancellationToken.None);
        var restoredItem = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.Id == item.Id);
        Assert.IsTrue(restored.Succeeded);
        Assert.IsFalse(restoredItem.IsExplicitlyIgnored);
        Assert.IsNull(restoredItem.AssignedWorkId);
        Assert.AreEqual(LibraryReconciliationItemState.Unclear, restoredItem.State);
        Assert.AreEqual(0, await scope.Db.MediaFiles.CountAsync(), "Skip decisions must not create local media files.");
    }

    /// <summary>Builds a dry-run that distinguishes a ready assignment, an explicit skip and a missing required mapping.</summary>
    [TestMethod]
    public async Task AssignmentOnlyPreviewShowsEveryFileAsAssignableIgnoredOrBlocked()
    {
        // Prepare a scanned file for each safe preview outcome and one pre-existing canonical work.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "mapped.mkv"), "media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "skipped.mkv"), "media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "unresolved.mkv"), "media");
        var work = new Work { CanonicalTitle = "Preview target", MediaType = WorkMediaType.Series };
        scope.Db.Works.Add(work);
        await scope.Db.SaveChangesAsync();
        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var items = await scope.Db.LibraryReconciliationPlanItems.Where(x => !x.IsDirectory).ToDictionaryAsync(x => x.RelativePath);
        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, [items["mapped.mkv"].Id], work.Id), CancellationToken.None);
        await scope.Service.SetIgnoredAsync(new LibraryReconciliationItemIgnoreRequest(created.Plan.Id, [items["skipped.mkv"].Id], true), CancellationToken.None);

        // Build the preview entirely from persisted plan evidence without mutating media or file state.
        var preview = await scope.Service.BuildPreviewAsync(created.Plan.Id, CancellationToken.None);

        // Verify that every scanned media file has one explicit reviewable dry-run outcome.
        Assert.IsNotNull(preview);
        Assert.AreEqual(LibraryReconciliationPreviewAction.AssignOnly, preview.Items.Single(x => x.RelativePath == "mapped.mkv").Action);
        Assert.AreEqual("Preview target", preview.Items.Single(x => x.RelativePath == "mapped.mkv").WorkTitle);
        Assert.AreEqual(LibraryReconciliationPreviewAction.KeepIgnored, preview.Items.Single(x => x.RelativePath == "skipped.mkv").Action);
        Assert.AreEqual(LibraryReconciliationPreviewAction.Blocked, preview.Items.Single(x => x.RelativePath == "unresolved.mkv").Action);
        Assert.AreEqual(0, await scope.Db.MediaFiles.CountAsync(), "Building a dry-run must not create local media files.");
    }

    /// <summary>Maps a file only to an existing episode of its effective work and clears that unit without clearing the work.</summary>
    [TestMethod]
    public async Task EpisodeMappingUsesTheEffectiveWorkAndRemainsReversible()
    {
        // Prepare a series work with one canonical episode and one scanned video file.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "episode-01.mkv"), "media");
        var work = new Work { CanonicalTitle = "Episode target", MediaType = WorkMediaType.Series };
        scope.Db.Works.Add(work);
        await scope.Db.SaveChangesAsync();
        var episode = new WorkEpisode { WorkId = work.Id, SeasonNumber = 1, EpisodeNumber = 1, Title = "Pilot" };
        scope.Db.WorkEpisodes.Add(episode);
        await scope.Db.SaveChangesAsync();
        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var item = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "episode-01.mkv");
        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, [item.Id], work.Id), CancellationToken.None);

        // Select the pre-existing episode and prove that it belongs to the effective work.
        var candidates = await scope.Service.GetEpisodeCandidatesAsync(created.Plan.Id, item.Id, CancellationToken.None);
        var assigned = await scope.Service.AssignEpisodeAsync(new LibraryReconciliationEpisodeAssignmentRequest(created.Plan.Id, item.Id, episode.Id), CancellationToken.None);
        var assignedItem = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.Id == item.Id);
        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual(episode.Id, candidates[0].Id);
        Assert.IsTrue(assigned.Succeeded);
        Assert.AreEqual(episode.Id, assignedItem.AssignedWorkEpisodeId);

        // Clear only the file-level unit and retain the administrator's explicit canonical work mapping.
        var reset = await scope.Service.AssignEpisodeAsync(new LibraryReconciliationEpisodeAssignmentRequest(created.Plan.Id, item.Id, null), CancellationToken.None);
        var resetItem = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.Id == item.Id);
        Assert.IsTrue(reset.Succeeded);
        Assert.IsNull(resetItem.AssignedWorkEpisodeId);
        Assert.AreEqual(work.Id, resetItem.AssignedWorkId);
        Assert.AreEqual(0, await scope.Db.MediaFiles.CountAsync(), "Episode mapping must not create local media files.");
    }

    /// <summary>Maps checked files to consecutive existing episodes only after their parent folder has an explicit canonical work.</summary>
    [TestMethod]
    public async Task EpisodeSequenceUsesTheAdministratorConfirmedFileOrder()
    {
        // Prepare one mapped folder, two media files and a work with three pre-existing canonical episodes.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        Directory.CreateDirectory(Path.Combine(root.Path, "season"));
        await File.WriteAllTextAsync(Path.Combine(root.Path, "season", "first.mkv"), "media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "season", "second.mkv"), "media");
        var work = new Work { CanonicalTitle = "Sequence target", MediaType = WorkMediaType.Series };
        scope.Db.Works.Add(work);
        await scope.Db.SaveChangesAsync();
        var episodes = Enumerable.Range(1, 3).Select(number => new WorkEpisode { WorkId = work.Id, SeasonNumber = 1, EpisodeNumber = number }).ToArray();
        scope.Db.WorkEpisodes.AddRange(episodes);
        await scope.Db.SaveChangesAsync();
        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var folder = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "season");
        var files = await scope.Db.LibraryReconciliationPlanItems.Where(x => x.RelativePath == "season/first.mkv" || x.RelativePath == "season/second.mkv").OrderBy(x => x.RelativePath).ToArrayAsync();
        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, [folder.Id], work.Id), CancellationToken.None);

        // Reject a mismatched explicit range, then accept the exact E02-E03 range in the administrator-confirmed file order.
        var rejected = await scope.Service.AssignEpisodeSequenceAsync(new LibraryReconciliationEpisodeSequenceRequest(created.Plan.Id, files.Select(x => x.Id).ToArray(), episodes[0].Id, episodes[2].Id), CancellationToken.None);
        var assigned = await scope.Service.AssignEpisodeSequenceAsync(new LibraryReconciliationEpisodeSequenceRequest(created.Plan.Id, files.Select(x => x.Id).ToArray(), episodes[1].Id, episodes[2].Id), CancellationToken.None);
        var mappedFiles = await scope.Db.LibraryReconciliationPlanItems.Where(x => files.Select(file => file.Id).Contains(x.Id)).OrderBy(x => x.RelativePath).ToArrayAsync();

        // Verify no filename parser creates identity: only the chosen existing episode sequence is persisted.
        Assert.IsFalse(rejected.Succeeded);
        Assert.IsTrue(assigned.Succeeded);
        Assert.AreEqual(episodes[1].Id, mappedFiles[0].AssignedWorkEpisodeId);
        Assert.AreEqual(episodes[2].Id, mappedFiles[1].AssignedWorkEpisodeId);
        Assert.AreEqual(0, await scope.Db.MediaFiles.CountAsync(), "Episode sequence mapping must not create local media files.");
    }

    /// <summary>Clears a descendant episode when a changed parent mapping makes the inherited work incompatible with that unit.</summary>
    [TestMethod]
    public async Task ParentWorkRemappingClearsIncompatibleDescendantUnits()
    {
        // Prepare one folder whose child initially inherits the same work as its explicitly selected episode.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        Directory.CreateDirectory(Path.Combine(root.Path, "season"));
        await File.WriteAllTextAsync(Path.Combine(root.Path, "season", "episode-01.mkv"), "media");
        var firstWork = new Work { CanonicalTitle = "First target", MediaType = WorkMediaType.Series };
        var replacementWork = new Work { CanonicalTitle = "Replacement target", MediaType = WorkMediaType.Series };
        scope.Db.Works.AddRange(firstWork, replacementWork);
        await scope.Db.SaveChangesAsync();
        var firstEpisode = new WorkEpisode { WorkId = firstWork.Id, SeasonNumber = 1, EpisodeNumber = 1 };
        scope.Db.WorkEpisodes.Add(firstEpisode);
        await scope.Db.SaveChangesAsync();
        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var folder = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "season");
        var file = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "season/episode-01.mkv");
        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, [folder.Id], firstWork.Id), CancellationToken.None);
        await scope.Service.AssignEpisodeAsync(new LibraryReconciliationEpisodeAssignmentRequest(created.Plan.Id, file.Id, firstEpisode.Id), CancellationToken.None);

        // Replace the inherited work and prove that the incompatible file-level identity is removed instead of becoming stale evidence.
        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, [folder.Id], replacementWork.Id), CancellationToken.None);
        var remappedFile = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.Id == file.Id);
        Assert.IsNull(remappedFile.AssignedWorkEpisodeId);
        Assert.AreEqual(0, await scope.Db.MediaFiles.CountAsync(), "Reconciling inherited work must not create local media files.");
    }

    /// <summary>Maps a file only to an existing volume of its effective work and clears that unit without clearing the work.</summary>
    [TestMethod]
    public async Task VolumeMappingUsesTheEffectiveWorkAndRemainsReversible()
    {
        // Prepare a work with one existing canonical volume and one scanned reading file.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "volume-01.cbz"), "media");
        var work = new Work { CanonicalTitle = "Volume target", MediaType = WorkMediaType.Manga };
        scope.Db.Works.Add(work);
        await scope.Db.SaveChangesAsync();
        var volume = new WorkVolume { WorkId = work.Id, Number = 1, Title = "Volume 1" };
        scope.Db.WorkVolumes.Add(volume);
        await scope.Db.SaveChangesAsync();
        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var item = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "volume-01.cbz");
        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, [item.Id], work.Id), CancellationToken.None);

        // Select an existing volume and verify that the persisted unit references the effective work only.
        var candidates = await scope.Service.GetVolumeCandidatesAsync(created.Plan.Id, item.Id, CancellationToken.None);
        var assigned = await scope.Service.AssignVolumeAsync(new LibraryReconciliationVolumeAssignmentRequest(created.Plan.Id, item.Id, volume.Id), CancellationToken.None);
        var assignedItem = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.Id == item.Id);
        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual(volume.Id, candidates[0].Id);
        Assert.IsTrue(assigned.Succeeded);
        Assert.AreEqual(volume.Id, assignedItem.AssignedWorkVolumeId);

        // Clear only the file-level volume and retain the administrator's explicit canonical work mapping.
        var reset = await scope.Service.AssignVolumeAsync(new LibraryReconciliationVolumeAssignmentRequest(created.Plan.Id, item.Id, null), CancellationToken.None);
        var resetItem = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.Id == item.Id);
        Assert.IsTrue(reset.Succeeded);
        Assert.IsNull(resetItem.AssignedWorkVolumeId);
        Assert.AreEqual(work.Id, resetItem.AssignedWorkId);
        Assert.AreEqual(0, await scope.Db.MediaFiles.CountAsync(), "Volume mapping must not create local media files.");
    }

    /// <summary>Maps a file only to an existing chapter of its effective work and selected volume, then restores the unit to review.</summary>
    [TestMethod]
    public async Task ChapterMappingUsesTheEffectiveWorkAndVolumeAndRemainsReversible()
    {
        // Prepare one work, volume and chapter together with one scanned reading file.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "chapter-01.cbz"), "media");
        var work = new Work { CanonicalTitle = "Chapter target", MediaType = WorkMediaType.Manga };
        scope.Db.Works.Add(work);
        await scope.Db.SaveChangesAsync();
        var volume = new WorkVolume { WorkId = work.Id, Number = 1 };
        scope.Db.WorkVolumes.Add(volume);
        await scope.Db.SaveChangesAsync();
        var chapter = new WorkChapter { WorkId = work.Id, VolumeId = volume.Id, Number = 1, Title = "Chapter 1" };
        scope.Db.WorkChapters.Add(chapter);
        await scope.Db.SaveChangesAsync();
        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var item = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "chapter-01.cbz");
        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, [item.Id], work.Id), CancellationToken.None);
        await scope.Service.AssignVolumeAsync(new LibraryReconciliationVolumeAssignmentRequest(created.Plan.Id, item.Id, volume.Id), CancellationToken.None);

        // Select the existing chapter constrained to the explicit volume.
        var candidates = await scope.Service.GetChapterCandidatesAsync(created.Plan.Id, item.Id, CancellationToken.None);
        var assigned = await scope.Service.AssignChapterAsync(new LibraryReconciliationChapterAssignmentRequest(created.Plan.Id, item.Id, chapter.Id), CancellationToken.None);
        var assignedItem = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.Id == item.Id);
        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual(chapter.Id, candidates[0].Id);
        Assert.IsTrue(assigned.Succeeded);
        Assert.AreEqual(chapter.Id, assignedItem.AssignedWorkChapterId);

        // Clear only the chapter and retain the validated work and volume choices.
        var reset = await scope.Service.AssignChapterAsync(new LibraryReconciliationChapterAssignmentRequest(created.Plan.Id, item.Id, null), CancellationToken.None);
        var resetItem = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.Id == item.Id);
        Assert.IsTrue(reset.Succeeded);
        Assert.IsNull(resetItem.AssignedWorkChapterId);
        Assert.AreEqual(volume.Id, resetItem.AssignedWorkVolumeId);
        Assert.AreEqual(0, await scope.Db.MediaFiles.CountAsync(), "Chapter mapping must not create local media files.");
    }

    /// <summary>Persists one mutually exclusive organization policy as plan evidence without performing a filesystem operation.</summary>
    [TestMethod]
    public async Task OrganizationModeIsPersistedOnlyAfterThePlanIsReady()
    {
        // Prepare a completed root-bound inventory before selecting the physical-operation policy.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "movie.mkv"), "media");
        await scope.Db.SaveChangesAsync();
        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);

        // Save the canonical organization mode and verify that only plan metadata changed.
        var saved = await scope.Service.SetOrganizationModeAsync(new LibraryReconciliationOrganizationModeRequest(created.Plan.Id, LibraryReconciliationOrganizationMode.OrganizeCanonical), CancellationToken.None);
        var plan = await scope.Db.LibraryReconciliationPlans.SingleAsync(x => x.Id == created.Plan.Id);
        Assert.IsTrue(saved.Succeeded);
        Assert.AreEqual(LibraryReconciliationOrganizationMode.OrganizeCanonical, plan.OrganizationMode);
        Assert.AreEqual(0, await scope.Db.MediaFiles.CountAsync(), "Selecting an organization policy must not create local media files.");
    }

    /// <summary>Executes only a fingerprint-confirmed rename preview and commits the corresponding durable canonical file link.</summary>
    [TestMethod]
    public async Task ConfirmedRenamePreviewMovesTheFileAndCommitsItsCanonicalLink()
    {
        // Prepare one root-bound file and one explicitly chosen existing canonical work.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        var sourcePath = Path.Combine(root.Path, "unstructured.mkv");
        await File.WriteAllTextAsync(sourcePath, "media");
        var work = new Work { CanonicalTitle = "Confirmed title", MediaType = WorkMediaType.Movie };
        scope.Db.Works.Add(work);
        await scope.Db.SaveChangesAsync();
        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var item = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "unstructured.mkv");
        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, [item.Id], work.Id), CancellationToken.None);
        await scope.Service.SetOrganizationModeAsync(new LibraryReconciliationOrganizationModeRequest(created.Plan.Id, LibraryReconciliationOrganizationMode.RenameFiles), CancellationToken.None);

        // Confirm the exact generated preview, then verify its physical and durable canonical consequences.
        var preview = await scope.Service.BuildPreviewAsync(created.Plan.Id, CancellationToken.None);
        var execution = await scope.Service.ExecuteAsync(new LibraryReconciliationExecutionRequest(created.Plan.Id, preview!.Fingerprint), CancellationToken.None);
        var link = await scope.Db.LibraryReconciliationFileLinks.SingleAsync(x => x.PlanId == created.Plan.Id);
        var completedPlan = await scope.Db.LibraryReconciliationPlans.SingleAsync(x => x.Id == created.Plan.Id);
        Assert.IsTrue(execution.Succeeded);
        Assert.AreEqual(LibraryReconciliationPreviewAction.Rename, preview.Items.Single().Action);
        Assert.IsFalse(File.Exists(sourcePath));
        Assert.IsTrue(File.Exists(Path.Combine(root.Path, "Confirmed title.mkv")));
        Assert.AreEqual("unstructured.mkv", link.OriginalRelativePath);
        Assert.AreEqual("Confirmed title.mkv", link.RelativePath);
        Assert.AreEqual(work.Id, link.WorkId);
        Assert.AreEqual(LibraryReconciliationPlanStatus.Completed, completedPlan.Status);
        Assert.IsTrue(execution.OperationId.HasValue);
        Assert.AreEqual(execution.OperationId, completedPlan.ExecutionOperationId);
    }

    /// <summary>A rename on a read-only mount must surface as a blocking conflict in the dry-run instead of failing mid-execution.</summary>
    [TestMethod]
    public async Task PreviewReportsAConflictWhenTheLibraryRootIsOnAReadOnlyMount()
    {
        using var scope = await Scope.CreateAsync(readOnlyMount: true);
        var root = scope.AddRoot("Media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "unstructured.mkv"), "media");
        var work = new Work { CanonicalTitle = "Confirmed title", MediaType = WorkMediaType.Movie };
        scope.Db.Works.Add(work);
        await scope.Db.SaveChangesAsync();
        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var item = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "unstructured.mkv");
        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, [item.Id], work.Id), CancellationToken.None);
        await scope.Service.SetOrganizationModeAsync(new LibraryReconciliationOrganizationModeRequest(created.Plan.Id, LibraryReconciliationOrganizationMode.RenameFiles), CancellationToken.None);

        var preview = await scope.Service.BuildPreviewAsync(created.Plan.Id, CancellationToken.None);

        Assert.AreEqual(LibraryReconciliationPreviewAction.Conflict, preview!.Items.Single().Action);
        Assert.IsFalse(preview.CanExecute);
    }

    /// <summary>Removes only empty moved-source directories when the administrator explicitly selects cleanup and preserves unknown neighboring files.</summary>
    [TestMethod]
    public async Task CanonicalOrganizationCanRemoveEmptySourceFoldersWithoutDeletingUnknownFiles()
    {
        // Prepare one nested media file and one neighboring unknown file that must keep its parent directory alive.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        var sourceDirectory = Path.Combine(root.Path, "legacy", "season");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "episode.mkv"), "media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "legacy", "keep.txt"), "unknown");
        var work = new Work { CanonicalTitle = "Organized target", MediaType = WorkMediaType.Series };
        scope.Db.Works.Add(work);
        await scope.Db.SaveChangesAsync();
        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var sourceFolder = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.PlanId == created.Plan.Id && x.RelativePath == "legacy/season");
        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, [sourceFolder.Id], work.Id), CancellationToken.None);
        var policyRequest = new LibraryReconciliationOrganizationModeRequest(created.Plan.Id, LibraryReconciliationOrganizationMode.OrganizeCanonical, true);
        var policy = await scope.Service.SetOrganizationModeAsync(policyRequest, CancellationToken.None);

        // Execute the approved move and prove cleanup is opt-in, scope-bound and never removes an unknown sibling file.
        var preview = await scope.Service.BuildPreviewAsync(created.Plan.Id, CancellationToken.None);
        var execution = await scope.Service.ExecuteAsync(new LibraryReconciliationExecutionRequest(created.Plan.Id, preview!.Fingerprint), CancellationToken.None);
        var plan = await scope.Db.LibraryReconciliationPlans.SingleAsync(x => x.Id == created.Plan.Id);
        Assert.IsTrue(policy.Succeeded);
        Assert.IsTrue(execution.Succeeded);
        Assert.IsTrue(plan.RemoveEmptySourceFolders);
        Assert.IsFalse(Directory.Exists(sourceDirectory));
        Assert.IsTrue(File.Exists(Path.Combine(root.Path, "legacy", "keep.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(root.Path, "Series", "Organized target", "Organized target.mkv")));
    }

    /// <summary>Creates and reverses a plan-only split group across files from separate physical folders without changing either path.</summary>
    [TestMethod]
    public async Task LogicalGroupsSupportReversibleCrossFolderMergeReview()
    {
        // Prepare two files in different physical folders so their shared review group cannot be mistaken for a physical hierarchy change.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        Directory.CreateDirectory(Path.Combine(root.Path, "one"));
        Directory.CreateDirectory(Path.Combine(root.Path, "two"));
        await File.WriteAllTextAsync(Path.Combine(root.Path, "one", "first.mkv"), "media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "two", "second.mkv"), "media");
        await scope.Db.SaveChangesAsync();
        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var items = await scope.Db.LibraryReconciliationPlanItems.Where(x => x.RelativePath == "one/first.mkv" || x.RelativePath == "two/second.mkv").OrderBy(x => x.RelativePath).ToArrayAsync();

        // Group the files together, then remove only their plan evidence and verify that no physical operation occurred.
        var grouped = await scope.Service.CreateLogicalGroupAsync(new LibraryReconciliationLogicalGroupRequest(created.Plan.Id, items.Select(x => x.Id).ToArray(), "Specials"), CancellationToken.None);
        var groups = await scope.Service.GetLogicalGroupsAsync(created.Plan.Id, CancellationToken.None);
        var cleared = await scope.Service.ClearLogicalGroupAsync(new LibraryReconciliationLogicalGroupClearRequest(created.Plan.Id, items.Select(x => x.Id).ToArray()), CancellationToken.None);
        var clearedItems = await scope.Db.LibraryReconciliationPlanItems.Where(x => items.Select(item => item.Id).Contains(x.Id)).ToArrayAsync();
        Assert.IsTrue(grouped.Succeeded);
        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual(2, groups[0].FileCount);
        Assert.IsTrue(cleared.Succeeded);
        Assert.IsTrue(clearedItems.All(x => x.LogicalGroupId is null));
        Assert.IsTrue(File.Exists(Path.Combine(root.Path, "one", "first.mkv")));
        Assert.IsTrue(File.Exists(Path.Combine(root.Path, "two", "second.mkv")));
    }

    /// <summary>Records a unique title match as advisory scan evidence without deriving canonical Work identity from the physical path.</summary>
    [TestMethod]
    public async Task ScanTitleEvidenceNeverAssignsACanonicalWork()
    {
        // Prepare a folder whose title exactly matches one existing work and a file that still requires owner confirmation.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        Directory.CreateDirectory(Path.Combine(root.Path, "Example Series"));
        await File.WriteAllTextAsync(Path.Combine(root.Path, "Example Series", "episode.mkv"), "media");
        var work = new Work { CanonicalTitle = "Example Series", MediaType = WorkMediaType.Series };
        scope.Db.Works.Add(work);
        await scope.Db.SaveChangesAsync();

        // Scan and assert that the recognizer exposes evidence while retaining the unresolved, identity-free item state.
        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var file = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "Example Series/episode.mkv");
        Assert.AreEqual(LibraryReconciliationItemState.Unclear, file.State);
        Assert.AreEqual(60, file.Confidence);
        Assert.IsNull(file.AssignedWorkId);
        StringAssert.Contains(file.DetectionSummary, work.CanonicalTitle);
    }

    /// <summary>Allows an administrator to omit optional name-based hints while preserving the same safe unresolved inventory.</summary>
    [TestMethod]
    public async Task ScanCanDisableOptionalFilenameEvidence()
    {
        // Prepare a physical title that would otherwise produce advisory evidence for one existing work.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        Directory.CreateDirectory(Path.Combine(root.Path, "Example Series"));
        await File.WriteAllTextAsync(Path.Combine(root.Path, "Example Series", "episode.mkv"), "media");
        scope.Db.Works.Add(new Work { CanonicalTitle = "Example Series", MediaType = WorkMediaType.Series });
        await scope.Db.SaveChangesAsync();

        // Disable the optional evidence stage and retain a normal, identity-free unresolved file item.
        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id, AnalyzeFilenameEvidence: false), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var file = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "Example Series/episode.mkv");
        Assert.IsFalse(created.Plan.AnalyzeFilenameEvidence);
        Assert.AreEqual(LibraryReconciliationItemState.Unclear, file.State);
        Assert.AreEqual(0, file.Confidence);
        Assert.IsNull(file.AssignedWorkId);
        Assert.AreEqual("File needs an explicit canonical assignment.", file.DetectionSummary);
    }

    /// <summary>Resets checked direct mapping overrides together, including stale release metadata that no longer belongs to a work choice.</summary>
    [TestMethod]
    public async Task BatchWorkResetClearsDirectWorkUnitAndReleaseOverrides()
    {
        // Prepare two mapped media files and one release selection that must not survive a cleared work mapping.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "one.mkv"), "media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "two.mkv"), "media");
        var work = new Work { CanonicalTitle = "Reset target", MediaType = WorkMediaType.Movie };
        scope.Db.Works.Add(work);
        await scope.Db.SaveChangesAsync();
        var edition = new WorkEdition { WorkId = work.Id, EditionKey = "director", Language = "en" };
        scope.Db.WorkEditions.Add(edition);
        await scope.Db.SaveChangesAsync();

        // Persist explicit mapping evidence, then reset the checked batch without touching the physical source files.
        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var items = await scope.Db.LibraryReconciliationPlanItems.Where(x => !x.IsDirectory).OrderBy(x => x.RelativePath).ToArrayAsync();
        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, items.Select(x => x.Id).ToArray(), work.Id), CancellationToken.None);
        await scope.Service.AssignReleaseMetadataAsync(new LibraryReconciliationReleaseMetadataRequest(created.Plan.Id, items[0].Id, edition.Id, null, "en", "en", null, "source"), CancellationToken.None);
        var reset = await scope.Service.ResetWorksAsync(new LibraryReconciliationBatchWorkResetRequest(created.Plan.Id, items.Select(x => x.Id).ToArray()), CancellationToken.None);
        var resetItems = await scope.Db.LibraryReconciliationPlanItems.Where(x => !x.IsDirectory).ToArrayAsync();
        Assert.IsTrue(reset.Succeeded);
        Assert.IsTrue(resetItems.All(x => x.AssignedWorkId is null && x.AssignedWorkEpisodeId is null && x.AssignedWorkEditionId is null && x.AssignedWorkVersionId is null));
        Assert.IsTrue(resetItems.All(x => x.Language is null && x.AudioLanguage is null && x.SubtitleLanguage is null && x.QualitySource is null));
        Assert.IsTrue(File.Exists(Path.Combine(root.Path, "one.mkv")));
        Assert.IsTrue(File.Exists(Path.Combine(root.Path, "two.mkv")));
    }

    /// <summary>Blocks execution preview when a reviewed source file changes after the scan, even if its canonical work mapping remains valid.</summary>
    [TestMethod]
    public async Task PreviewBlocksAFileThatChangedSinceTheScan()
    {
        // Prepare a mapped source and preserve its scan observation before changing its physical content.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        var sourcePath = Path.Combine(root.Path, "movie.mkv");
        await File.WriteAllTextAsync(sourcePath, "before");
        var work = new Work { CanonicalTitle = "Reviewed movie", MediaType = WorkMediaType.Movie };
        scope.Db.Works.Add(work);
        await scope.Db.SaveChangesAsync();
        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var item = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "movie.mkv");
        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, [item.Id], work.Id), CancellationToken.None);
        await File.WriteAllTextAsync(sourcePath, "after with a different size");

        // The stale source must become an explicit preview blocker instead of being executed from an obsolete inventory.
        var preview = await scope.Service.BuildPreviewAsync(created.Plan.Id, CancellationToken.None);
        Assert.AreEqual(LibraryReconciliationPreviewAction.Blocked, preview!.Items.Single().Action);
        Assert.IsFalse(preview.CanExecute);
    }

    /// <summary>Persists only release metadata that belongs to the effective work and carries it into the final reconciliation file link.</summary>
    [TestMethod]
    public async Task ReleaseMetadataRequiresTheEffectiveWorkAndCommitsWithTheFileLink()
    {
        // Prepare one mapped file with one edition/version pair and a competing work whose release must be rejected.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "movie.mkv"), "media");
        var work = new Work { CanonicalTitle = "Release target", MediaType = WorkMediaType.Movie };
        var otherWork = new Work { CanonicalTitle = "Other target", MediaType = WorkMediaType.Movie };
        scope.Db.Works.AddRange(work, otherWork);
        await scope.Db.SaveChangesAsync();
        var edition = new WorkEdition { WorkId = work.Id, EditionKey = "blu-ray", Language = "en", Format = "bluray" };
        var version = new WorkVersion { WorkId = work.Id, EditionId = edition.Id, VersionKey = "1080p", Quality = "1080p", Source = "bluray" };
        var invalidEdition = new WorkEdition { WorkId = otherWork.Id, EditionKey = "web", Language = "de" };
        scope.Db.WorkEditions.AddRange(edition, invalidEdition);
        scope.Db.WorkVersions.Add(version);
        await scope.Db.SaveChangesAsync();
        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var item = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.RelativePath == "movie.mkv");
        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, [item.Id], work.Id), CancellationToken.None);

        // Reject cross-work identity, then save the valid metadata and execute the safe assignment-only link commit.
        var rejected = await scope.Service.AssignReleaseMetadataAsync(new LibraryReconciliationReleaseMetadataRequest(created.Plan.Id, item.Id, invalidEdition.Id, null, "de", null, null, null), CancellationToken.None);
        var saved = await scope.Service.AssignReleaseMetadataAsync(new LibraryReconciliationReleaseMetadataRequest(created.Plan.Id, item.Id, edition.Id, version.Id, "en", "ja", "en", "1080p Blu-ray"), CancellationToken.None);
        var preview = await scope.Service.BuildPreviewAsync(created.Plan.Id, CancellationToken.None);
        var executed = await scope.Service.ExecuteAsync(new LibraryReconciliationExecutionRequest(created.Plan.Id, preview!.Fingerprint), CancellationToken.None);
        var link = await scope.Db.LibraryReconciliationFileLinks.SingleAsync(x => x.PlanId == created.Plan.Id);
        Assert.IsFalse(rejected.Succeeded);
        Assert.IsTrue(saved.Succeeded);
        Assert.IsTrue(executed.Succeeded);
        Assert.AreEqual(edition.Id, link.WorkEditionId);
        Assert.AreEqual(version.Id, link.WorkVersionId);
        Assert.AreEqual("ja", link.AudioLanguage);
        Assert.AreEqual("1080p Blu-ray", link.QualitySource);
    }

    /// <summary>Applies folder release defaults to descendant links while retaining a child's direct per-field override.</summary>
    [TestMethod]
    public async Task FolderReleaseMetadataIsInheritedPerFieldAndCommittedToTheFileLink()
    {
        // Prepare one folder-backed media file and an existing canonical release that the folder may explicitly choose.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        Directory.CreateDirectory(Path.Combine(root.Path, "series"));
        await File.WriteAllTextAsync(Path.Combine(root.Path, "series", "episode.mkv"), "media");
        var work = new Work { CanonicalTitle = "Inherited release target", MediaType = WorkMediaType.Series };
        scope.Db.Works.Add(work);
        await scope.Db.SaveChangesAsync();
        var edition = new WorkEdition { WorkId = work.Id, EditionKey = "collector", Language = "en" };
        var version = new WorkVersion { WorkId = work.Id, EditionId = edition.Id, VersionKey = "1080p", Quality = "1080p", Source = "disc" };
        scope.Db.WorkEditions.Add(edition);
        scope.Db.WorkVersions.Add(version);
        await scope.Db.SaveChangesAsync();
        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var folder = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.PlanId == created.Plan.Id && x.RelativePath == "series");
        var file = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.PlanId == created.Plan.Id && x.RelativePath == "series/episode.mkv");
        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(created.Plan.Id, [folder.Id], work.Id), CancellationToken.None);

        // Set the reusable folder defaults, then override only the child's audio value without copying the remaining defaults.
        var folderMetadata = new LibraryReconciliationReleaseMetadataRequest(created.Plan.Id, folder.Id, edition.Id, version.Id, "en", "ja", "en", "1080p disc");
        var savedFolderDefaults = await scope.Service.AssignReleaseMetadataAsync(folderMetadata, CancellationToken.None);
        var savedChildOverride = await scope.Service.AssignReleaseMetadataAsync(new LibraryReconciliationReleaseMetadataRequest(created.Plan.Id, file.Id, null, null, null, "de", null, null), CancellationToken.None);
        var resolved = await scope.Service.GetResolvedReleaseMetadataAsync(created.Plan.Id, file.Id, CancellationToken.None);
        var preview = await scope.Service.BuildPreviewAsync(created.Plan.Id, CancellationToken.None);
        var executed = await scope.Service.ExecuteAsync(new LibraryReconciliationExecutionRequest(created.Plan.Id, preview!.Fingerprint), CancellationToken.None);
        var link = await scope.Db.LibraryReconciliationFileLinks.SingleAsync(x => x.PlanItemId == file.Id);

        Assert.IsTrue(savedFolderDefaults.Succeeded);
        Assert.IsTrue(savedChildOverride.Succeeded);
        Assert.IsNotNull(resolved);
        Assert.AreEqual(edition.Id, resolved.EditionId);
        Assert.AreEqual(version.Id, resolved.VersionId);
        Assert.AreEqual("series", resolved.LanguageSourcePath);
        Assert.AreEqual("de", resolved.AudioLanguage);
        Assert.AreEqual("series/episode.mkv", resolved.AudioLanguageSourcePath);
        Assert.IsTrue(executed.Succeeded);
        Assert.AreEqual(edition.Id, link.WorkEditionId);
        Assert.AreEqual(version.Id, link.WorkVersionId);
        Assert.AreEqual("en", link.Language);
        Assert.AreEqual("de", link.AudioLanguage);
        Assert.AreEqual("1080p disc", link.QualitySource);
    }

    /// <summary>Reuses a visible confirmed link in a later plan and updates it for an explicit correction without creating a duplicate root-relative link.</summary>
    [TestMethod]
    public async Task ExistingCommittedLinkCanBeReopenedAndCorrected()
    {
        // Commit the original confirmed link for one physical file.
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "movie.mkv"), "media");
        var firstWork = new Work { CanonicalTitle = "First mapping", MediaType = WorkMediaType.Movie };
        var correctedWork = new Work { CanonicalTitle = "Corrected mapping", MediaType = WorkMediaType.Movie };
        scope.Db.Works.AddRange(firstWork, correctedWork);
        await scope.Db.SaveChangesAsync();
        var originalPlan = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(originalPlan.Plan!.Id, CancellationToken.None);
        var originalItem = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.PlanId == originalPlan.Plan.Id && x.RelativePath == "movie.mkv");
        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(originalPlan.Plan.Id, [originalItem.Id], firstWork.Id), CancellationToken.None);
        var originalPreview = await scope.Service.BuildPreviewAsync(originalPlan.Plan.Id, CancellationToken.None);
        await scope.Service.ExecuteAsync(new LibraryReconciliationExecutionRequest(originalPlan.Plan.Id, originalPreview!.Fingerprint), CancellationToken.None);

        // Open a new plan with the explicit confirmed-link shortcut, correct the mapping, and prove only one current link remains.
        var correctionPlan = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id, SkipConfidentAssignments: true), CancellationToken.None);
        await scope.Service.ScanAsync(correctionPlan.Plan!.Id, CancellationToken.None);
        var correctionItem = await scope.Db.LibraryReconciliationPlanItems.SingleAsync(x => x.PlanId == correctionPlan.Plan.Id && x.RelativePath == "movie.mkv");
        await scope.Service.AssignWorkAsync(new LibraryReconciliationWorkAssignmentRequest(correctionPlan.Plan.Id, [correctionItem.Id], correctedWork.Id), CancellationToken.None);
        var correctionPreview = await scope.Service.BuildPreviewAsync(correctionPlan.Plan.Id, CancellationToken.None);
        var corrected = await scope.Service.ExecuteAsync(new LibraryReconciliationExecutionRequest(correctionPlan.Plan.Id, correctionPreview!.Fingerprint), CancellationToken.None);
        var links = await scope.Db.LibraryReconciliationFileLinks.Where(x => x.LibraryRootId == root.Id).ToArrayAsync();
        Assert.AreEqual(LibraryReconciliationItemState.Recognized, correctionItem.State);
        Assert.IsTrue(corrected.Succeeded);
        Assert.AreEqual(1, links.Length);
        Assert.AreEqual(correctedWork.Id, links[0].WorkId);
        Assert.AreEqual(correctionPlan.Plan.Id, links[0].PlanId);
    }

    /// <summary>Finds only existing canonical works for a scanned plan and never turns a search term into identity.</summary>
    [TestMethod]
    public async Task WorkSearchIsCaseInsensitiveAndNeverCreatesACanonicalWork()
    {
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        var matchingWork = new Work { CanonicalTitle = "Example series", MediaType = WorkMediaType.Series };
        var otherWork = new Work { CanonicalTitle = "Different series", MediaType = WorkMediaType.Series };
        scope.Db.Works.AddRange(matchingWork, otherWork);
        await scope.Db.SaveChangesAsync();

        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        await scope.Service.ScanAsync(created.Plan!.Id, CancellationToken.None);
        var results = await scope.Service.SearchWorksAsync(created.Plan.Id, "EXAMPLE", CancellationToken.None);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(matchingWork.Id, results[0].Id);
        Assert.AreEqual(2, await scope.Db.Works.CountAsync());
    }

    /// <summary>A blank query fills the work picker alphabetically so the mapping step never offers an empty list.</summary>
    [TestMethod]
    public async Task BlankWorkSearchListsExistingWorksAlphabetically()
    {
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        scope.Db.Works.AddRange(new Work { CanonicalTitle = "Zeta", MediaType = WorkMediaType.Series }, new Work { CanonicalTitle = "Alpha", MediaType = WorkMediaType.Series });
        await scope.Db.SaveChangesAsync();

        var created = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        var results = await scope.Service.SearchWorksAsync(created.Plan!.Id, " ", CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "Alpha", "Zeta" }, results.Select(x => x.Title).ToArray());
    }

    /// <summary>The wizard resumes the newest scanned plan and ignores drafts that were never scanned.</summary>
    [TestMethod]
    public async Task LatestReadyPlanIsTheNewestScannedPlan()
    {
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "episode-01.mkv"), "media");
        await scope.Db.SaveChangesAsync();
        var scanned = await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);
        Assert.IsNotNull(scanned.Plan, scanned.Message);
        await scope.Service.ScanAsync(scanned.Plan.Id, CancellationToken.None);
        await scope.Service.CreateAsync(new LibraryReconciliationPlanRequest(root.Id), CancellationToken.None);

        var latest = await scope.Service.GetLatestReadyPlanIdAsync(CancellationToken.None);

        Assert.AreEqual(scanned.Plan.Id, latest);
    }

    private sealed class Scope : IDisposable
    {
        /// <summary>Retains the isolated files, database and service used by one reconciliation test.</summary>
        private Scope(string basePath, AppDbContext db, LibraryReconciliationPlanService service)
        {
            BasePath = basePath;
            Db = db;
            Service = service;
        }

        public string BasePath { get; }

        public AppDbContext Db { get; }

        public LibraryReconciliationPlanService Service { get; }

        /// <summary>Creates an isolated database and mount table for a single reconciliation service test.</summary>
        public static async Task<Scope> CreateAsync(bool readOnlyMount = false)
        {
            var basePath = Path.Combine(Path.GetTempPath(), $"jularr-reconciliation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(basePath);
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(basePath, "test.db")};Foreign Keys=True")
                .Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var browser = new FolderBrowseService(
                new FixedMountTable([new MountPoint(basePath, "ext4", "/dev/test", ReadOnly: readOnlyMount)]),
                new DirectoryAccess(),
                new FolderBrowseOptions(Path.Combine(basePath, "data")));
            return new Scope(basePath, db, new LibraryReconciliationPlanService(db, browser));
        }

        /// <summary>Adds a configurable test library root, optionally without its backing directory.</summary>
        public LibraryRoot AddRoot(string name, bool createDirectory = true, bool enabled = true)
        {
            var path = Path.Combine(BasePath, name);
            if (createDirectory)
            {
                Directory.CreateDirectory(path);
            }

            var root = new LibraryRoot { Name = name, Path = path, IsEnabled = enabled };
            Db.LibraryRoots.Add(root);
            return root;
        }

        /// <summary>Releases the test database and removes only this test's temporary root.</summary>
        public void Dispose()
        {
            Db.Dispose();
            try
            {
                Directory.Delete(BasePath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Provides a deterministic mount list so test paths are constrained like production storage.</summary>
    private sealed class FixedMountTable(IReadOnlyList<MountPoint> mounts) : IMountTable
    {
        /// <summary>Returns the controlled test mount set used by the safe path browser.</summary>
        public IReadOnlyList<MountPoint> GetMounts() => mounts;
    }
}
