using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Library.V1;
using Jularr.Backend.Service.Admin.Storage.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Library
{
    public enum LibraryTab : byte
    {
        Unmatched = 1,
        Files = 2,
        Rename = 3,
        Naming = 4,
        Versions = 5,
        Overview = 6
    }

    public sealed class LibraryModel(ReadLibraryFilesV1 readFiles, AssignLibraryFileV1 assign, UnassignLibraryFileV1 unassign, ReadLibraryRenamesV1 readRenames,
        ApplyLibraryRenamesV1 applyRenames, ReadNamingPatternsV1 readPatterns, UpdateNamingPatternV1 updatePattern, UpdateNamingSubtypePatternV1 updateSubtypePattern,
        ReadNamingSettingsV1 readNamingSettings, UpdateNamingSettingsV1 updateNamingSettings, ReadReplacementSettingsV1 readReplacement,
        UpdateReplacementSettingsV1 updateReplacement, KeepOnlyLibraryFileV1 keepOnly, ProtectLibraryFileV1 protect, ReadStorageMountsV1 readMounts, ReadLibraryOverviewV1 readOverview,
        QueueLibraryScanV1 queueScan, FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private IReadOnlyList<LibraryOverviewV1>? _overview;
        private ReplacementSettingsV1? _replacement;
        private UpdateReplacementSettingsDataV1? _replacementInput;
        private PageResult<StorageMountDetailsV1>? _mounts;
        private const int RenamePageSize = 100;
        private PageResult<LibraryFileDetailsV1>? _files;
        private LibraryRenamePlanV1? _renames;
        private IReadOnlyList<NamingPatternV1>? _patterns;
        private UpdateNamingPatternDataV1? _patternInput;
        private (MediaType Type, MediaSubtype? Subtype)? _failedPattern;
        private IReadOnlyList<NamingSettingsV1>? _namingSettings;
        private UpdateNamingSettingsDataV1? _namingSettingsInput;
        private MediaType? _failedSettings;
        private long? _failedFileId;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        // The files of one library.
        [BindProperty(SupportsGet = true, Name = "root")]
        public long? RootId { get; set; }

        // Each view has its address: the overview at /Admin/Library, the others below it.
        private LibraryTab Tab => Request.Path.Value?.TrimEnd('/').Split('/')[^1] is { } last && Enum.TryParse<LibraryTab>(last, true, out var tab) && tab != LibraryTab.Overview
            ? tab : LibraryTab.Overview;

        protected override void Define(Page page)
        {
            page.Title("library.tab." + Tab);
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Library/Library.entry.scss");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            // Every library with what it holds; new ones are added with their storage.
            if (_overview is { } libraries)
            {
                if (libraries.Count > 0) page.Content.Add(Totals(libraries));
                var table = new Table<LibraryOverviewV1>(libraries)
                    .Text(item => item.Name, "library.root")
                    .Content("table.type", item => new Text(MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId)))
                    .Content("library.overview.place", item => new ValueText(item.StorageMountName + " / " + item.RelativePath))
                    .Content("table.titles", item => new ValueText(item.WorkCount.ToString("N0", CultureInfo.CurrentCulture)))
                    .Content("storage.paths.files", item => new A(new ValueText(item.FileCount.ToString("N0", CultureInfo.CurrentCulture)))
                        .Attribute("href", "/Admin/Library/Files?root=" + item.Id.ToString(CultureInfo.InvariantCulture)))
                    .Content("table.size", item => new ValueText(ByteSize.Format(item.TotalBytes)))
                    .Content("library.overview.attention", Attention)
                    .Content("library.overview.scanned", item => item.IsScanning ? new Text("library.overview.scanning")
                        : item.LastScanAt is { } scanned ? new ValueText(scanned.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)) : new Text("library.overview.never"));

                // Scanning reads the storage, which needs its administration.
                if (CanRename)
                {
                    table.Content("common.actions", item => new Form<LibraryRootParametersV1>(form => form.Submit("storage.paths.scan", item.Name, valueHidden: true),
                        Address(handler: "Scan") + "&libraryRootId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new LibraryRootParametersV1(item.Id)));
                }

                page.Content.Add(libraries.Count == 0 ? new P(new Text("library.overview.empty")) : table);
                if (CanRename) page.Content.Add(new Div(new A(new Text("library.overview.manage")).Class("button").Attribute("href", "/Admin/Storage")).Class("page-actions"));
            }

            // Inventory and manual identity.
            if (_files is { } files)
            {
                var table = new Table<LibraryFileDetailsV1>(files.Items)
                    .Text(item => item.RelativePath, "library.file")
                    .Text(item => item.LibraryRootName, "library.root")
                    .Content("table.size", item => new ValueText(ByteSize.Format(item.SizeBytes)))
                    .Content("table.status", item => new Text(item.MissingSince is null ? "library.present" : "library.missing"));
                table = Tab == LibraryTab.Unmatched
                    ? table.Content("library.assign", item => Assignment(item, csrf))
                    : table.Content("table.work", item => item.WorkId is { } workId
                        ? new Div(new A(new ValueText(item.WorkTitle ?? "")).Attribute("href", "/Admin/Works/" + workId.ToString(CultureInfo.InvariantCulture)), Release(item, csrf), Subtitles(item))
                        : new Text("library.unmatched"));
                page.Content.Add(files.Items.Count > 0 ? table : new P(new Text(Tab == LibraryTab.Unmatched ? "library.emptyUnmatched" : "library.empty")));
                Paging(page, files.Page, files.Items.Count == files.PageSize);
            }

            if (_renames is { } renames)
            {
                if (TempData["RenamedCount"] is int renamed && TempData["SkippedCount"] is int skipped)
                {
                    page.Content.Add(new P(new ValueText(string.Format(CultureInfo.InvariantCulture, texts["library.renamed"], renamed, skipped))).Attribute("role", "status"));
                }

                if (renames.Items.Count == 0)
                {
                    page.Content.Add(new P(new Text("library.renameEmpty")));
                }
                else
                {
                    page.Content.Add(new Table<LibraryRenameV1>(renames.Items)
                        .Text(item => item.LibraryRootName, "library.root")
                        .Text(item => item.RelativePath, "library.file")
                        .Text(item => item.TargetPath, "library.renameTarget"));

                    // Files move only for the confirmed plan.
                    page.Content.Add(new Form<ApplyLibraryRenamesDataV1>(form =>
                    {
                        form.Hidden(item => item.Fingerprint);
                        form.Submit("library.renameApply");
                    }, Address(handler: "Rename"), csrf, new ApplyLibraryRenamesDataV1(renames.Fingerprint)));
                }

                Paging(page, PageNumber, renames.Items.Count == RenamePageSize);
            }

            // Folders and names of movie and episode files for imports and renames, each with a sample.
            if (_patterns is { } patterns)
            {
                foreach (var pattern in patterns)
                {
                    var failed = _failedPattern == (pattern.MediaTypeId, pattern.MediaSubtypeId);
                    page.Content.Add(new UiSection("library.naming." + pattern.MediaTypeId + (pattern.MediaSubtypeId is { } subtype ? "." + subtype : ""), [
                        new Form<UpdateNamingPatternDataV1>(form =>
                        {
                            form.Hidden(item => item.Version);
                            form.Text(item => item.Folder, "library.naming.folder");
                            form.Text(item => item.File, "library.naming.file");
                            form.Save();
                        }, Address(handler: "Naming") + "&mediaTypeId=" + pattern.MediaTypeId + (pattern.MediaSubtypeId is { } kind ? "&mediaSubtypeId=" + kind : ""), csrf,
                            failed && _patternInput is { } input ? input : new UpdateNamingPatternDataV1(pattern.Folder, pattern.File, pattern.Version), failed ? FormError : null),
                        new P(new Text("library.naming.example"), new ValueText(" "), new Span(new ValueText(pattern.Example)).Class("library-example"))]));
                }

                // How values become names: illegal characters, colons and the folder of season 0.
                var colons = Enum.GetValues<ColonReplacement>().Select(mode => new SelectOption(((byte)mode).ToString(CultureInfo.InvariantCulture), texts["library.naming.colon." + mode]));
                foreach (var settings in _namingSettings ?? [])
                {
                    var failed = _failedSettings == settings.MediaTypeId;
                    page.Content.Add(new UiSection("library.naming.settings." + settings.MediaTypeId, [new Form<UpdateNamingSettingsDataV1>(form =>
                    {
                        form.Hidden(item => item.Version);
                        form.Toggle(item => item.ReplacesIllegalCharacters, "library.naming.replacesIllegal");
                        form.Select(item => item.ColonReplacementId, "library.naming.colon", colons);
                        form.Text(item => item.CustomColonReplacement, "library.naming.colonCustom");
                        if (settings.MediaTypeId == MediaType.Series) form.Text(item => item.SpecialsFolder, "library.naming.specials");
                        form.Save();
                    }, Address(handler: "NamingSettings") + "&mediaTypeId=" + settings.MediaTypeId, csrf, failed && _namingSettingsInput is { } entered ? entered
                        : new UpdateNamingSettingsDataV1(settings.ReplacesIllegalCharacters, settings.ColonReplacementId, settings.CustomColonReplacement, settings.SpecialsFolder, settings.Version),
                        failed ? FormError : null)]));
                }

                page.Content.Add(new UiSection("library.naming.placeholders", [new Dl([.. NamingTokens.All.SelectMany(token => new IUiComponent[]
                    {
                        new Dt(new ValueText(NamingTokens.Display(token))).Class("library-example"),
                        new Dd(new Text("library.naming.token." + token.Replace(" ", "", StringComparison.Ordinal)))
                    })]).Class("library-placeholders"), new P(new Text("library.naming.rules"))]));
            }

            // What an import does with the earlier file of the same film or episode.
            if (_replacement is { } replacement)
            {
                var modes = Enum.GetValues<RecycleMode>().Select(mode => new SelectOption(((byte)mode).ToString(CultureInfo.InvariantCulture), texts["library.replacement.mode." + mode]));
                var mounts = (_mounts?.Items ?? []).Where(mount => !mount.IsReadOnly)
                    .Select(mount => new SelectOption(mount.Id.ToString(CultureInfo.InvariantCulture), mount.Name)).Prepend(new SelectOption("", "-"));
                page.Content.Add(new Form<UpdateReplacementSettingsDataV1>(form =>
                {
                    form.Hidden(item => item.Version);
                    form.Toggle(item => item.KeepsReleases, "library.replacement.keepsReleases");
                    form.Select(item => item.RecycleModeId, "library.replacement.mode", modes);
                    form.Select(item => item.RecycleStorageMountId, "library.replacement.mount", mounts);
                    form.Text(item => item.RecyclePath, "library.replacement.path");
                    form.Number(item => item.RecycleDays, "library.replacement.days");
                    form.Toggle(item => item.UnmonitorsDeletedUnits, "library.replacement.unmonitorsDeleted");
                    form.Save();
                }, Address(handler: "Replacement"), csrf, _replacementInput ?? new UpdateReplacementSettingsDataV1(replacement.KeepsReleases, replacement.RecycleModeId,
                    replacement.RecycleStorageMountId, replacement.RecyclePath, replacement.RecycleDays, replacement.Version, replacement.UnmonitorsDeletedUnits), _replacementInput is null ? null : FormError));
            }

            if (FormError is not null && _failedFileId is null && _failedPattern is null && _failedSettings is null && _replacementInput is null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        // What the libraries hold altogether: how many, the files in them, those not identified and when one was scanned last.
        private IUiComponent Totals(IReadOnlyList<LibraryOverviewV1> libraries)
        {
            var scanned = libraries.Max(item => item.LastScanAt);
            return new KpiTiles(
                new KpiTile("library.tab.Overview", new ValueText(libraries.Count.ToString("N0", CultureInfo.CurrentCulture)), new Framework.Icon("library", assets, 20)),
                new KpiTile("storage.paths.files", new ValueText(libraries.Sum(item => item.FileCount).ToString("N0", CultureInfo.CurrentCulture)), new Framework.Icon("document", assets, 20)),
                new KpiTile("library.unmatched", new ValueText(libraries.Sum(item => item.UnmatchedCount).ToString("N0", CultureInfo.CurrentCulture)), new Framework.Icon("status", assets, 20)),
                new KpiTile("library.overview.scanned", scanned is { } at ? new ValueText(at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)) : new Text("library.overview.never"), new Framework.Icon("sync", assets, 20)));
        }

        private Form<AssignLibraryFileDataV1> Assignment(LibraryFileDetailsV1 file, string? csrf)
        {
            var units = WorkUnitTypes.For(file.MediaTypeId);
            var input = new AssignLibraryFileDataV1(file.SuggestedWorkId, file.SuggestedWorkId is null ? file.SuggestedTitle : null, file.SuggestedWorkUnitTypeId,
                file.SuggestedUnitNumber, file.SuggestedParentUnitNumber);
            return new Form<AssignLibraryFileDataV1>(form =>
            {
                if (file.SuggestedWorkId is null)
                {
                    form.Text(item => item.Title, "library.newWork");
                }
                else
                {
                    form.Hidden(item => item.WorkId);
                }

                if (units.Count > 0)
                {
                    form.Select(item => item.WorkUnitTypeId, "library.unitType", [new SelectOption("", texts["library.unit.None"]),
                        .. units.Select(unit => new SelectOption(((byte)unit).ToString(CultureInfo.InvariantCulture), texts["library.unit." + unit]))]);
                    form.Number(item => item.UnitNumber, "library.unitNumber");
                    if (units.Any(unit => WorkUnitTypes.ParentOf(unit) is not null)) form.Number(item => item.ParentUnitNumber, "library.parentNumber." + file.MediaTypeId);
                }

                form.Submit(file.SuggestedWorkId is null ? "library.createAndAssign" : "library.assignExisting");
            }, Address(handler: "Assign") + "&storedFileId=" + file.Id.ToString(CultureInfo.InvariantCulture), csrf, input, _failedFileId == file.Id ? FormError : null);
        }

        // Keeping one version is the administrator's confirmation, also against a better one.
        // Films and episodes have their subtitles searched by hand while the Subtitles module is on.
        private IUiComponent Subtitles(LibraryFileDetailsV1 item) => item.MediaTypeId is MediaType.Movie or MediaType.Series && texts.Can(null, "Subtitles")
            ? new A(new Text("details.subtitles")).Attribute("href", "/Admin/Library/Subtitles/" + item.Id.ToString(CultureInfo.InvariantCulture))
            : new Span();

        private Div Release(LibraryFileDetailsV1 file, string? csrf)
        {
            var id = file.Id.ToString(CultureInfo.InvariantCulture);
            var unassign = new Form<StoredFileParametersV1>(form => form.Submit("library.unassign"), Address(handler: "Unassign") + "&storedFileId=" + id, csrf,
                new StoredFileParametersV1(file.Id));
            var protection = new Form<ProtectLibraryFileDataV1>(form =>
            {
                form.Hidden(item => item.IsProtected);
                form.Submit(file.IsProtected ? "library.unprotect" : "library.protect");
            }, Address(handler: "Protect") + "&storedFileId=" + id, csrf, new ProtectLibraryFileDataV1(!file.IsProtected));
            return file.HasOtherVersions
                ? new Div(unassign, protection, new Form<StoredFileParametersV1>(form => form.Submit("library.keepOnly"), Address(handler: "KeepOnly") + "&storedFileId=" + id, csrf,
                    new StoredFileParametersV1(file.Id)))
                : new Div(unassign, protection);
        }

        public async Task<IActionResult> OnPostProtectAsync([BindRequired] long storedFileId, [BindRequired] bool isProtected)
        {
            await protect.ExecuteAsync<UpdateResult>(new StoredFileParametersV1(storedFileId), new ProtectLibraryFileDataV1(isProtected), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostKeepOnlyAsync([BindRequired] long storedFileId)
        {
            await keepOnly.ExecuteAsync<UpdateResult>(new StoredFileParametersV1(storedFileId), default, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnGetAsync()
        {
            if (Tab is LibraryTab.Naming or LibraryTab.Rename or LibraryTab.Versions && !CanRename) return LocalRedirect("/Admin/Library");
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAssignAsync([BindRequired] long storedFileId, long? workId, string? title, WorkUnitType? workUnitTypeId, decimal? unitNumber, decimal? parentUnitNumber)
        {
            _failedFileId = storedFileId;
            var data = new AssignLibraryFileDataV1(workId, string.IsNullOrWhiteSpace(title) ? null : title, workUnitTypeId, unitNumber, parentUnitNumber);
            await assign.ExecuteAsync<AssignLibraryFileResultV1>(new StoredFileParametersV1(storedFileId), data, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostUnassignAsync([BindRequired] long storedFileId)
        {
            await unassign.ExecuteAsync<UpdateResult>(new StoredFileParametersV1(storedFileId), default, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        // Missing files, and unidentified ones to assign.
        private static IUiComponent Attention(LibraryOverviewV1 item) => item.MissingCount == 0 && item.UnmatchedCount == 0 ? new Text("library.overview.fine")
            : new Span([.. new IUiComponent?[]
            {
                item.MissingCount > 0 ? new Span(new ValueText(item.MissingCount.ToString("N0", CultureInfo.CurrentCulture) + " "), new Text("library.missing")) : null,
                item.UnmatchedCount > 0 ? new A(new ValueText(item.UnmatchedCount.ToString("N0", CultureInfo.CurrentCulture) + " "), new Text("library.unmatched"))
                    .Attribute("href", "/Admin/Library/Unmatched?root=" + item.Id.ToString(CultureInfo.InvariantCulture)) : null
            }.OfType<IUiComponent>()]).Class("library-attention");

        // A library is read again in the background.
        public async Task<IActionResult> OnPostScanAsync([BindRequired] long libraryRootId)
        {
            await queueScan.ExecuteAsync<QueueLibraryScanResultV1>(new LibraryRootParametersV1(libraryRootId), default, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostNamingAsync([BindRequired] MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string? folder, string? file, long version)
        {
            _failedPattern = (mediaTypeId, mediaSubtypeId);
            _patternInput = new UpdateNamingPatternDataV1(folder, file ?? "", version);
            if (mediaSubtypeId is { } subtype)
            {
                await updateSubtypePattern.ExecuteAsync<UpdateResult>(new NamingSubtypePatternParametersV1(mediaTypeId, subtype), _patternInput, HttpContext.RequestAborted);
            }
            else
            {
                await updatePattern.ExecuteAsync<UpdateResult>(new NamingPatternParametersV1(mediaTypeId), _patternInput, HttpContext.RequestAborted);
            }

            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostNamingSettingsAsync([BindRequired] MediaType mediaTypeId, bool replacesIllegalCharacters, ColonReplacement colonReplacementId, string? customColonReplacement,
            string? specialsFolder, long version)
        {
            _failedSettings = mediaTypeId;
            _namingSettingsInput = new UpdateNamingSettingsDataV1(replacesIllegalCharacters, colonReplacementId, customColonReplacement, specialsFolder, version);
            await updateNamingSettings.ExecuteAsync<UpdateResult>(new NamingSettingsParametersV1(mediaTypeId), _namingSettingsInput, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostReplacementAsync(bool keepsReleases, RecycleMode recycleModeId, long? recycleStorageMountId, string? recyclePath, int recycleDays, long version, bool unmonitorsDeletedUnits)
        {
            _replacementInput = new UpdateReplacementSettingsDataV1(keepsReleases, recycleModeId, recycleStorageMountId, recyclePath, recycleDays, version, unmonitorsDeletedUnits);
            await updateReplacement.ExecuteAsync<UpdateResult>(new ReplacementSettingsParametersV1(), _replacementInput, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostRenameAsync([BindRequired] string fingerprint)
        {
            var result = await applyRenames.ExecuteAsync<ApplyLibraryRenamesResultV1>(new LibraryRenamesParametersV1(), new ApplyLibraryRenamesDataV1(fingerprint), HttpContext.RequestAborted);
            TempData["RenamedCount"] = result.RenamedCount;
            TempData["SkippedCount"] = result.SkippedCount;
            return LocalRedirect(Address());
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            if (Tab == LibraryTab.Overview)
            {
                _overview = await readOverview.ExecuteAsync<BaseDataList<LibraryOverviewV1>>(new ReadLibraryOverviewParametersV1(), ct);
                return;
            }

            if (Tab == LibraryTab.Naming)
            {
                _patterns = await readPatterns.ExecuteAsync<BaseDataList<NamingPatternV1>>(new ReadNamingPatternsParametersV1(), ct);
                _namingSettings = await readNamingSettings.ExecuteAsync<BaseDataList<NamingSettingsV1>>(new ReadNamingSettingsParametersV1(), ct);
                return;
            }

            if (Tab == LibraryTab.Versions)
            {
                _replacement = await readReplacement.ExecuteAsync<ReplacementSettingsV1>(new ReplacementSettingsParametersV1(), ct);
                _mounts = await readMounts.ExecuteAsync<PageResult<StorageMountDetailsV1>>(new ReadStorageMountsParametersV1(1, 100), ct);
                return;
            }

            if (Tab == LibraryTab.Rename)
            {
                _renames = await readRenames.ExecuteAsync<LibraryRenamePlanV1>(new ReadLibraryRenamesParametersV1(PageNumber, RenamePageSize), cancellationToken: ct);
                return;
            }

            _files = await readFiles.ExecuteAsync<PageResult<LibraryFileDetailsV1>>(new ReadLibraryFilesParametersV1(RootId, Tab == LibraryTab.Unmatched, PageNumber), ct);
        }

        private void Paging(Page page, int current, bool hasNext)
        {
            if (current > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", Address(current - 1)));
            if (hasNext) page.Content.Add(new A(new Text("common.next")).Attribute("href", Address(current + 1)));
        }

        private bool CanRename => texts.CanManageStorage();

        private string Address(int? pageNumber = null, string? handler = null) =>
            (Tab == LibraryTab.Overview ? "/Admin/Library" : "/Admin/Library/" + Tab) + "?PageNumber=" + (pageNumber ?? PageNumber).ToString(CultureInfo.InvariantCulture)
            + (RootId is { } root ? "&root=" + root.ToString(CultureInfo.InvariantCulture) : "") + (handler is null ? "" : "&handler=" + handler);
    }
}
