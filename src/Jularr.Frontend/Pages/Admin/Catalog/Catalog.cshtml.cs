using System.Globalization;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Worker.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Admin.Worker.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Catalog
{
    // The provider catalogs of local discovery as a list: how each is kept, how far it has come and the Owner's choices for it, which open in a dialog.
    public sealed class CatalogModel(ReadExternalWorkSyncsV1 read, UpdateExternalWorkSyncV1 update, ReadCoverCacheV1 readCovers, UpdateCoverCacheV1 updateCovers, ReadInstanceModulesV1 readModules, RunWorkerJobV1 run,
        FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
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
            page.Asset("Pages/Admin/Catalog/Catalog.entry.scss");
            page.Asset("Framework/Components/TimePicker.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            var token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            if (!_isOn)
            {
                page.Content.Add(new P(new Text(_keeps ? "admin.catalog.off" : "admin.catalog.nothingKept"), new ValueText(" "), new A(new Text("admin.instance.title")).Attribute("href", "/Admin/Instance"))
                    .Attribute("role", "status"));
            }

            // The figures, then the catalogs.
            page.Content.Add(new KpiTiles(
                new KpiTile("admin.catalog.kpi.catalogs", Number(_syncs.Count), Glyph("globe"), UiTone.Accent),
                new KpiTile("admin.catalog.entries", Number(_syncs.Sum(sync => sync.Entries)), Glyph("layers"), UiTone.Success),
                new KpiTile("admin.catalog.pending", Number(_syncs.Sum(sync => sync.Pending)), Glyph("clock"), UiTone.Warning)).Strip());

            var columns = new List<ListColumn>
            {
                new("admin.catalog.catalog", ListRole.Main), new("admin.catalog.mode", ListRole.Data, 2), new("admin.catalog.entries", ListRole.Data), new("admin.catalog.pending", ListRole.Data, 1),
                new("admin.catalog.listedOn", ListRole.Data, 1), new("table.status", ListRole.Status), new("common.actions", ListRole.Actions)
            };
            page.Content.Add(new ListPanel("admin.catalog.list", columns, [.. _syncs.Select(Row)]).Modifier("catalog-list"));
            page.Content.AddRange(_syncs.Select(sync => Dialog(sync, token)));

            // The images of titles that are not Works are kept for a while and up to a size.
            var covers = _coversDraft ?? new UpdateCoverCacheDataV1(_covers!.KeepDays, _covers.MaximumMegabytes, _covers.Version);
            page.Content.Add(new UiSection("admin.catalog.covers", [
                new Form<UpdateCoverCacheDataV1>(form =>
                {
                    form.Hidden(item => item.Version);
                    form.Number(item => item.KeepDays, "admin.catalog.coversKeepDays", affix: FieldAffix.Unit("unit.days"));
                    form.Number(item => item.MaximumMegabytes, "admin.catalog.coversMaximumMegabytes", affix: FieldAffix.Unit("unit.megabytes"));
                    form.Save();
                }, "/Admin/Catalog?handler=Covers", token, covers, _coversDraft is null ? null : FormError)]));

            // The page's action floats at the bottom; the catalogs share one background pass.
            var sync = new Button(Glyph("sync", 16), new Span(new Text("admin.catalog.syncNow")).Class("action-dock__text")).Submit();
            page.Content.Add(new ActionDock(assets, "admin.catalog.actions", [new PostForm("/Admin/Catalog?handler=Sync", token, [], _isOn ? sync : sync.Disabled())]));
        }

        // How a catalog is kept: asked live, the titles Discover loaded, or the whole catalog.
        private string Mode(ExternalWorkSyncV1 sync) => _isOn && sync.IsEnabled ? "whole" : _keeps ? "kept" : "live";

        private ListRow Row(ExternalWorkSyncV1 sync)
        {
            var windows = sync.Windows.Count == 0 ? texts["admin.catalog.allDay"] : string.Join(", ", sync.Windows.Select(window => window.StartsAt.ToString("HH:mm", CultureInfo.InvariantCulture) + "–" + window.EndsAt.ToString("HH:mm", CultureInfo.InvariantCulture)));
            var subline = new Span(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["admin.catalog.pace"], sync.RequestsPerMinute) + " · " + windows));
            var identity = new ListIdentity(new IconTile(Glyph("globe", 18), UiTone.Accent), new Text("admin.catalog." + sync.Namespace), subline);
            var mode = Mode(sync);
            var state = sync.IsEnabled
                ? new StateChip(new Text("admin.catalog.state.active"), UiTone.Success, Glyph("check", 14))
                : new StateChip(new Text("admin.catalog.state.off"), UiTone.Neutral, Glyph("pause", 14));
            var listed = sync.ListedOn is { } date ? new Span(new ValueText(date.ToString("d", CultureInfo.CurrentCulture))).Class("list-value") : new Span(new ValueText("–")).Class("list-value list-value--dim");
            var actions = new Div(Modal.Opener(new Button(Glyph("settings", 14), new Text("admin.catalog.configure")).Compact().Soft(), "catalog-" + sync.Namespace)).Class("list-actions");
            return new ListRow([identity, new Tag(new Text("admin.catalog.mode." + mode)), Number(sync.Entries), Number(sync.Pending), listed, state, actions],
                [ListPanel.Fact("admin.catalog.mode", new Text("admin.catalog.mode." + mode)), ListPanel.Fact("admin.catalog.entries", Number(sync.Entries)), ListPanel.Fact("admin.catalog.pending", Number(sync.Pending))]);
        }

        // One catalog's settings in a dialog; adding or removing a window shows the draft again with the dialog open, and only Save keeps it.
        private IUiComponent Dialog(ExternalWorkSyncV1 sync, string? token)
        {
            var data = _draftNamespace == sync.Namespace ? _draft! : new UpdateExternalWorkSyncDataV1(sync.IsEnabled, sync.RequestsPerMinute, sync.PausesWhileUsed, sync.Version, sync.Windows);
            var address = "/Admin/Catalog?namespace=" + Uri.EscapeDataString(sync.Namespace);
            var form = new Form<UpdateExternalWorkSyncDataV1>(form =>
            {
                form.Hidden(item => item.Version);
                form.Toggle(item => item.IsEnabled, "admin.catalog.enabled");
                form.Number(item => item.RequestsPerMinute, "admin.catalog.requestsPerMinute", affix: FieldAffix.Unit("unit.perMinute"));
                form.Toggle(item => item.PausesWhileUsed, "admin.catalog.pausesWhileUsed");
                form.Rows(item => item.Windows, (row, _) =>
                {
                    row.Time(item => item.StartsAt, "admin.catalog.startsAt");
                    row.Time(item => item.EndsAt, "admin.catalog.endsAt");
                });
                if (data.Windows.Count < MaxWindows) form.Content(WindowButton("admin.catalog.addWindow", address, true));
                if (data.Windows.Count > 0) form.Content(WindowButton("admin.catalog.removeWindow", address, false));
                form.Cancel();
                form.Submit("common.save");
            }, address, token, data, _draftNamespace == sync.Namespace ? FormError : null);
            return new Modal(assets, "catalog-" + sync.Namespace, "admin.catalog." + sync.Namespace, [form], _draftNamespace == sync.Namespace);
        }

        private static Button WindowButton(string labelKey, string address, bool add)
        {
            var button = new Button(new Text(labelKey));
            button.Attribute("type", "submit").Attribute("formaction", address + "&handler=Windows").Attribute("name", "add").Attribute("value", add ? "true" : "false")
                .Attribute("formnovalidate", "formnovalidate");
            return button;
        }

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);

        private static IUiComponent Number(long value) => new ValueText(value.ToString("N0", CultureInfo.CurrentCulture));

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

        // The catalogs' background pass starts at once.
        public async Task<IActionResult> OnPostSyncAsync()
        {
            await run.ExecuteAsync<UpdateResult>(new JobParametersV1("catalog"), default, HttpContext.RequestAborted);
            Hint("admin.catalog.syncStarted");
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
