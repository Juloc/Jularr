using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Admin;

public sealed record AdminLibraryRootRow(
    Guid Id,
    string Name,
    string Path,
    DateTime? LastScannedAt,
    bool WakeOnLanEnabled,
    string? WakeMacAddress,
    string? WakeBroadcastAddress,
    LibraryRootAvailabilitySnapshot Availability,
    int ReconciliationIntervalMinutes,
    AdminLibraryScanRow? ActiveScan,
    AdminLibraryScanRow? LastScan,
    StorageIntegritySummary Integrity)
{
    public string AvailabilityLabel(UiTextBundle ui) =>
        Availability.Health switch
        {
            StorageHealthState.Online => ui["admin.system.health.online"],
            StorageHealthState.Starting => ui["admin.system.health.starting"],
            StorageHealthState.OfflineExpected => ui["admin.system.health.sleeping"],
            StorageHealthState.OfflineUnexpected => ui["admin.system.health.unavailable"],
            _ => ui["admin.system.health.error"]
        };

    // One short fact next to the state: free space, why it is offline, or what failed.
    public string? AvailabilityDetail(UiTextBundle ui) =>
        Availability.Health switch
        {
            StorageHealthState.Online => Availability.FreeSpaceBytes is { } free
                ? ui.Format("admin.system.health.freeSpace", ("size", StorageHealth.FormatBytes(free)))
                : null,
            StorageHealthState.OfflineExpected => ui["admin.system.health.wakeConfigured"],
            StorageHealthState.OfflineUnexpected => ui["admin.system.health.wakeNotConfigured"],
            StorageHealthState.Error => Availability.DiagnosticCode switch
            {
                StorageDiagnosticCodes.WakeTimeout => ui["admin.system.health.wakeTimeout"],
                StorageDiagnosticCodes.WakeSendFailed => ui["admin.system.health.wakeSendFailed"],
                var code => ui.Format(
                    "admin.system.storage.unreachable",
                    ("code", code ?? ui["admin.system.storage.unreachableDefaultCode"]))
            },
            _ => null
        };

    public string AvailabilityCss =>
        Availability.Health switch
        {
            StorageHealthState.Online => "status-ok",
            StorageHealthState.Starting or StorageHealthState.OfflineExpected => "status-warning",
            _ => "status-error"
        };

    public bool CanWake =>
        WakeOnLanEnabled &&
        Availability.WakeConfigured &&
        Availability.Health is not StorageHealthState.Online and not StorageHealthState.Starting;
}

[Authorize(Policy = JularrPolicies.AdminSystem)]
public sealed class SystemModel(
    AppDbContext db,
    LibraryScanCoordinator scans,
    CurrentAccountContext account,
    LibraryRootAvailabilityService availability,
    WakeOnLanService wakeOnLan,
    StorageIntegrityService integrity,
    IndexerStore indexerStore,
    DownloadClientStore downloadClientStore,
    AcquisitionHealthStore acquisitionHealth,
    LibraryRootRoutingService routing) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public IReadOnlyList<AdminLibraryRootRow> Roots { get; private set; } = [];
    public int TotalIndexers { get; private set; }
    public int HealthyIndexers { get; private set; }
    public int TotalDownloadClients { get; private set; }
    public int HealthyDownloadClients { get; private set; }

    [BindProperty]
    public string Name { get; set; } = "Anime";

    [BindProperty]
    public string Path { get; set; } = "/media/anime";

    /// <summary>What the new root is for. The Anime scanner reads only roots that serve Anime; a Movie or TV root is routed through Storage.</summary>
    [BindProperty]
    public LibraryContentType ContentType { get; set; } = LibraryContentType.Anime;

    public static readonly LibraryContentType[] RootContentTypes = [LibraryContentType.Anime, LibraryContentType.Movie, LibraryContentType.Tv];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAddAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Path))
        {
            ModelState.AddModelError(string.Empty, Ui["admin.system.error.nameAndPathRequired"]);
            await LoadAsync(cancellationToken);
            return Page();
        }

        var fullPath = System.IO.Path.GetFullPath(Path.Trim());
        if (await db.LibraryRoots.AnyAsync(x => x.Path == fullPath, cancellationToken))
        {
            ModelState.AddModelError(string.Empty, Ui["admin.system.error.rootExists"]);
            await LoadAsync(cancellationToken);
            return Page();
        }

        if (!RootContentTypes.Contains(ContentType))
        {
            return BadRequest();
        }

        var root = new LibraryRoot
        {
            Name = Name.Trim(),
            Path = fullPath
        };
        db.LibraryRoots.Add(root);
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            await routing.SetSupportedAsync(root.Id, ContentType, true, cancellationToken);
        }
        catch (LibraryRootConflictException)
        {
            db.LibraryRoots.Remove(root);
            await db.SaveChangesAsync(cancellationToken);
            ModelState.AddModelError(string.Empty, Ui["admin.storage.destinations.conflict"]);
            await LoadAsync(cancellationToken);
            return Page();
        }

        TempData["Status"] = Ui["admin.system.rootAdded"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostConfigureWakeAsync(
        Guid rootId,
        bool wakeOnLanEnabled,
        string? wakeMacAddress,
        string? wakeBroadcastAddress,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        var root = await db.LibraryRoots
            .SingleOrDefaultAsync(x => x.Id == rootId, cancellationToken);
        if (root is null)
        {
            return NotFound();
        }

        string? normalizedMac = null;
        if (!string.IsNullOrWhiteSpace(wakeMacAddress) &&
            !WakeOnLanService.TryNormalizeMacAddress(
                wakeMacAddress,
                out normalizedMac))
        {
            ModelState.AddModelError(
                string.Empty,
                Ui["admin.system.error.invalidMac"]);
            await LoadAsync(cancellationToken);
            return Page();
        }

        if (wakeOnLanEnabled && normalizedMac is null)
        {
            ModelState.AddModelError(
                string.Empty,
                Ui["admin.system.error.macRequired"]);
            await LoadAsync(cancellationToken);
            return Page();
        }

        if (!WakeOnLanService.TryResolveBroadcastEndpoint(
                wakeBroadcastAddress,
                out _))
        {
            ModelState.AddModelError(
                string.Empty,
                Ui["admin.system.error.invalidBroadcast"]);
            await LoadAsync(cancellationToken);
            return Page();
        }

        root.WakeOnLanEnabled = wakeOnLanEnabled;
        root.WakeMacAddress = normalizedMac;
        root.WakeBroadcastAddress = string.IsNullOrWhiteSpace(wakeBroadcastAddress)
            ? null
            : wakeBroadcastAddress.Trim();

        await db.SaveChangesAsync(cancellationToken);
        TempData["Status"] = wakeOnLanEnabled
            ? Ui["admin.system.wakeOnLanSaved"]
            : Ui["admin.system.wakeOnLanDisabled"];

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestRootAsync(
        Guid rootId,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        var status = await availability.CheckAsync(
            rootId,
            force: true,
            cancellationToken);

        if (status is null)
        {
            return NotFound();
        }

        TempData["Status"] = status.State switch
        {
            StorageAvailabilityState.Available =>
                Ui["admin.system.storage.online"],
            StorageAvailabilityState.Starting =>
                Ui["admin.system.storage.starting"],
            StorageAvailabilityState.Offline =>
                Ui["admin.system.storage.offline"],
            StorageAvailabilityState.Unreachable =>
                Ui.Format("admin.system.storage.unreachable", ("code", status.DiagnosticCode ?? Ui["admin.system.storage.unreachableDefaultCode"])),
            _ => Ui["admin.system.storage.unknown"]
        };

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostWakeRootAsync(
        Guid rootId,
        CancellationToken cancellationToken)
    {
        var result = await wakeOnLan.WakeAsync(rootId, cancellationToken);
        if (result.Availability is null &&
            !await db.LibraryRoots.AnyAsync(x => x.Id == rootId, cancellationToken))
        {
            return NotFound();
        }

        TempData["Status"] = result.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostScanAsync(
        Guid rootId,
        CancellationToken cancellationToken)
    {
        var result = await scans.QueueAsync(
            new LibraryScanRequest(rootId, LibraryScanTrigger.Manual, ProfileId: account.ProfileId),
            cancellationToken);

        if (result.Outcome == LibraryScanQueueOutcome.RootNotFound)
        {
            return NotFound();
        }

        TempData["Status"] = result.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostConfigureReconciliationAsync(
        Guid rootId,
        int reconciliationIntervalMinutes,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        var root = await db.LibraryRoots
            .SingleOrDefaultAsync(x => x.Id == rootId, cancellationToken);
        if (root is null)
        {
            return NotFound();
        }

        if (reconciliationIntervalMinutes != 0 &&
            (reconciliationIntervalMinutes < LibraryRoot.MinimumReconciliationIntervalMinutes ||
             reconciliationIntervalMinutes > LibraryRoot.MaximumReconciliationIntervalMinutes))
        {
            ModelState.AddModelError(
                string.Empty,
                Ui.Format(
                    "admin.system.error.reconciliationRange",
                    ("min", LibraryRoot.MinimumReconciliationIntervalMinutes),
                    ("max", LibraryRoot.MaximumReconciliationIntervalMinutes)));
            await LoadAsync(cancellationToken);
            return Page();
        }

        root.ReconciliationIntervalMinutes = reconciliationIntervalMinutes;
        await db.SaveChangesAsync(cancellationToken);

        TempData["Status"] = reconciliationIntervalMinutes == 0
            ? Ui["admin.system.reconciliationDisabled"]
            : Ui.Format("admin.system.reconciliationSet", ("minutes", reconciliationIntervalMinutes));
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var roots = await db.LibraryRoots
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var rootNames = roots.ToDictionary(x => x.Id, x => x.Name);
        var scanRows = (await new OperationStore(db).ListAsync(
                new OperationListFilter(
                    Kind: LibraryScanCoordinator.OperationKind,
                    Limit: LibraryScanCoordinator.HistoryLimit),
                cancellationToken))
            .Select(operation => AdminLibraryScanRow.From(operation, rootNames))
            .Where(row => row.Details is not null)
            .ToArray();

        var integrityByRoot = await integrity.SummarizeAsync(cancellationToken);
        var rows = new List<AdminLibraryRootRow>(roots.Count);
        foreach (var root in roots)
        {
            var activeScan = scanRows.FirstOrDefault(row =>
                row.Details!.RootId == root.Id && row.Operation.IsActive);
            var lastScan = scanRows
                .Where(row => row.Details!.RootId == root.Id && !row.Operation.IsActive)
                .OrderByDescending(row => row.Operation.FinishedAtUtc ?? row.Operation.UpdatedAtUtc)
                .FirstOrDefault();

            var current = availability.GetCached(root)
                ?? await availability.CheckAsync(
                    root.Id,
                    force: false,
                    cancellationToken)
                ?? new LibraryRootAvailabilitySnapshot(
                    root.Id,
                    StorageAvailabilityState.Unknown,
                    DateTimeOffset.UtcNow,
                    null,
                    false);

            rows.Add(new AdminLibraryRootRow(
                root.Id,
                root.Name,
                root.Path,
                root.LastScannedAt,
                root.WakeOnLanEnabled,
                root.WakeMacAddress,
                root.WakeBroadcastAddress,
                current,
                root.ReconciliationIntervalMinutes,
                activeScan,
                lastScan,
                integrityByRoot.GetValueOrDefault(root.Id, StorageIntegritySummary.Clean)));
        }

        Roots = rows;

        var indexers = await indexerStore.LoadAllAsync(cancellationToken);
        TotalIndexers = indexers.Count;
        HealthyIndexers = 0;
        foreach (var indexer in indexers)
        {
            if (await acquisitionHealth.IsHealthyAsync(AcquisitionHealthKind.Indexer, indexer.Id, cancellationToken))
            {
                HealthyIndexers++;
            }
        }

        var downloadClients = await downloadClientStore.LoadAllAsync(cancellationToken);
        TotalDownloadClients = downloadClients.Count;
        HealthyDownloadClients = 0;
        foreach (var client in downloadClients)
        {
            if (await acquisitionHealth.IsHealthyAsync(AcquisitionHealthKind.DownloadClient, client.Id, cancellationToken))
            {
                HealthyDownloadClients++;
            }
        }
    }
}
