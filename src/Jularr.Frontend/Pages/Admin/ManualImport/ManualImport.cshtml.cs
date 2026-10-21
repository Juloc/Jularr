using System.Globalization;
using System.Linq.Expressions;
using System.Text.Encodings.Web;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Backend.Service.Admin.Storage.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Jularr.Frontend.Pages.Admin.ManualImport
{
    // A folder on any connected storage, imported by hand as a film or as the episodes of a series: choose it, check what each file is, import.
    public sealed class ManualImportModel(FindManualImportWorksV1 find, ReadManualImportWorkV1 readWork, ReadManualImportFilesV1 readFiles, RunManualImportV1 run,
        ReadManualImportDownloadV1 readDownload, RunManualImportDownloadV1 runDownload, ReadStorageMountsV1 mounts,
        ReadStorageMountV1 mount, ReadStorageDirectoriesV1 directories, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private BaseDataList<ManualImportWorkV1>? _works;
        private ManualImportWorkV1? _work;
        private ManualImportFolderV1? _folder;
        private string? _mountName;

        [BindProperty(SupportsGet = true)]
        public string? Query { get; set; }

        [BindProperty(SupportsGet = true)]
        public long? WantedItemId { get; set; }

        [BindProperty(SupportsGet = true)]
        public long? StorageMountId { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? RelativePath { get; set; }

        [BindProperty(SupportsGet = true)]
        public int? Imported { get; set; }

        // A waiting download of the queue: its folder and film or series are known.
        [BindProperty(SupportsGet = true)]
        public long? OperationId { get; set; }

        protected override void Define(Page page)
        {
            page.Title("admin.manualImport.title");
            page.Navigation("nav.admin");
            page.Asset("Framework/Components/FolderPicker.entry.ts");
            page.Asset("Framework/Components/Form.entry.ts");
            page.Asset("Pages/Admin/ManualImport/ManualImport.entry.scss");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            if (Imported is { } count)
            {
                page.Content.Add(new P(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["manualImport.done"], count))).Attribute("role", "status"));
            }

            if (_work is null)
            {
                page.Content.Add(new Form<ManualImportWorksParametersV1>(form =>
                {
                    form.Text(item => item.Query, "manualImport.search");
                    form.Submit("manualImport.find");
                }, Address("Find"), csrf, new ManualImportWorksParametersV1(Query ?? ""), FormError));
                if (_works is { } works)
                {
                    page.Content.Add(works.Count == 0 ? new P(new Text("manualImport.noMatch")).Attribute("role", "status") : new Table<ManualImportWorkV1>(works)
                        .Content("media.title", item => new ValueText(item.CanonicalTitle + (item.ReleaseYear is { } year ? " (" + year.ToString(CultureInfo.InvariantCulture) + ")" : "")))
                        .Content("media.type", item => new Text(MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId)))
                        .Link(item => item.WantedItemId, "common.actions", id => Address(wantedItemId: id), "manualImport.choose"));
                }

                return;
            }

            page.Content.Add(new Div(new H2(new ValueText(_work.CanonicalTitle + (_work.ReleaseYear is { } released ? " (" + released.ToString(CultureInfo.InvariantCulture) + ")" : ""))),
                OperationId is null ? new A(new Text("manualImport.change")).Attribute("href", Address()) : new Span()).Class("page-actions"));
            if (OperationId is null)
            {
                page.Content.Add(new Form<ReadManualImportDataV1>(form =>
                {
                    form.Hidden(item => item.StorageMountId);
                    form.Hidden(item => item.RelativePath);
                    form.Content(Picker());
                    form.Submit("manualImport.read");
                }, Address("Read", _work.WantedItemId), csrf, new ReadManualImportDataV1(StorageMountId ?? 0, RelativePath ?? ""), _folder is null ? FormError : null));
            }

            if (_folder is not { } folder || OperationId is null && StorageMountId is null)
            {
                return;
            }

            if (folder.Files.Count == 0)
            {
                page.Content.Add(new P(new Text("manualImport.noFiles")).Attribute("role", "status"));
                return;
            }

            // A film takes its largest video; a series every file whose name already says which episode it is.
            var largest = folder.Files.MaxBy(file => file.SizeBytes)!.Path;
            var found = folder.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
            var isMovie = _work.MediaTypeId == MediaType.Movie;
            var rows = new BaseDataList<ManualImportChoiceV1>(folder.Files.Select(file => new ManualImportChoiceV1(file.Path, isMovie ? file.Path == largest : file.Season is not null && file.Episode is not null,
                file.Season, file.Episode)));
            if (OperationId is { } operationId)
            {
                page.Content.Add(new Form<RunManualImportDownloadDataV1>(form =>
                {
                    ChoiceRows(form, item => item.Files, item => item.ImportTransferModeId, found, isMovie);
                    form.Submit("manualImport.run");
                }, Address("RunDownload", operationId: operationId), csrf, new RunManualImportDownloadDataV1(ImportTransferMode.Link, rows), FormError));
                return;
            }

            page.Content.Add(new Form<RunManualImportDataV1>(form =>
            {
                form.Hidden(item => item.StorageMountId);
                form.Hidden(item => item.RelativePath);
                ChoiceRows(form, item => item.Files, item => item.ImportTransferModeId, found, isMovie);
                form.Submit("manualImport.run");
            }, Address("Run", _work.WantedItemId), csrf, new RunManualImportDataV1(StorageMountId!.Value, RelativePath ?? "", ImportTransferMode.Link, rows), FormError));
        }

        // Each file with what its name says, whether it comes along and, for episodes, which one it is; then how the files are placed.
        private void ChoiceRows<TData>(Form<TData> form, Expression<Func<TData, BaseDataList<ManualImportChoiceV1>>> files, Expression<Func<TData, ImportTransferMode>> mode,
            Dictionary<string, ManualImportFileV1> found, bool isMovie) where TData : BaseData
        {
            form.Rows(files, (row, choice) =>
            {
                row.Hidden(item => item.Path);
                row.Content(new Div(new Strong(new ValueText(choice.Path)), new Span(new ValueText(Details(found[choice.Path])))).Class("manual-import-file"));
                row.Toggle(item => item.Imports, "manualImport.import");
                if (!isMovie)
                {
                    row.Number(item => item.Season, "manualImport.season");
                    row.Number(item => item.Episode, "manualImport.episode");
                }
            });
            form.Select(mode, "manualImport.mode", Enum.GetValues<ImportTransferMode>().Select(value => new SelectOption(((byte)value).ToString(CultureInfo.InvariantCulture), texts["manualImport.mode." + value])));
        }

        // The quality its name says, its size, and the name the library would give it.
        private static string Details(ManualImportFileV1 file) =>
            string.Join(" · ", new[] { file.QualityKey, ByteSize.Format(file.SizeBytes), file.TargetName is { } name ? "→ " + name : null }.Where(text => !string.IsNullOrEmpty(text)));

        private FolderPicker Picker()
        {
            var browse = QueryHelpers.AddQueryString(Address("Folders"), new Dictionary<string, string?>
            {
                ["storageMountId"] = StorageMountId?.ToString(CultureInfo.InvariantCulture)
            });
            var label = _mountName is not null ? _mountName + " /" + RelativePath : texts["downloadStorage.unset"];
            return new FolderPicker(browse, RelativePath ?? "", nameof(ReadManualImportDataV1.RelativePath), browse, storageMountFieldName: nameof(ReadManualImportDataV1.StorageMountId),
                choosesStorage: true, selectedLabel: label);
        }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!texts.Can("acquisition.manage", "Acquisition")) return LocalRedirect("/Admin/Activity");
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public IActionResult OnPostFind() => LocalRedirect(Address(query: Query));

        public IActionResult OnPostRead() => LocalRedirect(Address(wantedItemId: WantedItemId, storageMountId: StorageMountId is > 0 ? StorageMountId : null, relativePath: StorageMountId is > 0 ? RelativePath : null));

        public async Task<IActionResult> OnPostRunAsync(long storageMountId, string? relativePath, ImportTransferMode importTransferModeId, List<FileRow>? files)
        {
            var wantedItemId = WantedItemId ?? throw new KeyNotFoundException("The film or series is missing.");
            var choices = new BaseDataList<ManualImportChoiceV1>((files ?? []).Where(row => row.Path is not null).Select(row => new ManualImportChoiceV1(row.Path!, row.Imports, row.Season, row.Episode)));
            var result = await run.ExecuteAsync<ManualImportResultV1>(new ManualImportParametersV1(wantedItemId),
                new RunManualImportDataV1(storageMountId, relativePath ?? "", importTransferModeId, choices), HttpContext.RequestAborted);
            return LocalRedirect(Address(wantedItemId: wantedItemId, imported: result.Imported));
        }

        public async Task<IActionResult> OnPostRunDownloadAsync(ImportTransferMode importTransferModeId, List<FileRow>? files)
        {
            var operationId = OperationId ?? throw new KeyNotFoundException("The download is missing.");
            var choices = new BaseDataList<ManualImportChoiceV1>((files ?? []).Where(row => row.Path is not null).Select(row => new ManualImportChoiceV1(row.Path!, row.Imports, row.Season, row.Episode)));
            await runDownload.ExecuteAsync<ManualImportResultV1>(new ActivityOperationParametersV1(operationId), new RunManualImportDownloadDataV1(importTransferModeId, choices), HttpContext.RequestAborted);
            return LocalRedirect("/Admin/Activity");
        }

        public async Task<IActionResult> OnGetFoldersAsync(long? storageMountId, string? path, string? afterName, int mountPage = 1)
        {
            IUiComponent content;
            var ct = HttpContext.RequestAborted;
            var home = Address("Folders");
            if (storageMountId is { } id)
            {
                var connection = await mount.ExecuteAsync<StorageMountDetailsV1>(new StorageMountParametersV1(id), ct);
                var items = await directories.ExecuteAsync<BaseDataList<StorageDirectoryV1>>(new StorageMountParametersV1(id), new ReadStorageDirectoriesDataV1(path ?? "", afterName), ct);
                var browse = QueryHelpers.AddQueryString(home, "storageMountId", id.ToString(CultureInfo.InvariantCulture));
                content = FolderPicker.Directories(browse, path ?? "", items.Select(item => item.Name).ToArray(), texts, afterName, items.Count == StorageDirectoryV1.PageSize,
                    home, id, connection.Name + " /" + path);
            }
            else
            {
                var configured = await mounts.ExecuteAsync<PageResult<StorageMountDetailsV1>>(new ReadStorageMountsParametersV1(mountPage), ct);
                var entries = configured.Items.Select(item => (IUiComponent)new Li(new A(new ValueText(item.Name + " · " + item.SharePath))
                    .Attribute("href", QueryHelpers.AddQueryString(home, "storageMountId", item.Id.ToString(CultureInfo.InvariantCulture)))
                    .Attribute("data-folder-navigate", ""))).ToArray();
                var list = new List<IUiComponent> { new H3(new Text("folderPicker.storageConnections")) };
                list.Add(entries.Length == 0 ? new P(new Text("downloadStorage.noStorage")) : new Ul(entries).Class("folder-picker__folders"));
                if (mountPage > 1) list.Add(new A(new Text("common.previous")).Attribute("href", QueryHelpers.AddQueryString(home, "mountPage", (mountPage - 1).ToString(CultureInfo.InvariantCulture)))
                    .Attribute("data-folder-navigate", ""));
                if (configured.Items.Count == configured.PageSize) list.Add(new A(new Text("common.next")).Attribute("href", QueryHelpers.AddQueryString(home, "mountPage", (mountPage + 1).ToString(CultureInfo.InvariantCulture)))
                    .Attribute("data-folder-navigate", ""));
                content = new Div([.. list]).Attribute("data-folder-fragment", "");
            }

            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            content.WriteTo(writer, HtmlEncoder.Default, texts);
            Response.Headers.CacheControl = "no-store";
            return Content(writer.ToString(), "text/html");
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            if (OperationId is { } operationId)
            {
                var download = await readDownload.ExecuteAsync<ManualImportDownloadV1>(new ActivityOperationParametersV1(operationId), default, ct);
                WantedItemId = download.WantedItemId;
                _folder = new ManualImportFolderV1(download.Files);
            }

            if (WantedItemId is not { } wantedItemId)
            {
                if (!string.IsNullOrWhiteSpace(Query)) _works = await find.ExecuteAsync<BaseDataList<ManualImportWorkV1>>(new ManualImportWorksParametersV1(Query.Trim()), ct);
                return;
            }

            _work = await readWork.ExecuteAsync<ManualImportWorkV1>(new ManualImportParametersV1(wantedItemId), ct);
            if (OperationId is null && StorageMountId is { } mountId)
            {
                _mountName = (await mount.ExecuteAsync<StorageMountDetailsV1>(new StorageMountParametersV1(mountId), ct)).Name;
                if (RelativePath is { } path)
                {
                    _folder = await readFiles.ExecuteAsync<ManualImportFolderV1>(new ManualImportParametersV1(wantedItemId), new ReadManualImportDataV1(mountId, path), ct);
                }
            }
        }

        private string Address(string? handler = null, long? wantedItemId = null, long? storageMountId = null, string? relativePath = null, string? query = null, int? imported = null, long? operationId = null) =>
            QueryHelpers.AddQueryString("/Admin/ManualImport", new Dictionary<string, string?>
            {
                ["handler"] = handler,
                ["wantedItemId"] = (wantedItemId ?? (handler is null ? null : WantedItemId))?.ToString(CultureInfo.InvariantCulture),
                ["storageMountId"] = storageMountId?.ToString(CultureInfo.InvariantCulture),
                ["relativePath"] = relativePath,
                ["query"] = query,
                ["imported"] = imported?.ToString(CultureInfo.InvariantCulture),
                ["operationId"] = operationId?.ToString(CultureInfo.InvariantCulture)
            });

        public sealed record FileRow(string? Path, bool Imports, int? Season, int? Episode);
    }
}
