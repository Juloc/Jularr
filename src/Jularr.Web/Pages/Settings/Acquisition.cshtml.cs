using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.AniListAutoMonitor;
using Jularr.Web.Features.Acquisition.Backup;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Policy;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Media.Optimization;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Storage.FolderBrowse;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Settings;

/// <summary>
/// The one settings page for the P1 import/policy backlog: import mode (global and per library
/// root), post-import playback optimization, remote path mappings, tags, delay profiles, tag-scoped indexer restrictions, the current
/// profile's AniList list auto-monitor rule, and acquisition settings backup/restore.
/// </summary>
[Authorize(Policy = JularrPolicies.AcquisitionSettings)]
public sealed class AcquisitionModel(
    AnimeImportSettingsStore importSettings,
    AcquisitionPolicyStore policyStore,
    AniListAutoMonitorSettingsStore aniListAutoMonitorStore,
    AcquisitionBackupService backupService,
    IndexerStore indexerStore,
    CurrentAccountContext currentAccount,
    MediaInboxImportService inboxes,
    FolderBrowseService folders,
    LibraryRootRoutingService routing,
    AppDbContext db,
    ILogger<AcquisitionModel> logger,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public AnimeImportSettingsState ImportSettings { get; private set; } = AnimeImportSettingsState.Empty();
    public AcquisitionPolicyState Policy { get; private set; } = AcquisitionPolicyState.Empty();
    public IReadOnlyList<LibraryRoot> Roots { get; private set; } = [];

    /// <summary>The Storage-owned default destination of each importer-routed media type; a missing entry means imports of that type wait.</summary>
    public IReadOnlyDictionary<MediaAcquisitionKind, LibraryRootRoute> Destinations { get; private set; } = new Dictionary<MediaAcquisitionKind, LibraryRootRoute>();

    /// <summary>Roots that serve Movie or TV: their placement policy belongs to Storage. Anime roots keep their import mode override here.</summary>
    public IReadOnlySet<Guid> RoutedRootIds { get; private set; } = new HashSet<Guid>();
    public IReadOnlyList<IndexerEntry> IndexerEntries { get; private set; } = [];
    public bool AniListAutoMonitorEnabled { get; private set; }
    public InstanceModuleSettings InstanceModules { get; private set; } = InstanceModuleSettings.Default;
    public bool ShowAnimeAutoMonitor => InstanceModules.IsEnabled(InstanceModule.Anime);
    public AcquisitionBackupPreview? RestorePreview { get; private set; }
    public string? PendingRestoreJson { get; private set; }
    public string? Error => TempData["AcquisitionSettingsError"] as string;

    /// <summary>
    /// Library and inbox folders, browsing the container's file system and the settings backup are
    /// storage settings, Owner only.
    /// </summary>
    public bool CanManageStorage => currentAccount.Can(JularrPolicies.AdminSystem);

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public string ImportModeLabel(ImportMode mode) => mode switch
    {
        ImportMode.Move => Ui["settings.acquisition.importMode.move"],
        ImportMode.Copy => Ui["settings.acquisition.importMode.copy"],
        ImportMode.Hardlink => Ui["settings.acquisition.importMode.hardlink"],
        ImportMode.HardlinkOrCopy => Ui["settings.acquisition.importMode.hardlinkOrCopy"],
        _ => mode.ToString()
    };

    public async Task<IActionResult> OnPostImportModeAsync(
        ImportMode defaultImportMode,
        Guid? rootId,
        ImportMode? rootImportMode,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        await importSettings.UpdateAsync(
            state =>
            {
                var roots = new Dictionary<Guid, ImportMode>(state.RootImportModes);
                if (rootId is { } id)
                {
                    if (rootImportMode is { } mode)
                    {
                        roots[id] = mode;
                    }
                    else
                    {
                        roots.Remove(id);
                    }
                }

                return state with { DefaultImportMode = defaultImportMode, RootImportModes = roots };
            },
            cancellationToken);
        TempData["Status"] = Ui["settings.acquisition.status.importModeSaved"];
        return RedirectToPage();
    }

    /// <summary>The reading media types with their own folders, in display order.</summary>
    public IReadOnlyList<MediaAcquisitionKind> MediaFolderKinds =>
        MediaInboxImportService.InboxKinds.Where(IsKindEnabled).ToArray();

    public MediaLibraryTarget FoldersFor(MediaAcquisitionKind kind) => ImportSettings.FoldersFor(kind);

    /// <summary>
    /// Every reading media type can retain its original files in a NAS library folder. The
    /// database holds derived reader state; it is not the sole canonical media copy.
    /// </summary>
    public static bool HasLibraryFolder(MediaAcquisitionKind kind) =>
        MediaFolderKindsStatic.Contains(kind);

    /// <summary>Movies and TV place imports into the default LibraryRoot chosen in Admin → Storage instead of a folder set here.</summary>
    public static bool IsRouted(MediaAcquisitionKind kind) =>
        MediaInboxImportService.RoutedContentType(kind) is not null;

    public string ImportPolicyLabel(LibraryPlacementPolicy policy) => ImportModeLabel(ImportFileTransfer.ModeFor(policy));

    private static readonly MediaAcquisitionKind[] MediaFolderKindsStatic =
        [MediaAcquisitionKind.Manga, MediaAcquisitionKind.LightNovel, MediaAcquisitionKind.Book];

    /// <summary>The conventional folder name of a media type in the NAS layout (placeholders only).</summary>
    public static string FolderName(MediaAcquisitionKind kind) => kind switch
    {
        MediaAcquisitionKind.LightNovel => "lightnovels",
        MediaAcquisitionKind.Book => "books",
        _ => kind.ToString().ToLowerInvariant()
    };

    public string MediaLabel(MediaAcquisitionKind kind) => Ui[MediaKindLabelKeys.Folders(kind)];

    /// <summary>
    /// Sets the folders of one reading media type: its durable library folder, import mode and
    /// inbox folder. An empty field clears that folder.
    /// </summary>
    public async Task<IActionResult> OnPostMediaFoldersAsync(
        MediaAcquisitionKind kind,
        string? libraryRoot,
        ImportMode? importMode,
        string? inboxRoot,
        CancellationToken cancellationToken)
    {
        await LoadInstanceModulesAsync(cancellationToken);

        if (!CanManageStorage)
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!MediaFolderKinds.Contains(kind) || importMode is { } mode && !Enum.IsDefined(mode))
        {
            return BadRequest();
        }

        var library = HasLibraryFolder(kind) ? Clean(libraryRoot) : null;
        var inbox = Clean(inboxRoot);
        if (!IsAbsolute(library) || !IsAbsolute(inbox))
        {
            TempData["AcquisitionSettingsError"] = Ui["settings.acquisition.validation.libraryRootAbsolute"];
            return RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: "media-folders");
        }

        // An inbox that is, contains or sits inside any library root would import library files onto themselves.
        if (inbox is not null &&
            MediaInboxImportService.RoutedContentType(kind) is not null &&
            await routing.FindOverlappingRootAsync(inbox, cancellationToken) is not null)
        {
            TempData["AcquisitionSettingsError"] = Ui["settings.acquisition.validation.inboxOverlapsDestination"];
            return RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: "media-folders");
        }

        // Importing the inbox into the very folder it is scanned from would import files onto
        // themselves; nested folders are only warned about next to the fields.
        if (library is not null &&
            inbox is not null &&
            await folders.ComparePairAsync(library, inbox, cancellationToken) == FolderRelation.Same)
        {
            TempData["AcquisitionSettingsError"] = Ui["storage.pair.same"];
            return RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: "media-folders");
        }

        await importSettings.UpdateAsync(
            state =>
            {
                var libraries = new Dictionary<MediaAcquisitionKind, MediaLibraryTarget>(state.MediaLibraries ?? []);
                // The folders form does not touch the media type's remote path mappings.
                var target = (libraries.TryGetValue(kind, out var existing) ? existing : new MediaLibraryTarget()) with
                {
                    LibraryRoot = library,
                    ImportMode = HasLibraryFolder(kind) && library is not null ? importMode : null,
                    InboxRoot = inbox
                };
                if (target.IsEmpty)
                {
                    libraries.Remove(kind);
                }
                else
                {
                    libraries[kind] = target;
                }

                return state with { MediaLibraries = libraries };
            },
            cancellationToken);
        TempData["Status"] = Ui.Format("settings.acquisition.status.mediaFoldersSaved", ("media", MediaLabel(kind)));
        return RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: "media-folders");

        static string? Clean(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        static bool IsAbsolute(string? path) =>
            path is null || path.StartsWith('/') || Path.IsPathFullyQualified(path);
    }

    /// <summary>Imports what is in one media type's inbox folder now, with that media type's importer.</summary>
    public async Task<IActionResult> OnPostScanInboxAsync(
        MediaAcquisitionKind kind,
        CancellationToken cancellationToken)
    {
        await LoadInstanceModulesAsync(cancellationToken);

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!MediaFolderKinds.Contains(kind))
        {
            return BadRequest();
        }

        try
        {
            var result = await inboxes.RunAsync(kind, currentAccount.ProfileId, cancellationToken);
            TempData["Status"] = Ui.Format(
                "settings.acquisition.status.inboxScanned",
                ("media", MediaLabel(kind)),
                ("result", result.Message));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            IOException or
            UnauthorizedAccessException)
        {
            logger.LogError(exception, "Scanning the {Kind} inbox failed", kind);
            TempData["AcquisitionSettingsError"] = Ui["settings.acquisition.error.inboxScanFailed"];
        }

        return RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: "media-folders");
    }

    public string PlaybackOptimizationLabel(LosslessPlaybackOptimizationMode mode) => mode switch
    {
        LosslessPlaybackOptimizationMode.Off => Ui["settings.acquisition.playbackOptimization.off"],
        LosslessPlaybackOptimizationMode.SafeOnly => Ui["settings.acquisition.playbackOptimization.safeOnly"],
        _ => mode.ToString()
    };

    public async Task<IActionResult> OnPostPlaybackOptimizationAsync(
        LosslessPlaybackOptimizationMode playbackOptimization,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!Enum.IsDefined(playbackOptimization))
        {
            return BadRequest();
        }

        await importSettings.UpdateAsync(
            state => state with { PlaybackOptimization = playbackOptimization },
            cancellationToken);
        TempData["Status"] = Ui["settings.acquisition.status.playbackOptimizationSaved"];
        return RedirectToPage();
    }

    /// <summary>Every media type has its own remote path mappings.</summary>
    public IReadOnlyList<MediaAcquisitionKind> PathMappingKinds =>
        Enum.GetValues<MediaAcquisitionKind>().Where(IsKindEnabled).ToArray();

    public string KindLabel(MediaAcquisitionKind kind) =>
        Ui[$"admin.requests.kind.{AcquisitionAccessNames.Kind(kind)}"];

    public async Task<IActionResult> OnPostAddPathMappingAsync(
        MediaAcquisitionKind kind,
        string remotePrefix,
        string localPrefix,
        CancellationToken cancellationToken)
    {
        await LoadInstanceModulesAsync(cancellationToken);

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!PathMappingKinds.Contains(kind))
        {
            return BadRequest();
        }

        if (string.IsNullOrWhiteSpace(remotePrefix) || string.IsNullOrWhiteSpace(localPrefix))
        {
            TempData["AcquisitionSettingsError"] = Ui["settings.acquisition.validation.pathMappingRequired"];
            return RedirectToPage();
        }

        await importSettings.UpdateAsync(
            state => state.WithRemotePathMapping(kind, new RemotePathMapping(remotePrefix, localPrefix)),
            cancellationToken);
        TempData["Status"] = Ui["settings.acquisition.status.pathMappingSaved"];
        return RedirectToPage();
    }

    /// <summary>
    /// Shows what the importer would do with a path an external system reports: the mapped path and,
    /// for the owner, whether it exists inside the container. The mapping is the canonical
    /// <see cref="AnimeImportSettingsState.TranslatePath"/>; a mapping still being typed in the add
    /// form is applied exactly as adding it would apply it.
    /// </summary>
    public async Task<IActionResult> OnGetPreviewPathMappingAsync(
        MediaAcquisitionKind kind,
        string? samplePath,
        string? remotePrefix,
        string? localPrefix,
        CancellationToken cancellationToken)
    {
        await LoadInstanceModulesAsync(cancellationToken);

        if (!ModelState.IsValid || !PathMappingKinds.Contains(kind) || string.IsNullOrWhiteSpace(samplePath))
        {
            return BadRequest();
        }

        var state = await importSettings.LoadAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(remotePrefix) && !string.IsNullOrWhiteSpace(localPrefix))
        {
            state = state.WithRemotePathMapping(kind, new RemotePathMapping(remotePrefix, localPrefix));
        }

        var reported = samplePath.Trim();
        var mapped = state.TranslatePath(kind, reported);
        // Whether a path exists is a look at the container's file system: Owner only.
        var check = CanManageStorage
            ? await folders.CheckAsync(mapped, directoryOnly: false, otherPath: null, cancellationToken)
            : null;
        return new JsonResult(
            new PathMappingPreview(reported, mapped, !string.Equals(reported, mapped, StringComparison.Ordinal), check),
            FolderBrowseJson.Options);
    }

    public async Task<IActionResult> OnPostRemovePathMappingAsync(
        MediaAcquisitionKind kind,
        string remotePrefix,
        CancellationToken cancellationToken)
    {
        await LoadInstanceModulesAsync(cancellationToken);

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!PathMappingKinds.Contains(kind))
        {
            return BadRequest();
        }

        await importSettings.UpdateAsync(
            state => state.WithRemotePathMappings(
                kind,
                state.RemotePathMappingsFor(kind)
                    .Where(mapping => !mapping.RemotePrefix.Equals(remotePrefix, StringComparison.OrdinalIgnoreCase))),
            cancellationToken);
        TempData["Status"] = Ui["settings.acquisition.status.pathMappingRemoved"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddTagAsync(string name, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AcquisitionSettingsError"] = Ui["settings.acquisition.validation.tagNameRequired"];
            return RedirectToPage();
        }

        await policyStore.UpdateAsync(
            state =>
            {
                var id = name.Trim().ToLowerInvariant().Replace(' ', '-');
                if (state.Tags.Any(tag => tag.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                {
                    return state;
                }

                var tags = state.Tags.Append(new AcquisitionTag(id, name.Trim())).ToList();
                return state with { Tags = tags };
            },
            cancellationToken);
        TempData["Status"] = Ui["settings.acquisition.status.tagAdded"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveTagAsync(string tagId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        await policyStore.UpdateAsync(
            state => state with
            {
                Tags = state.Tags.Where(tag => !tag.Id.Equals(tagId, StringComparison.OrdinalIgnoreCase)).ToList(),
                DelayProfiles = state.DelayProfiles
                    .Select(profile => profile with { TagIds = profile.TagIds.Where(id => !id.Equals(tagId, StringComparison.OrdinalIgnoreCase)).ToArray() })
                    .ToList(),
                IndexerRestrictions = state.IndexerRestrictions
                    .Select(restriction => restriction with { TagIds = restriction.TagIds.Where(id => !id.Equals(tagId, StringComparison.OrdinalIgnoreCase)).ToArray() })
                    .ToList()
            },
            cancellationToken);
        TempData["Status"] = Ui["settings.acquisition.status.tagRemoved"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddDelayProfileAsync(
        string name,
        int delayMinutes,
        string? qualityProfileId,
        string[]? tagIds,
        bool isDefault,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (string.IsNullOrWhiteSpace(name) || delayMinutes < 0)
        {
            TempData["AcquisitionSettingsError"] = Ui["settings.acquisition.validation.delayProfileRequired"];
            return RedirectToPage();
        }

        await policyStore.UpdateAsync(
            state =>
            {
                var profiles = state.DelayProfiles.Append(new AnimeDelayProfile(
                    Guid.NewGuid().ToString("N"),
                    name.Trim(),
                    delayMinutes,
                    string.IsNullOrWhiteSpace(qualityProfileId) ? null : qualityProfileId.Trim(),
                    tagIds ?? [],
                    isDefault)).ToList();
                return state with { DelayProfiles = profiles };
            },
            cancellationToken);
        TempData["Status"] = Ui["settings.acquisition.status.delayProfileAdded"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveDelayProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        await policyStore.UpdateAsync(
            state => state with { DelayProfiles = state.DelayProfiles.Where(profile => profile.Id != profileId).ToList() },
            cancellationToken);
        TempData["Status"] = Ui["settings.acquisition.status.delayProfileRemoved"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddIndexerRestrictionAsync(
        string name,
        string[]? tagIds,
        Guid[]? allowedIndexerEntryIds,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (string.IsNullOrWhiteSpace(name) || tagIds is not { Length: > 0 })
        {
            TempData["AcquisitionSettingsError"] = Ui["settings.acquisition.validation.indexerRestrictionNameAndTag"];
            return RedirectToPage();
        }

        var ids = (allowedIndexerEntryIds ?? [])
            .Distinct()
            .Order()
            .ToArray();
        if (ids.Length == 0)
        {
            TempData["AcquisitionSettingsError"] = Ui["settings.acquisition.validation.indexerRestrictionIndexer"];
            return RedirectToPage();
        }

        await policyStore.UpdateAsync(
            state =>
            {
                var restrictions = state.IndexerRestrictions.Append(
                    new AnimeIndexerRestriction(Guid.NewGuid().ToString("N"), name.Trim(), tagIds, ids)).ToList();
                return state with { IndexerRestrictions = restrictions };
            },
            cancellationToken);
        TempData["Status"] = Ui["settings.acquisition.status.indexerRestrictionAdded"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveIndexerRestrictionAsync(string restrictionId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        await policyStore.UpdateAsync(
            state => state with { IndexerRestrictions = state.IndexerRestrictions.Where(item => item.Id != restrictionId).ToList() },
            cancellationToken);
        TempData["Status"] = Ui["settings.acquisition.status.indexerRestrictionRemoved"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAniListAutoMonitorAsync(bool enabled, CancellationToken cancellationToken)
    {
        await LoadInstanceModulesAsync(cancellationToken);
        if (!ShowAnimeAutoMonitor)
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        await aniListAutoMonitorStore.SetEnabledAsync(currentAccount.ProfileId, enabled, DateTimeOffset.UtcNow, cancellationToken);
        TempData["Status"] = enabled
            ? Ui["settings.acquisition.status.autoMonitorOn"]
            : Ui["settings.acquisition.status.autoMonitorOff"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostExportBackupAsync(CancellationToken cancellationToken)
    {
        if (!CanManageStorage)
        {
            return Forbid();
        }

        var bundle = await backupService.ExportAsync(cancellationToken);
        var json = JsonSerializer.Serialize(bundle, JsonOptions);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        return File(bytes, "application/json", $"jularr-acquisition-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
    }

    public async Task<IActionResult> OnPostPreviewRestoreAsync(IFormFile backupFile, CancellationToken cancellationToken)
    {
        if (!CanManageStorage)
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadAsync(cancellationToken);
        if (backupFile is null || backupFile.Length == 0)
        {
            TempData["AcquisitionSettingsError"] = Ui["settings.acquisition.validation.chooseBackupFile"];
            return RedirectToPage();
        }

        using var reader = new StreamReader(backupFile.OpenReadStream());
        var json = await reader.ReadToEndAsync(cancellationToken);
        try
        {
            var bundle = JsonSerializer.Deserialize<AcquisitionBackupBundle>(json, JsonOptions);
            if (bundle is null)
            {
                TempData["AcquisitionSettingsError"] = Ui["settings.acquisition.validation.backupEmpty"];
                return RedirectToPage();
            }

            RestorePreview = await backupService.PreviewRestoreAsync(bundle, cancellationToken);
            PendingRestoreJson = json;
        }
        catch (JsonException)
        {
            TempData["AcquisitionSettingsError"] = Ui["settings.acquisition.validation.backupInvalidJson"];
            return RedirectToPage();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostApplyRestoreAsync(string pendingRestoreJson, CancellationToken cancellationToken)
    {
        if (!CanManageStorage)
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        try
        {
            var bundle = JsonSerializer.Deserialize<AcquisitionBackupBundle>(pendingRestoreJson, JsonOptions);
            if (bundle is null)
            {
                TempData["AcquisitionSettingsError"] = Ui["settings.acquisition.validation.backupUnreadable"];
                return RedirectToPage();
            }

            var result = await backupService.RestoreAsync(bundle, cancellationToken);
            TempData[result.Success ? "Status" : "AcquisitionSettingsError"] = result.Success
                ? Ui.Format("settings.acquisition.status.restored", ("count", result.FilesWritten))
                : string.Join(" ", result.Errors);
        }
        catch (JsonException)
        {
            TempData["AcquisitionSettingsError"] = Ui["settings.acquisition.validation.backupUnreadable"];
        }

        return RedirectToPage();
    }

    private bool IsKindEnabled(MediaAcquisitionKind kind) =>
        InstanceModules.IsEnabled(AcquisitionInstanceModules.For(kind));

    private async Task LoadInstanceModulesAsync(CancellationToken cancellationToken)
    {
        InstanceModules = instanceModules is null
            ? InstanceModuleSettings.Default
            : await instanceModules.GetAsync(cancellationToken);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadInstanceModulesAsync(cancellationToken);
        ImportSettings = await importSettings.LoadAsync(cancellationToken);
        Policy = await policyStore.LoadAsync(cancellationToken);
        Roots = await db.LibraryRoots.AsNoTracking().OrderBy(root => root.Name).ToArrayAsync(cancellationToken);
        RoutedRootIds = (await db.LibraryRootContentAssignments.AsNoTracking().Where(assignment => assignment.ContentType != LibraryContentType.Anime).Select(assignment => assignment.LibraryRootId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var destinations = new Dictionary<MediaAcquisitionKind, LibraryRootRoute>();
        foreach (var kind in MediaInboxImportService.InboxKinds)
        {
            if (MediaInboxImportService.RoutedContentType(kind) is { } contentType && await routing.ResolveDefaultAsync(contentType, cancellationToken) is { } route)
            {
                destinations[kind] = route;
            }
        }

        Destinations = destinations;
        IndexerEntries = (await indexerStore.LoadAllAsync(cancellationToken)).OrderBy(entry => entry.Priority).ToArray();
        var autoMonitor = await aniListAutoMonitorStore.LoadAsync(cancellationToken);
        AniListAutoMonitorEnabled = autoMonitor.IsEnabled(currentAccount.ProfileId);
    }
}

/// <summary>
/// The result of testing a path against the remote path mappings: the path as an external system
/// reports it, the path Jularr reads, and (for the owner) what exists there.
/// </summary>
public sealed record PathMappingPreview(string Reported, string Mapped, bool Changed, PathCheck? Check);
