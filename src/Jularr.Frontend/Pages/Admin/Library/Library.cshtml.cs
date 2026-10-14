using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
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
        Naming = 4
    }

    public sealed class LibraryModel(ReadLibraryFilesV1 readFiles, AssignLibraryFileV1 assign, UnassignLibraryFileV1 unassign, ReadLibraryRenamesV1 readRenames,
        ApplyLibraryRenamesV1 applyRenames, ReadNamingPatternsV1 readPatterns, UpdateNamingPatternV1 updatePattern, ReadReplacementSettingsV1 readReplacement,
        UpdateReplacementSettingsV1 updateReplacement, ReadStorageMountsV1 readMounts, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private ReplacementSettingsV1? _replacement;
        private UpdateReplacementSettingsDataV1? _replacementInput;
        private PageResult<StorageMountDetailsV1>? _mounts;
        private const int RenamePageSize = 100;
        private PageResult<LibraryFileDetailsV1>? _files;
        private LibraryRenamePlanV1? _renames;
        private IReadOnlyList<NamingPatternV1>? _patterns;
        private UpdateNamingPatternDataV1? _patternInput;
        private MediaType? _failedPattern;
        private long? _failedFileId;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public LibraryTab Tab { get; set; } = LibraryTab.Unmatched;

        protected override void Define(Page page)
        {
            page.Title("admin.library.title");
            page.Navigation("nav.admin");
            page.Content.Add(new NavigationTabs(Enum.GetValues<LibraryTab>().Where(tab => tab is not (LibraryTab.Rename or LibraryTab.Naming) || CanRename)
                .Select(tab => new NavigationTab("library.tab." + tab, Address(tab: tab), tab == Tab)).ToArray(), "admin.library.title"));
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

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
                        ? new Div(new A(new ValueText(item.WorkTitle ?? "")).Attribute("href", "/Admin/Works/" + workId.ToString(CultureInfo.InvariantCulture)), Release(item, csrf))
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

            // Names of movie and episode files for imports and renames.
            if (_patterns is { } patterns)
            {
                page.Content.Add(new P(new Text("library.naming.tokens")));
                foreach (var pattern in patterns)
                {
                    var failed = _failedPattern == pattern.MediaTypeId;
                    page.Content.Add(new Form<UpdateNamingPatternDataV1>(form =>
                    {
                        form.Hidden(item => item.Version);
                        form.Text(item => item.Pattern, "library.naming." + pattern.MediaTypeId);
                        form.Submit("common.save");
                    }, Address(handler: "Naming") + "&mediaTypeId=" + pattern.MediaTypeId, csrf, new UpdateNamingPatternDataV1(failed && _patternInput is { } input ? input.Pattern : pattern.Pattern, pattern.Version),
                        failed ? FormError : null));
                }
            }

            // What an import does with the earlier file of the same film or episode.
            if (_replacement is { } replacement)
            {
                var mounts = (_mounts?.Items ?? []).Where(mount => !mount.IsReadOnly)
                    .Select(mount => new SelectOption(mount.Id.ToString(CultureInfo.InvariantCulture), mount.Name)).Prepend(new SelectOption("", texts["library.replacement.noFolder"]));
                page.Content.Add(new UiSection("library.replacement.title", [new Form<UpdateReplacementSettingsDataV1>(form =>
                {
                    form.Hidden(item => item.Version);
                    form.Toggle(item => item.KeepsReleases, "library.replacement.keepsReleases");
                    form.Toggle(item => item.RecyclesInLibraries, "library.replacement.inLibraries");
                    form.Select(item => item.RecycleStorageMountId, "library.replacement.mount", mounts);
                    form.Text(item => item.RecyclePath, "library.replacement.path");
                    form.Number(item => item.RecycleDays, "library.replacement.days");
                    form.Submit("common.save");
                }, Address(handler: "Replacement"), csrf, _replacementInput ?? new UpdateReplacementSettingsDataV1(replacement.KeepsReleases, replacement.RecyclesInLibraries,
                    replacement.RecycleStorageMountId, replacement.RecyclePath, replacement.RecycleDays, replacement.Version), _replacementInput is null ? null : FormError)]));
            }

            if (FormError is not null && _failedFileId is null && _failedPattern is null && _replacementInput is null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
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

        private Form<StoredFileParametersV1> Release(LibraryFileDetailsV1 file, string? csrf) =>
            new(form => form.Submit("library.unassign"), Address(handler: "Unassign") + "&storedFileId=" + file.Id.ToString(CultureInfo.InvariantCulture), csrf,
                new StoredFileParametersV1(file.Id));

        public async Task<IActionResult> OnGetAsync()
        {
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

        public async Task<IActionResult> OnPostNamingAsync([BindRequired] MediaType mediaTypeId, string? pattern, long version)
        {
            _failedPattern = mediaTypeId;
            _patternInput = new UpdateNamingPatternDataV1(pattern ?? "", version);
            await updatePattern.ExecuteAsync<UpdateResult>(new NamingPatternParametersV1(mediaTypeId), _patternInput, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostReplacementAsync(bool keepsReleases, bool recyclesInLibraries, long? recycleStorageMountId, string? recyclePath, int recycleDays, long version)
        {
            _replacementInput = new UpdateReplacementSettingsDataV1(keepsReleases, recyclesInLibraries, recycleStorageMountId, recyclePath, recycleDays, version);
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
            if (Tab is LibraryTab.Rename or LibraryTab.Naming && !CanRename)
            {
                Tab = LibraryTab.Files;
            }

            if (Tab == LibraryTab.Naming)
            {
                _patterns = await readPatterns.ExecuteAsync<BaseDataList<NamingPatternV1>>(new ReadNamingPatternsParametersV1(), ct);
                _replacement = await readReplacement.ExecuteAsync<ReplacementSettingsV1>(new ReplacementSettingsParametersV1(), ct);
                _mounts = await readMounts.ExecuteAsync<PageResult<StorageMountDetailsV1>>(new ReadStorageMountsParametersV1(1, 100), ct);
                return;
            }

            if (Tab == LibraryTab.Rename)
            {
                _renames = await readRenames.ExecuteAsync<LibraryRenamePlanV1>(new ReadLibraryRenamesParametersV1(PageNumber, RenamePageSize), cancellationToken: ct);
                return;
            }

            _files = await readFiles.ExecuteAsync<PageResult<LibraryFileDetailsV1>>(new ReadLibraryFilesParametersV1(null, Tab == LibraryTab.Unmatched, PageNumber), ct);
        }

        private void Paging(Page page, int current, bool hasNext)
        {
            if (current > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", Address(current - 1)));
            if (hasNext) page.Content.Add(new A(new Text("common.next")).Attribute("href", Address(current + 1)));
        }

        private bool CanRename => texts.CanManageStorage();

        private string Address(int? pageNumber = null, LibraryTab? tab = null, string? handler = null) =>
            "/Admin/Library?Tab=" + (tab ?? Tab) + "&PageNumber=" + (pageNumber ?? (tab is null ? PageNumber : 1)).ToString(CultureInfo.InvariantCulture)
            + (handler is null ? "" : "&handler=" + handler);
    }
}
