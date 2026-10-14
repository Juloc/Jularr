using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Library.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Library
{
    public enum LibraryTab : byte
    {
        Unmatched = 1,
        Files = 2
    }

    public sealed class LibraryModel(ReadLibraryFilesV1 readFiles, AssignLibraryFileV1 assign, UnassignLibraryFileV1 unassign, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private PageResult<LibraryFileDetailsV1>? _files;
        private long? _failedFileId;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public LibraryTab Tab { get; set; } = LibraryTab.Unmatched;

        protected override void Define(Page page)
        {
            page.Title("admin.library.title");
            page.Navigation("nav.admin");
            page.Content.Add(new NavigationTabs(Enum.GetValues<LibraryTab>()
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
                page.Content.Add(table);
                if (files.Items.Count == 0) page.Content.Add(new P(new Text(Tab == LibraryTab.Unmatched ? "library.emptyUnmatched" : "library.empty")));
                Paging(page, files.Page, files.Items.Count == files.PageSize);
            }

            if (FormError is not null && _failedFileId is null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
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

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _files = await readFiles.ExecuteAsync<PageResult<LibraryFileDetailsV1>>(new ReadLibraryFilesParametersV1(null, Tab == LibraryTab.Unmatched, PageNumber), ct);
        }

        private void Paging(Page page, int current, bool hasNext)
        {
            if (current > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", Address(current - 1)));
            if (hasNext) page.Content.Add(new A(new Text("common.next")).Attribute("href", Address(current + 1)));
        }

        private string Address(int? pageNumber = null, LibraryTab? tab = null, string? handler = null) =>
            "/Admin/Library?Tab=" + (tab ?? Tab) + "&PageNumber=" + (pageNumber ?? (tab is null ? PageNumber : 1)).ToString(CultureInfo.InvariantCulture)
            + (handler is null ? "" : "&handler=" + handler);
    }
}
