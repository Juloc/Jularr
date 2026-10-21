using System.Globalization;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Catalog
{
    // The kept provider catalogs of full local discovery: how far each has come and the Owner's choices for it.
    public sealed class CatalogModel(ReadExternalWorkSyncsV1 read, UpdateExternalWorkSyncV1 update, ReadCoverCacheV1 readCovers, UpdateCoverCacheV1 updateCovers, ReadInstanceModulesV1 readModules, IAntiforgery antiforgery,
        UiText texts) : JularrPage
    {
        private const int MaxWindows = 8;
        private IReadOnlyList<ExternalWorkSyncV1> _syncs = [];
        private bool _isOn;
        private bool _keeps;
        private string? _draftNamespace;
        private UpdateExternalWorkSyncDataV1? _draft;
        private CoverCacheV1? _covers;
        private UpdateCoverCacheDataV1? _coversDraft;

        protected override void Define(Page page)
        {
            page.Title("admin.catalog.title");
            page.Navigation("nav.admin");
            page.Asset("Framework/Components/TimePicker.entry.ts");
            if (!_isOn)
            {
                page.Content.Add(new P(new Text(_keeps ? "admin.catalog.off" : "admin.catalog.nothingKept"), new ValueText(" "), new A(new Text("admin.instance.title")).Attribute("href", "/Admin/Instance"))
                    .Attribute("role", "status"));
            }

            var token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            foreach (var sync in _syncs)
            {
                var data = _draftNamespace == sync.Namespace ? _draft! : new UpdateExternalWorkSyncDataV1(sync.IsEnabled, sync.RequestsPerMinute, sync.PausesWhileUsed, sync.Version, sync.Windows);
                var address = "/Admin/Catalog?namespace=" + Uri.EscapeDataString(sync.Namespace);
                page.Content.Add(new UiSection("admin.catalog." + sync.Namespace, [
                    new FactList([
                        ("admin.catalog.entries", new ValueText(sync.Entries.ToString("N0", CultureInfo.CurrentCulture))),
                        ("admin.catalog.pending", new ValueText(sync.Pending.ToString("N0", CultureInfo.CurrentCulture))),
                        ("admin.catalog.listedOn", new ValueText(sync.ListedOn?.ToString("d", CultureInfo.CurrentCulture) ?? "–"))]),
                    new Form<UpdateExternalWorkSyncDataV1>(form =>
                    {
                        form.Hidden(item => item.Version);
                        form.Toggle(item => item.IsEnabled, "admin.catalog.enabled");
                        form.Number(item => item.RequestsPerMinute, "admin.catalog.requestsPerMinute");
                        form.Toggle(item => item.PausesWhileUsed, "admin.catalog.pausesWhileUsed");
                        form.Rows(item => item.Windows, (row, _) =>
                        {
                            row.Time(item => item.StartsAt, "admin.catalog.startsAt");
                            row.Time(item => item.EndsAt, "admin.catalog.endsAt");
                        });
                        if (data.Windows.Count < MaxWindows) form.Content(WindowButton("admin.catalog.addWindow", address, true));
                        if (data.Windows.Count > 0) form.Content(WindowButton("admin.catalog.removeWindow", address, false));
                        form.Save();
                    }, address, token, data, _draftNamespace == sync.Namespace ? FormError : null)]));
            }

            // The images of titles that are not Works are kept for a while and up to a size.
            var covers = _coversDraft ?? new UpdateCoverCacheDataV1(_covers!.KeepDays, _covers.MaximumMegabytes, _covers.Version);
            page.Content.Add(new UiSection("admin.catalog.covers", [
                new Form<UpdateCoverCacheDataV1>(form =>
                {
                    form.Hidden(item => item.Version);
                    form.Number(item => item.KeepDays, "admin.catalog.coversKeepDays");
                    form.Number(item => item.MaximumMegabytes, "admin.catalog.coversMaximumMegabytes");
                    form.Save();
                }, "/Admin/Catalog?handler=Covers", token, covers, _coversDraft is null ? null : FormError)]));
        }

        // Adding or removing a window shows the draft again; only Save keeps it.
        private static Button WindowButton(string labelKey, string address, bool add)
        {
            var button = new Button(new Text(labelKey));
            button.Attribute("type", "submit").Attribute("formaction", address + "&handler=Windows").Attribute("name", "add").Attribute("value", add ? "true" : "false")
                .Attribute("formnovalidate", "formnovalidate");
            return button;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string @namespace, bool isEnabled, int requestsPerMinute, bool pausesWhileUsed, long version, List<WindowRow>? windows)
        {
            _draftNamespace = @namespace;
            _draft = Input(isEnabled, requestsPerMinute, pausesWhileUsed, version, windows);
            await update.ExecuteAsync<UpdateResult>(new ExternalWorkSyncParametersV1(@namespace), _draft, HttpContext.RequestAborted);
            return LocalRedirect("/Admin/Catalog");
        }

        public async Task<IActionResult> OnPostCoversAsync(int keepDays, int maximumMegabytes, long version)
        {
            _coversDraft = new UpdateCoverCacheDataV1(keepDays, maximumMegabytes, version);
            await updateCovers.ExecuteAsync<UpdateResult>(new ReadCoverCacheParametersV1(), _coversDraft, HttpContext.RequestAborted);
            return LocalRedirect("/Admin/Catalog");
        }

        public async Task<IActionResult> OnPostWindowsAsync(string @namespace, bool isEnabled, int requestsPerMinute, bool pausesWhileUsed, long version, List<WindowRow>? windows, bool add)
        {
            var rows = windows ?? [];
            if (add && rows.Count < MaxWindows) rows.Add(new(new TimeOnly(2, 0), new TimeOnly(6, 0)));
            else if (!add && rows.Count > 0) rows.RemoveAt(rows.Count - 1);
            _draftNamespace = @namespace;
            _draft = Input(isEnabled, requestsPerMinute, pausesWhileUsed, version, rows);
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _syncs = await read.ExecuteAsync<BaseDataList<ExternalWorkSyncV1>>(new ReadExternalWorkSyncsParametersV1(), ct);
            _covers = await readCovers.ExecuteAsync<CoverCacheV1>(new ReadCoverCacheParametersV1(), ct);
            var modules = await readModules.ExecuteAsync<BaseDataList<InstanceModuleDetailsV1>>(new ReadInstanceModulesParametersV1(), ct);
            _keeps = modules.Any(module => module.Key == "DiscoverCache" && module.IsEnabled);
            _isOn = _keeps && modules.Any(module => module.Key == "LocalDiscovery" && module.IsEnabled);
        }

        private static UpdateExternalWorkSyncDataV1 Input(bool isEnabled, int requestsPerMinute, bool pausesWhileUsed, long version, List<WindowRow>? windows) =>
            new(isEnabled, requestsPerMinute, pausesWhileUsed, version,
                new((windows ?? []).Where(row => row.StartsAt is not null && row.EndsAt is not null).Select(row => new ExternalWorkSyncWindowV1(row.StartsAt!.Value, row.EndsAt!.Value))));

        public sealed record WindowRow(TimeOnly? StartsAt, TimeOnly? EndsAt);
    }
}
