using System.Globalization;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
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
            page.Content.Add(AdminNavigation.Acquisition);

            // The key is used for this read only; a preview changes nothing.
            var sources = Enum.GetValues<TakeoverSourceType>().Select(source => new SelectOption(((byte)source).ToString(CultureInfo.InvariantCulture), texts["takeover.source." + source]));
            page.Content.Add(new Form<RunTakeoverDataV1>(form =>
            {
                form.Select(item => item.TakeoverSourceTypeId, "takeover.source", sources);
                form.Text(item => item.BaseUrl, "acquisition.connection.baseUrl");
                form.Password(item => item.ApiKey, "acquisition.connection.apiKey", "off");
                form.Select(item => item.Apply, "takeover.mode", [new SelectOption("False", texts["takeover.preview"]), new SelectOption("True", texts["takeover.apply"])]);
                form.Submit("takeover.run");
            }, "/Admin/Acquisition/Takeover", antiforgery.GetAndStoreTokens(HttpContext).RequestToken, _input is { } input ? new RunTakeoverDataV1(input.TakeoverSourceTypeId, input.BaseUrl, "", input.Apply)
                : new RunTakeoverDataV1(TakeoverSourceType.Prowlarr, "", "", false), FormError));

            if (_items is { } items)
            {
                page.Content.Add(new Table<TakeoverItemV1>(items)
                    .Resource(item => item.TakeoverItemTypeId, "takeover.kind", kind => "takeover.kind." + kind)
                    .Text(item => item.Name, "sources.name")
                    .Resource(item => item.OutcomeKey, "table.status", outcome => "takeover.outcome." + outcome)
                    .Content("common.actions", item => item.SuggestedUrl is { } url
                        ? new A(new Text("sources.add")).Class("button").Attribute("href", "/Admin/Acquisition?Type=" + (item.TakeoverItemTypeId == TakeoverItemType.Client ? "Sabnzbd" : "Newznab")
                            + "&name=" + Uri.EscapeDataString(item.Name) + "&url=" + Uri.EscapeDataString(url))
                        : new Span()));
                if (items.Count == 0)
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

        public async Task<IActionResult> OnPostAsync([BindRequired] TakeoverSourceType takeoverSourceTypeId, string? baseUrl, string? apiKey, [BindRequired] bool apply)
        {
            _input = new RunTakeoverDataV1(takeoverSourceTypeId, baseUrl ?? "", apiKey ?? "", apply);
            _items = await run.ExecuteAsync<BaseDataList<TakeoverItemV1>>(new TakeoverParametersV1(), _input, HttpContext.RequestAborted);
            PreparePage();
            return Page();
        }
    }
}
