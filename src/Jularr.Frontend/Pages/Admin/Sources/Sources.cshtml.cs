using System.Globalization;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Sources
{
    public sealed class SourcesModel(ReadSourceCatalogV1 read, SetUpSourcesV1 setUp, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private IReadOnlyList<SourceCatalogItemV1> _sources = [];

        protected override void Define(Page page)
        {
            page.Title("admin.sources.title");
            page.Navigation("nav.admin");
            page.Content.Add(AdminNavigation.Acquisition);
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            // Every enabled source is searched together; setup adds what needs nothing from the Owner.
            page.Content.Add(new Form<SourceCatalogParametersV1>(form => form.Submit("sources.setUpAll"), "/Admin/Acquisition/Sources?handler=SetUp", csrf, new SourceCatalogParametersV1()));
            if (TempData["SourcesAdded"] is string added)
            {
                page.Content.Add(new P(new ValueText(string.Format(CultureInfo.InvariantCulture, texts["sources.setUpResult"], added.Length == 0 ? "-" : added,
                    TempData["SourcesNeedAccount"] as string is { Length: > 0 } waiting ? waiting : "-"))).Attribute("role", "status"));
            }

            foreach (var group in _sources.GroupBy(source => source.SourceGroupId))
            {
                page.Content.Add(new UiSection("sources.group." + group.Key, [new Table<SourceCatalogItemV1>([.. group])
                    .Text(item => item.Name, "sources.name")
                    .Content("sources.capabilities", item => new ValueText(string.Join(", ", item.Capabilities.Select(capability => texts["sources.capability." + capability]))))
                    .Resource(item => item.SourceAvailabilityId, "table.status", availability => "sources.availability." + availability)
                    .Content("common.actions", Action)]));
            }
        }

        private IUiComponent Action(SourceCatalogItemV1 item)
        {
            if (item.IsConfigured)
            {
                return new Text("sources.configured");
            }

            // Only sources Jularr supports can be added; the form asks for accounts.
            if (item is { SourceAvailabilityId: SourceAvailability.Ready or SourceAvailability.Account, AcquisitionConnectionTypeId: { } type })
            {
                var href = "/Admin/Acquisition?Type=" + type + "&name=" + Uri.EscapeDataString(item.Name) + (item.DefaultBaseUrl is { } url ? "&url=" + Uri.EscapeDataString(url) : "");
                return new A(new Text("sources.add")).Class("button").Attribute("href", href);
            }

            return new Span();
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostSetUpAsync()
        {
            var result = await setUp.ExecuteAsync<SourceSetupResultV1>(new SourceCatalogParametersV1(), default, HttpContext.RequestAborted);
            var names = (await read.ExecuteAsync<BaseDataList<SourceCatalogItemV1>>(new SourceCatalogParametersV1(), HttpContext.RequestAborted)).ToDictionary(item => item.Key, item => item.Name);
            TempData["SourcesAdded"] = string.Join(", ", result.Added.Select(key => names.GetValueOrDefault(key, key)));
            TempData["SourcesNeedAccount"] = string.Join(", ", result.NeedsAccount.Select(key => names.GetValueOrDefault(key, key)));
            return RedirectToPage("/Admin/Sources/Sources");
        }

        protected override async Task LoadDataAsync()
        {
            _sources = await read.ExecuteAsync<BaseDataList<SourceCatalogItemV1>>(new SourceCatalogParametersV1(), HttpContext.RequestAborted);
        }
    }
}
