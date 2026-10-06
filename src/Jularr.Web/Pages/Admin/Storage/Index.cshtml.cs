using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Storage.Insights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Admin.Storage;

/// <summary>One content type whose importer resolves its destination here: the supporting roots and the one default.</summary>
public sealed record StorageDestination(LibraryContentType ContentType, IReadOnlyList<LibraryRootRoute> Routes)
{
    public LibraryRootRoute? Default => Routes.FirstOrDefault(route => route.IsDefault && route.IsEnabled);
}

/// <summary>
/// Storage insights (#414): where disk space goes and what Jularr can safely clean up. Usage comes
/// from the library inventory and cached storage state, so opening this page never wakes a
/// sleeping NAS; the cleanup only removes rebuildable Jularr cache leftovers, never library media.
/// It also owns the default destination LibraryRoot and placement policy of the content types whose importers route through
/// Storage (#815).
/// </summary>
[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class IndexModel(
    AppDbContext db,
    StorageUsageService usageService,
    StorageCleanupService cleanupService,
    LibraryRootRoutingService routing,
    CurrentAccountContext currentAccount,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    // Entries listed per cache area in the cleanup preview; the totals always cover all of them.
    public const int PreviewEntriesPerArea = 8;

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public StorageUsageReport Usage { get; private set; } = new([], [], []);

    public StorageCacheReport Cache { get; private set; } = new([], StorageCleanupPlan.Empty);

    public IReadOnlyList<StorageDestination> Destinations { get; private set; } = [];

    public IReadOnlyList<LibraryRoot> EnabledRoots { get; private set; } = [];

    /// <summary>Changing where imports are placed is a storage setting: Owner only.</summary>
    public bool CanManageDestinations => currentAccount.Can(JularrPolicies.AdminSystem);

    public string? Error => TempData["StorageError"] as string;

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        await LoadAsync(cancellationToken);

    /// <summary>Makes one root the default destination of a content type (or clears the default) and sets how it places imports.</summary>
    public async Task<IActionResult> OnPostDestinationAsync(
        LibraryContentType contentType,
        Guid? libraryRootId,
        LibraryPlacementPolicy placementPolicy,
        CancellationToken cancellationToken)
    {
        if (!CanManageDestinations)
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!LibraryRootRoutingService.ManagedTypes.Contains(contentType) || !Enum.IsDefined(placementPolicy))
        {
            return BadRequest();
        }

        try
        {
            if (libraryRootId is { } rootId)
            {
                await routing.AssignDefaultAsync(contentType, rootId, placementPolicy, cancellationToken);
            }
            else
            {
                await routing.SetDefaultAsync(contentType, null, cancellationToken);
            }

            TempData["Status"] = Ui["admin.storage.destinations.saved"];
        }
        catch (LibraryRootConflictException)
        {
            TempData["StorageError"] = Ui["admin.storage.destinations.conflict"];
        }
        catch (InvalidOperationException)
        {
            TempData["StorageError"] = Ui["admin.storage.destinations.failed"];
        }

        return RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: "destinations");
    }

    public string ContentTypeLabel(LibraryContentType contentType) => Ui[MediaKindLabelKeys.Name(LibraryRootRoutingService.KindOf(contentType)!.Value)];

    /// <summary>Whether an importer without a default root waits (Movie, TV, Music) or reads the folder of Import &amp; naming (reading and audiobook types).</summary>
    public static bool WaitsForDefault(LibraryContentType contentType) => LibraryRootRoutingService.ImporterRoutedTypes.Contains(contentType);

    public string PlacementLabel(LibraryPlacementPolicy policy) =>
        ImportFileTransfer.ModeFor(policy) switch
        {
            ImportMode.Move => Ui["settings.acquisition.importMode.move"],
            ImportMode.Copy => Ui["settings.acquisition.importMode.copy"],
            ImportMode.Hardlink => Ui["settings.acquisition.importMode.hardlink"],
            _ => Ui["settings.acquisition.importMode.hardlinkOrCopy"]
        };

    public async Task<IActionResult> OnPostCleanAsync(
        List<StorageCacheAreaKind> areas,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var result = await cleanupService.CleanAsync(areas, cancellationToken);

        TempData["Status"] = result.Removed == 0
            ? Ui["admin.storage.cleanup.nothing"]
            : Ui.Format(
                "admin.storage.cleanup.done",
                ("files", result.Removed.ToString("N0")),
                ("size", StorageHealth.FormatBytes(result.BytesFreed)));
        return RedirectToPage();
    }

    public string HealthLabel(StorageHealthState? health) =>
        health switch
        {
            StorageHealthState.Online => Ui["admin.system.health.online"],
            StorageHealthState.Starting => Ui["admin.system.health.starting"],
            StorageHealthState.OfflineExpected => Ui["admin.system.health.sleeping"],
            StorageHealthState.OfflineUnexpected => Ui["admin.system.health.unavailable"],
            StorageHealthState.Error => Ui["admin.system.health.error"],
            _ => Ui["admin.storage.roots.notChecked"]
        };

    public static string HealthCss(StorageHealthState? health) =>
        health switch
        {
            StorageHealthState.Online => "status-ok",
            StorageHealthState.Starting or StorageHealthState.OfflineExpected => "status-warning",
            StorageHealthState.OfflineUnexpected or StorageHealthState.Error => "status-error",
            _ => ""
        };

    public string MediaKindLabel(StorageMediaKind kind) =>
        Ui[$"admin.storage.media.{kind.ToString().ToLowerInvariant()}"];

    public string AreaLabel(StorageCacheAreaKind area) =>
        Ui[$"admin.storage.cache.area.{area.ToString().ToLowerInvariant()}"];

    public string ReasonLabel(ReclaimReason reason) =>
        Ui[$"admin.storage.cache.reason.{reason.ToString().ToLowerInvariant()}"];

    public static string EntryName(ReclaimCandidate candidate) =>
        Path.GetFileName(candidate.Path);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Usage = await usageService.GetAsync(StorageUsageService.DefaultLargestItems, cancellationToken);
        Cache = await cleanupService.PreviewAsync(cancellationToken);
        EnabledRoots = await db.LibraryRoots.AsNoTracking().Where(root => root.IsEnabled).OrderBy(root => root.Name).ToArrayAsync(cancellationToken);

        // A content type whose module is disabled has no importer to configure, so it disappears instead of showing a dead control.
        var modules = instanceModules is null ? InstanceModuleSettings.Default : await instanceModules.GetAsync(cancellationToken);
        var destinations = new List<StorageDestination>();
        foreach (var contentType in LibraryRootRoutingService.ManagedTypes)
        {
            if (modules.IsEnabled(InstanceModule.Acquisition) && modules.IsEnabled(AcquisitionInstanceModules.For(LibraryRootRoutingService.KindOf(contentType)!.Value)))
            {
                destinations.Add(new StorageDestination(contentType, await routing.ListAsync(contentType, cancellationToken)));
            }
        }

        Destinations = destinations;
    }
}
