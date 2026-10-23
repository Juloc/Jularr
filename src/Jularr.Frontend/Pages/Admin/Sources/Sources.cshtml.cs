using System.Globalization;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Storage.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Sources
{
    public sealed class SourcesModel(ReadSourceCatalogV1 read, SetUpSourcesV1 setUp, ReadDownloadStorageSettingsV1 downloadStorage,
        IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private IReadOnlyList<SourceCatalogItemV1> _sources = [];
        private bool _directStorageReady = true;

        protected override void Define(Page page)
        {
            page.Title("admin.sources.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Acquisition/Acquisition.entry.scss");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            // Warn before adding a native book source, not after its first failed grab.
            if (!_directStorageReady) page.Content.Add(NativeDownloadStorageNotice.Message(DownloadStorageArea.Direct));

            // Every enabled source is searched together; setup adds what needs nothing from the Owner.
            page.Content.Add(new Form<SourceCatalogParametersV1>(form => form.Submit("sources.setUpAll"), "/Admin/Acquisition/Sources?handler=SetUp", csrf, new SourceCatalogParametersV1()));
            foreach (var group in _sources.Where(source => source.SourceGroupId != SourceGroup.Subtitles || texts.Can(null, "Subtitles")).GroupBy(source => source.SourceGroupId))
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
                var href = (item.SourceGroupId == SourceGroup.Subtitles ? "/Admin/Acquisition/Subtitles" : "/Admin/Acquisition") + "?Type=" + type + "&name=" + Uri.EscapeDataString(item.Name) + (item.DefaultBaseUrl is { } url ? "&url=" + Uri.EscapeDataString(url) : "");
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
            var added = string.Join(", ", result.Added.Select(key => names.GetValueOrDefault(key, key)));
            var waiting = string.Join(", ", result.NeedsAccount.Select(key => names.GetValueOrDefault(key, key)));
            Hint("sources.setUpResult", added.Length == 0 ? "-" : added, waiting.Length == 0 ? "-" : waiting);
            return RedirectToPage("/Admin/Sources/Sources");
        }

        protected override async Task LoadDataAsync()
        {
            _sources = await read.ExecuteAsync<BaseDataList<SourceCatalogItemV1>>(new SourceCatalogParametersV1(), HttpContext.RequestAborted);
            var settings = await downloadStorage.ExecuteAsync<DownloadStorageSettingsV1>(new DownloadStorageParametersV1(DownloadStorageArea.Direct), HttpContext.RequestAborted);
            _directStorageReady = NativeDownloadStorageNotice.IsConfigured(settings);
        }
    }
}
