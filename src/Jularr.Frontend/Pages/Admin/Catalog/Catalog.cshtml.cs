using System.Globalization;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Worker.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Admin.Worker.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Catalog
{
    // The provider catalogs of local discovery as cards: how each is kept, how far it has come and the Owner's choices for it as inputs inside the card.
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

            // Each catalog is a card with its counts and its own inputs, saved by the page's pill.
            page.Content.Add(new CardGrid([.. _syncs.Select(sync => Card(sync, token))]));

            // The images of titles that are not Works are kept for a while and up to a size.
            var covers = _coversDraft ?? new UpdateCoverCacheDataV1(_covers!.KeepDays, _covers.MaximumMegabytes, _covers.FormatId, _covers.Version);
            page.Content.Add(new UiSection("admin.catalog.covers", [
                new Form<UpdateCoverCacheDataV1>(form =>
                {
                    form.Hidden(item => item.Version);
                    form.Number(item => item.KeepDays, "admin.catalog.coversKeepDays", affix: FieldAffix.Unit("unit.days"));
                    form.Number(item => item.MaximumMegabytes, "admin.catalog.coversMaximumMegabytes", affix: FieldAffix.Unit("unit.megabytes"));
                    form.Select(item => item.FormatId, "admin.catalog.coversFormat", [new(((byte)CoverFormat.Jxl).ToString(CultureInfo.InvariantCulture), texts["admin.catalog.coversFormat.jxl"]),
                        new(((byte)CoverFormat.WebP).ToString(CultureInfo.InvariantCulture), texts["admin.catalog.coversFormat.webp"])]);

                    // Where cjxl cannot run, the state says which format is really kept.
                    if (_covers!.ActiveFormatId != _covers.FormatId) form.Note(new StateChip(new Text("admin.catalog.coversFormatFallback"), UiTone.Warning));
                    form.Save();
                }, "/Admin/Catalog?handler=Covers", token, covers, _coversDraft is null ? null : FormError)]));

            // The page's action floats at the bottom; the catalogs share one background pass.
            var sync = new Button(Glyph("sync", 16), new Span(new Text("admin.catalog.syncNow")).Class("action-dock__text")).Submit();
            page.Content.Add(new ActionDock(assets, "admin.catalog.actions", [new PostForm("/Admin/Catalog?handler=Sync", token, [], _isOn ? sync : sync.Disabled())]));
        }

        // How a catalog is kept: asked live, the titles Discover loaded, or the whole catalog.
        private string Mode(ExternalWorkSyncV1 sync) => _isOn && sync.IsEnabled ? "whole" : _keeps ? "kept" : "live";

        // One catalog: its mode and state in the head, what it holds, and its settings as inputs; adding or removing a window shows the draft of this card again, and only the pill keeps it.
        private IUiComponent Card(ExternalWorkSyncV1 sync, string? token)
        {
            var state = sync.IsEnabled
                ? new StateChip(new Text("admin.catalog.state.active"), UiTone.Success, Glyph("check", 14))
                : new StateChip(new Text("admin.catalog.state.off"), UiTone.Neutral, Glyph("pause", 14));
            var listed = sync.ListedOn is { } date ? new ValueText(date.ToString("d", CultureInfo.CurrentCulture)) : new ValueText("–");
            var body = new List<IUiComponent> { Fact("admin.catalog.entries", Number(sync.Entries)), Fact("admin.catalog.pending", Number(sync.Pending)), Fact("admin.catalog.listedOn", listed) };

            var data = _draftNamespace == sync.Namespace ? _draft! : new UpdateExternalWorkSyncDataV1(sync.IsEnabled, sync.RequestsPerMinute, sync.PausesWhileUsed, sync.Version, sync.Windows);
            var address = "/Admin/Catalog?namespace=" + Uri.EscapeDataString(sync.Namespace);
            body.Add(new Form<UpdateExternalWorkSyncDataV1>(form =>
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
                if (data.Windows.Count < MaxWindows) form.Action(WindowButton("admin.catalog.addWindow", address, true));
                if (data.Windows.Count > 0) form.Action(WindowButton("admin.catalog.removeWindow", address, false));
                form.Save();
            }, address, token, data, _draftNamespace == sync.Namespace ? FormError : null));
            return new FeatureCard(new IconTile(Glyph("globe", 18), UiTone.Accent), texts["admin.catalog." + sync.Namespace], texts["admin.catalog.mode." + Mode(sync)], state, body, [], null, "catalog-" + sync.Namespace);
        }

        private static IUiComponent Fact(string labelKey, IUiComponent value) => new Div(new Span(new Text(labelKey)).Class("feature-card__note"), new Span(value)).Class("feature-card__row");

        private static HtmlElement WindowButton(string labelKey, string address, bool add) =>
            new Button(new Text(labelKey)).Compact().Soft().Attribute("type", "submit").Attribute("formaction", address + "&handler=Windows").Attribute("name", "add").Attribute("value", add ? "true" : "false")
                .Attribute("formnovalidate", "formnovalidate");

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

        public async Task<IActionResult> OnPostCoversAsync(int keepDays, int maximumMegabytes, CoverFormat formatId, long version)
        {
            _coversDraft = new UpdateCoverCacheDataV1(keepDays, maximumMegabytes, formatId, version);
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
