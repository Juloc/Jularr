using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Browser.V1;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Browser.V1;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Browser
{
    // Temporary: the browser of the instance, what it does and what an administrator can make it do.
    public sealed class BrowserModel(ReadBrowserStatusV1 status, RunBrowserActionV1 run, ReadAcquisitionConnectionsV1 connections, ReadAcquisitionConnectionV1 connection,
        TestAcquisitionConnectionV1 test, UpdateAcquisitionConnectionV1 update, ReadWantedV1 wanted, SearchWantedV1 search, OpenBrowserAssistanceV1 openView,
        CloseBrowserAssistanceV1 closeView, ReadInstanceModulesV1 readModules, UpdateInstanceModuleV1 updateModule, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private BrowserStatusV1? _status;
        private PageResult<AcquisitionConnectionDetailsV1>? _connections;
        private PageResult<WantedDetailsV1>? _wanted;
        private InstanceModuleDetailsV1? _browserModule;

        protected override void Define(Page page)
        {
            page.Title("admin.browser.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Browser/Browser.entry.ts");
            page.Asset("Pages/Admin/Browser/Browser.entry.scss");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            // The host's browser is optional and never installs privileged packages through the web service.
            if (_status is { } browser)
            {
                var key = browser.IsRunning ? "admin.browser.running" : browser.IsAvailable ? "admin.browser.configured" : "admin.browser.missing";
                var tone = browser.IsRunning ? UiTone.Success : browser.IsAvailable ? UiTone.Warning : UiTone.Neutral;
                page.Content.Add(new Div(new StateChip(new Text(key), tone),
                    browser.LastError is { } error ? new P(new ValueText(error)) : new Span()));
                page.Content.Add(new P(new Text("admin.browser.installation"), new ValueText(": "),
                    new Text("admin.browser.installation." + browser.InstallationState)));
                if (browser.TotalBytes > 0 && browser.InstallationState == "downloading")
                {
                    page.Content.Add(new P(new ValueText(browser.DownloadedBytes.ToString(CultureInfo.InvariantCulture)),
                        new ValueText(" / " + browser.TotalBytes.ToString(CultureInfo.InvariantCulture) + " bytes")));
                }
                if (browser.InstalledVersion is { } packageVersion)
                    page.Content.Add(new P(new Text("admin.browser.version"), new ValueText(": " + packageVersion)));
                if (browser.ManagedSizeBytes > 0)
                    page.Content.Add(new P(new Text("admin.browser.diskUsage"),
                        new ValueText(": " + (browser.ManagedSizeBytes / 1048576d).ToString("0.0", CultureInfo.CurrentCulture) + " MiB")));
                if (browser.InstallationError is { } installError) page.Content.Add(new P(new ValueText(installError)));
                // Never change externally owned Chrome installations through managed-package actions.
                var managed = string.IsNullOrWhiteSpace(browser.RemoteAddress) &&
                    (browser.Path is null || browser.Path.Contains("/managed/current/", StringComparison.Ordinal));
                if (managed && _browserModule is { } module)
                {
                    if (!module.IsEnabled)
                    {
                        page.Content.Add(new Form<BrowserParametersV1>(form => form.Submit("admin.browser.install"),
                            "/Admin/Browser?handler=Install", csrf, new BrowserParametersV1()));
                    }
                    else
                    {
                        if (browser.InstallationState is "installed" or "failed")
                        {
                            page.Content.Add(new Form<BrowserParametersV1>(form => form.Submit("admin.browser.update"),
                                "/Admin/Browser?handler=Action&action=update", csrf, new BrowserParametersV1()));
                            page.Content.Add(new Form<BrowserParametersV1>(form => form.Submit("admin.browser.repair"),
                                "/Admin/Browser?handler=Action&action=repair", csrf, new BrowserParametersV1()));
                        }
                        page.Content.Add(new Form<BrowserParametersV1>(form => form.Submit("admin.browser.uninstall"),
                            "/Admin/Browser?handler=Uninstall", csrf, new BrowserParametersV1()));
                    }
                }
                page.Content.Add(new P(new Text("admin.browser.moduleHint"),
                    new A(new Text("admin.browser.moduleLink")).Attribute("href", "/Admin/Instance/Modules")));
            }

            var sources = _status is { IsAvailable: true, RemoteAddress: null }
                ? _connections?.Items.Where(item => item.IsEnabled && item.AcquisitionConnectionTypeId == AcquisitionConnectionType.AnnasArchive).ToArray() ?? [] : [];
            if (sources.Length > 0)
            {
                var selected = _status?.ManualView?.AcquisitionConnectionId ?? sources[0].Id;
                page.Content.Add(new Form<AcquisitionConnectionParametersV1>(form =>
                {
                    form.Select(item => item.AcquisitionConnectionId, "admin.browser.source", [.. sources.Select(item => new SelectOption(item.Id.ToString(CultureInfo.InvariantCulture), item.BaseUrl is { } address ? new Uri(address).Host : item.Id.ToString(CultureInfo.InvariantCulture)))]);
                    form.Submit("admin.browser.manualOpen");
                }, "/Admin/Browser?handler=OpenView", csrf, new AcquisitionConnectionParametersV1(selected)));
            }
            if (_status?.ManualView is { } view) page.Content.Add(new RemoteBrowser(view, csrf));

            // What the browser is and what it did last.
            page.Content.Add(new H2(new Text("admin.browser.state")));
            page.Content.Add(new Table<BrowserEventV1>([.. _status?.Events.Items ?? []]).Text(item => item.Kind, "admin.browser.kind").Text(item => item.Message, "admin.browser.message").Text(item => item.At, "admin.browser.at"));
            // What the administrator can make it do.
            page.Content.Add(new H2(new Text("admin.browser.actions")));
            page.Content.Add(new Form<RunBrowserActionDataV1>(form =>
            {
                form.Select(item => item.Action, "admin.browser.action", [new SelectOption("start", texts["admin.browser.start"]), new SelectOption("stop", texts["admin.browser.stop"]),
                    new SelectOption("reset", texts["admin.browser.reset"]), new SelectOption("read", texts["admin.browser.read"])]);
                form.Text(item => item.Url, "admin.browser.url");
                form.Submit("admin.browser.run");
            }, "/Admin/Browser?handler=Action", csrf, new RunBrowserActionDataV1("start"), FormError));

            // The sources of the instance: every one of them can be checked and put out of the way.
            page.Content.Add(new H2(new Text("admin.browser.sources")));
            page.Content.Add(new Table<AcquisitionConnectionDetailsV1>([.. _connections?.Items ?? []])
                .Text(item => item.Name, "admin.browser.source")
                .Text(item => item.BaseUrl, "admin.browser.address")
                .Content("admin.browser.state", item => new ValueText(item.ErrorKey ?? "-"))
                .Content("common.actions", item => Actions(item, csrf)));

            // The requests of the instance: a person may search them by hand.
            page.Content.Add(new H2(new Text("admin.browser.wanted")));
            page.Content.Add(new Table<WantedDetailsV1>([.. _wanted?.Items ?? []])
                .Text(item => item.CanonicalTitle, "admin.browser.request")
                .Content("common.actions", item => new Form<BrowserParametersV1>(form => form.Submit("admin.browser.search"),
                    "/Admin/Browser?handler=Search&wantedItemId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new BrowserParametersV1())));
        }

        private IUiComponent Actions(AcquisitionConnectionDetailsV1 item, string? csrf)
        {
            var buttons = new List<IUiComponent>
            {
                new Form<BrowserParametersV1>(form => form.Submit("admin.browser.test"), "/Admin/Browser?handler=Test&acquisitionConnectionId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new BrowserParametersV1()),
                new Form<BrowserParametersV1>(form => form.Submit("admin.browser.off"), "/Admin/Browser?handler=Off&acquisitionConnectionId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new BrowserParametersV1())
            };
            return new Div([.. buttons]);
        }

        public async Task<IActionResult> OnPostOpenViewAsync(long acquisitionConnectionId)
        {
            await openView.ExecuteAsync<BrowserViewV1>(new AcquisitionConnectionParametersV1(acquisitionConnectionId), default, HttpContext.RequestAborted);
            await LoadDataAsync();
            PreparePage();
            NoHint();
            return Page();
        }

        public async Task<IActionResult> OnPostCloseViewAsync(Guid leaseId)
        {
            await closeView.ExecuteAsync<UpdateResult>(new BrowserLeaseParametersV1(leaseId), default, HttpContext.RequestAborted);
            await LoadDataAsync();
            PreparePage();
            Hint("admin.browser.manualClosed");
            return Page();
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostActionAsync(string action, string? url)
        {
            var result = await run.ExecuteAsync<BrowserStatusV1>(new BrowserParametersV1(), new RunBrowserActionDataV1(action ?? "start", url), HttpContext.RequestAborted);
            Hint(action is "update" or "repair" ? "admin.browser.queuedHint" : "admin.browser.ran");
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostInstallAsync()
        {
            await SetBrowserModuleAsync(true);
            return LocalRedirect("/Admin/Browser");
        }

        public async Task<IActionResult> OnPostUninstallAsync()
        {
            await SetBrowserModuleAsync(false);
            return LocalRedirect("/Admin/Browser");
        }

        private async Task SetBrowserModuleAsync(bool enabled)
        {
            var modules = await readModules.ExecuteAsync<BaseDataList<InstanceModuleDetailsV1>>(
                new ReadInstanceModulesParametersV1(), HttpContext.RequestAborted);
            var module = modules.Single(item => item.Key == "Browser");
            if (module.IsEnabled != enabled)
                await updateModule.ExecuteAsync<UpdateResult>(new UpdateInstanceModuleParametersV1(module.Id),
                    new UpdateInstanceModuleDataV1(enabled, module.Version), HttpContext.RequestAborted);
        }

        public async Task<IActionResult> OnPostTestAsync(long acquisitionConnectionId)
        {
            var result = await test.ExecuteAsync<AcquisitionConnectionTestV1>(new AcquisitionConnectionParametersV1(acquisitionConnectionId), default, HttpContext.RequestAborted);
            HintText(result.IsAvailable ? texts["admin.browser.sourceOk"] : texts["admin.browser.sourceFailed"] + " " + (result.ErrorKey ?? "-"));
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        // The source stops searching by itself; the requests stay with the people.
        public async Task<IActionResult> OnPostOffAsync(long acquisitionConnectionId)
        {
            var details = await connection.ExecuteAsync<AcquisitionConnectionDetailsV1>(new AcquisitionConnectionParametersV1(acquisitionConnectionId), HttpContext.RequestAborted);
            await update.ExecuteAsync<UpdateResult>(new AcquisitionConnectionParametersV1(acquisitionConnectionId), new SaveAcquisitionConnectionDataV1(details.Name, details.AcquisitionConnectionTypeId, details.BaseUrl, null,
                false, details.Priority, details.Categories, details.DownloadCategory, details.RemotePath, details.LocalPath, details.Username, details.MaxConnections, null,
                details.MediaTypeIds, details.SearchesAutomatically, details.QueryLimit, details.GrabLimit, details.Host, details.Port, details.UseSsl, details.DownloadStorageMountId), HttpContext.RequestAborted);
            Hint("admin.browser.sourceOff");
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        // A person searches the request by hand.
        public async Task<IActionResult> OnPostSearchAsync(long wantedItemId)
        {
            var result = await search.ExecuteAsync<BaseDataList<WantedReleaseCandidateV1>>(new WantedItemParametersV1(wantedItemId), new SearchWantedDataV1(), HttpContext.RequestAborted);
            HintText(texts["admin.browser.searched"] + " " + result.Count);
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            var modules = await readModules.ExecuteAsync<BaseDataList<InstanceModuleDetailsV1>>(
                new ReadInstanceModulesParametersV1(), HttpContext.RequestAborted);
            _browserModule = modules.FirstOrDefault(item => item.Key == "Browser");
            _status = await status.ExecuteAsync<BrowserStatusV1>(new BrowserParametersV1(), HttpContext.RequestAborted);
            _connections = await connections.ExecuteAsync<PageResult<AcquisitionConnectionDetailsV1>>(new ReadAcquisitionConnectionsParametersV1(1, 50), HttpContext.RequestAborted);
            _wanted = await wanted.ExecuteAsync<PageResult<WantedDetailsV1>>(new ReadWantedParametersV1(1, 50, wantedViewTypeId: WantedViewType.Missing), HttpContext.RequestAborted);
        }
    }
}
