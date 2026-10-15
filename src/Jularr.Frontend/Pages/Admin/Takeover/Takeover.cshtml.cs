using System.Globalization;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Takeover
{
    public sealed class TakeoverModel(RunTakeoverV1 run, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private RunTakeoverDataV1? _input;
        private IReadOnlyList<TakeoverItemV1>? _items;

        protected override void Define(Page page)
        {
            page.Title("admin.takeover.title");
            page.Navigation("nav.admin");
            if (SetupWizard.IsActive(Request)) page.Content.Add(SetupWizard.Frame(SetupStepType.Media, antiforgery.GetAndStoreTokens(HttpContext).RequestToken));

            // The key is used for this read only; a preview changes nothing.
            // A preview's root folders are assigned to libraries before taking over; the suggestion is preselected.
            var sources = Enum.GetValues<TakeoverSourceType>().Select(source => new SelectOption(((byte)source).ToString(CultureInfo.InvariantCulture), texts["takeover.source." + source]));
            var folders = (_items ?? []).Where(item => item is { TakeoverItemTypeId: TakeoverItemType.RootFolder, LibraryRootId: not null }).ToArray();
            page.Content.Add(new Form<RunTakeoverDataV1>(form =>
            {
                form.Select(item => item.TakeoverSourceTypeId, "takeover.source", sources);
                form.Text(item => item.BaseUrl, "acquisition.connection.baseUrl");
                form.Password(item => item.ApiKey, "acquisition.connection.apiKey", "off");
                form.Select(item => item.Apply, "takeover.mode", [new SelectOption("False", texts["takeover.preview"]), new SelectOption("True", texts["takeover.apply"])]);
                if (folders.Length > 0)
                {
                    form.Section("takeover.rootFolders", section => section.ChoiceTable("takeover.rootFolders",
                        [.. folders.Select(folder => new SelectOption(Choice(folder), folder.Name + " → " + folder.LibraryName))], section.Column(item => item.RootFolders, "takeover.assign")));
                }

                form.Submit("takeover.run");
            }, SetupWizard.Address(Request, SetupStepType.Media, "/Admin/Acquisition/Takeover"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, _input is { } input
                ? new RunTakeoverDataV1(input.TakeoverSourceTypeId, input.BaseUrl, "", input.Apply,
                    [.. folders.Where(folder => folder.OutcomeKey is "takeover_suggested" or "takeover_assigned").Select(Choice)])
                : new RunTakeoverDataV1(TakeoverSourceType.Prowlarr, "", "", false), FormError));

            if (_items?.Where(item => item.LibraryRootId is null).ToArray() is { } items)
            {
                page.Content.Add(new Table<TakeoverItemV1>(items)
                    .Resource(item => item.TakeoverItemTypeId, "takeover.kind", kind => "takeover.kind." + kind)
                    .Text(item => item.Name, "sources.name")
                    .Resource(item => item.OutcomeKey, "table.status", outcome => "takeover.outcome." + outcome)
                    .Content("common.actions", item => item.SuggestedUrl is { } url
                        ? new A(new Text("sources.add")).Class("button").Attribute("href", (item.TakeoverItemTypeId == TakeoverItemType.Client ? SetupWizard.Address(Request, SetupStepType.Downloads, "/Admin/Acquisition/Downloads") + "?Type=Sabnzbd"
                            : SetupWizard.Address(Request, SetupStepType.Indexers, "/Admin/Acquisition") + "?Type=Newznab")
                            + "&name=" + Uri.EscapeDataString(item.Name) + "&url=" + Uri.EscapeDataString(url))
                        : new Span()));
                if (items.Length == 0 && folders.Length == 0)
                {
                    page.Content.Add(new P(new Text("takeover.empty")));
                }
            }
        }

        public IActionResult OnGet()
        {
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] TakeoverSourceType takeoverSourceTypeId, string? baseUrl, string? apiKey, [BindRequired] bool apply, string[]? rootFolders)
        {
            _input = new RunTakeoverDataV1(takeoverSourceTypeId, baseUrl ?? "", apiKey ?? "", apply, rootFolders);
            _items = await run.ExecuteAsync<BaseDataList<TakeoverItemV1>>(new TakeoverParametersV1(), _input, HttpContext.RequestAborted);
            PreparePage();
            return Page();
        }

        private static string Choice(TakeoverItemV1 folder) => folder.LibraryRootId!.Value.ToString(CultureInfo.InvariantCulture) + ":" + folder.Name;
    }
}
