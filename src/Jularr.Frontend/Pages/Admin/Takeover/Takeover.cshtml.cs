using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Storage.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Takeover
{
    public sealed class TakeoverModel(RunTakeoverV1 run, ReadStorageMountsV1 mounts, ReadStorageMountV1 mount, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private RunTakeoverDataV1? _input;
        private IReadOnlyList<TakeoverItemV1>? _items;
        private SelectOption[] _mountChoices = [];
        private PageResult<StorageMountDetailsV1>? _mounts;
        [BindProperty(SupportsGet = true)] public int MountPage { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("admin.takeover.title");
            page.Navigation("nav.admin");
            if (SetupWizard.IsActive(Request)) page.Content.Add(SetupWizard.Frame(HttpContext, SetupStepType.Media));

            // The key is used for this read only; a preview changes nothing.
            // A preview's root folders are assigned to libraries before taking over; the suggestion is preselected.
            var sources = Enum.GetValues<TakeoverSourceType>().Select(source => new SelectOption(((byte)source).ToString(CultureInfo.InvariantCulture), texts["takeover.source." + source]));
            var folders = (_items ?? []).Where(item => item is { TakeoverItemTypeId: TakeoverItemType.RootFolder, LibraryRootId: not null }).ToArray();
            var input = DisplayInput(folders);
            page.Content.Add(new Form<RunTakeoverDataV1>(form =>
            {
                form.Select(item => item.TakeoverSourceTypeId, "takeover.source", sources);
                form.Text(item => item.BaseUrl, "acquisition.connection.baseUrl");
                form.Password(item => item.ApiKey, "acquisition.connection.apiKey", "off");
                form.Select(item => item.Apply, "takeover.mode", [new SelectOption("False", texts["takeover.preview"]), new SelectOption("True", texts["takeover.apply"])]);
                if (folders.Length > 0)
                {
                    form.Section("takeover.rootFolders", section => section.ChoiceTable("takeover.rootFolders",
                        [.. folders.Select(folder => new SelectOption(Choice(folder), folder.Name + " → " + folder.LibraryName))], [section.Column(item => item.RootFolders, "takeover.assign")]));
                }
                else foreach (var folder in input.RootFolders ?? [])
                    form.Content(new Input().Attribute("type", "hidden").Attribute("name", nameof(input.RootFolders)).Attribute("value", folder));
                if (input.ConnectionKeys.Count > 0) form.Section("takeover.inlineKeys", section => section.Rows(item => item.ConnectionKeys, (row, key) =>
                {
                    row.Hidden(item => item.TakeoverItemTypeId);
                    row.Hidden(item => item.BaseUrl);
                    row.Content(new P(new ValueText(key.BaseUrl)));
                    row.Password(item => item.ApiKey, "acquisition.connection.apiKey", "off");
                }));
                if (input.NewLibraries.Count > 0) form.Section("takeover.newLibraries", section =>
                {
                    section.Rows(item => item.NewLibraries, (row, library) =>
                    {
                        row.Hidden(item => item.SourcePath);
                        row.Content(new P(new ValueText(library.SourcePath)));
                        row.Text(item => item.Name, "profile.name");
                        row.Select(item => item.StorageMountId, "storage.title", _mountChoices);
                        row.Text(item => item.RelativePath, "storage.paths.path");
                    });
                    if (_mounts is { } mountPage)
                    {
                        if (mountPage.Page > 1) section.Content(MountPaging("common.previous", mountPage.Page - 1));
                        if (mountPage.Items.Count == mountPage.PageSize) section.Content(MountPaging("common.next", mountPage.Page + 1));
                    }
                });

                form.Submit("takeover.run");
            }, SetupWizard.Address(Request, SetupStepType.Media, "/Admin/Acquisition/Takeover"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, input, FormError));

            if (_items?.Where(item => item.LibraryRootId is null).ToArray() is { } items)
            {
                page.Content.Add(new Table<TakeoverItemV1>(items)
                    .Resource(item => item.TakeoverItemTypeId, "takeover.kind", kind => "takeover.kind." + kind)
                    .Text(item => item.Name, "sources.name")
                    .Resource(item => item.OutcomeKey, "table.status", outcome => "takeover.outcome." + outcome));
                if (items.Length == 0 && folders.Length == 0)
                {
                    page.Content.Add(new P(new Text("takeover.empty")));
                }
            }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] TakeoverSourceType takeoverSourceTypeId, string? baseUrl, string? apiKey, [BindRequired] bool apply,
            string[]? rootFolders, List<ConnectionKeyRow>? connectionKeys, List<LibraryRow>? newLibraries, bool draft = false)
        {
            _input = new RunTakeoverDataV1(takeoverSourceTypeId, baseUrl ?? "", apiKey ?? "", apply, rootFolders,
                new((connectionKeys ?? []).Select(item => new TakeoverConnectionKeyV1(item.TakeoverItemTypeId, item.BaseUrl ?? "", item.ApiKey))),
                new((newLibraries ?? []).Select(item => new TakeoverLibraryV1(item.SourcePath ?? "", item.Name ?? "", item.StorageMountId, item.RelativePath ?? ""))));
            if (!draft) _items = await run.ExecuteAsync<BaseDataList<TakeoverItemV1>>(new TakeoverParametersV1(), _input, HttpContext.RequestAborted);
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        private static string Choice(TakeoverItemV1 folder) => folder.LibraryRootId!.Value.ToString(CultureInfo.InvariantCulture) + ":" + folder.Name;

        private RunTakeoverDataV1 DisplayInput(IReadOnlyList<TakeoverItemV1> folders)
        {
            if (_input is null) return new(TakeoverSourceType.Prowlarr, "", "", false);
            var keys = _input.ConnectionKeys.Select(item => new TakeoverConnectionKeyV1(item.TakeoverItemTypeId, item.BaseUrl, null))
                .Concat((_items ?? []).Where(item => item.SuggestedUrl is not null)
                    .Select(item => new TakeoverConnectionKeyV1(item.TakeoverItemTypeId, item.SuggestedUrl!, null)))
                .DistinctBy(item => (item.TakeoverItemTypeId, item.BaseUrl)).ToArray();
            var libraries = (_items is null ? _input.NewLibraries.Select(item => item.SourcePath)
                    : _items.Where(item => item.TakeoverItemTypeId == TakeoverItemType.RootFolder).Select(item => item.Name)).Distinct(StringComparer.Ordinal)
                .Select(path => _input.NewLibraries.FirstOrDefault(item => item.SourcePath == path)
                    ?? new TakeoverLibraryV1(path, path.TrimEnd('/', '\\').Split('/', '\\')[^1] is { Length: > 0 } name ? name : texts["library.root"], null,
                        path.TrimEnd('/', '\\').Split('/', '\\')[^1])).ToArray();
            return new(_input.TakeoverSourceTypeId, _input.BaseUrl, "", _input.Apply,
                folders.Count > 0 ? [.. folders.Where(folder => folder.OutcomeKey is "takeover_suggested" or "takeover_assigned").Select(Choice)] : _input.RootFolders,
                new(keys), new(libraries));
        }

        protected override async Task LoadDataAsync()
        {
            _mounts = await mounts.ExecuteAsync<PageResult<StorageMountDetailsV1>>(new ReadStorageMountsParametersV1(MountPage), HttpContext.RequestAborted);
            var choices = _mounts.Items.ToList();
            foreach (var id in (_input?.NewLibraries.Select(item => item.StorageMountId).OfType<long>() ?? []).Distinct().Where(id => !choices.Any(item => item.Id == id)))
                choices.Add(await mount.ExecuteAsync<StorageMountDetailsV1>(new StorageMountParametersV1(id), HttpContext.RequestAborted));
            _mountChoices = new[] { new SelectOption("", texts["takeover.keepLibrary"]) }.Concat(choices.Select(item => new SelectOption(item.Id.ToString(CultureInfo.InvariantCulture), item.Name))).ToArray();
        }

        private IUiComponent MountPaging(string key, int target) => new Button(new Text(key)).Attribute("type", "submit")
            .Attribute("formaction", SetupWizard.Address(Request, SetupStepType.Media, "/Admin/Acquisition/Takeover") + "?draft=true&MountPage=" + target.ToString(CultureInfo.InvariantCulture))
            .Attribute("formnovalidate", "formnovalidate");

        public sealed record ConnectionKeyRow(TakeoverItemType TakeoverItemTypeId, string? BaseUrl, string? ApiKey);
        public sealed record LibraryRow(string? SourcePath, string? Name, long? StorageMountId, string? RelativePath);
    }
}
