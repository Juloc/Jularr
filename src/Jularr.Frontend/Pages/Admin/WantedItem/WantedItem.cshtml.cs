using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Admin.Wanted;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.WantedItem
{
    // One target in Arr+: its state and searches, profile and library, seasons and episodes, and its releases.
    public sealed class WantedItemModel(ReadWantedItemV1 readItem, UpdateWantedV1 update, SearchWantedAutomaticallyV1 automatic, ReadWantedReleasesV1 releases,
        MarkWantedReleaseV1 mark, ReadAcquisitionProfilesV1 profiles, ReadWantedLibrariesV1 libraries, ReadWantedUnitsV1 readUnits, UpdateWantedUnitV1 updateUnit,
        IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private WantedDetailsV1? _item;
        private IReadOnlyList<WantedReleaseV1> _history = [];
        private IReadOnlyList<AcquisitionProfileDetailsV1> _profiles = [];
        private IReadOnlyList<WantedLibraryV1> _libraries = [];
        private IReadOnlyList<WantedUnitV1> _units = [];

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        // The season whose episodes are listed.
        [BindProperty(SupportsGet = true)]
        public long? SeasonId { get; set; }

        protected override void Define(Page page)
        {
            if (_item is not { } item) return;
            page.Title("admin.wanted.title", item.CanonicalTitle);
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/WantedItem/WantedItem.entry.scss");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            page.Content.Add(new Div(new A(new Text("common.back")).Class("button").Attribute("href", "/Admin/Wanted"),
                new A(new Text("wanted.openWork")).Class("button").Attribute("href", "/Admin/Works/" + item.WorkId.ToString(CultureInfo.InvariantCulture))).Class("wanted-actions"));
            page.Content.Add(new UiSection("wanted.section.status", [.. Status(item, csrf)]));
            if (CanAcquire && Options(item) is { Length: > 0 } options) page.Content.Add(new UiSection("wanted.section.choice", [ChoiceForm(item, options, csrf)]));
            if (item.HasUnits) page.Content.Add(new UiSection("wanted.monitoring", [.. Units(item, csrf)]));
            page.Content.Add(new UiSection("wanted.tab.history", [.. History(csrf)]));
            if (FormError is not null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
        }

        // State, then the searches of the whole title and pausing.
        private List<IUiComponent> Status(WantedDetailsV1 item, string? csrf)
        {
            var content = new List<IUiComponent> { new P(new Text("table.status"), new ValueText(": "), WantedStatus.Of(item)) };
            if (item.RequestedLanguages.Length > 0) content.Add(new P(new Text("request.language"), new ValueText(": " + string.Join(", ", item.RequestedLanguages))));
            if (item.LastSearchedAt is { } searchedAt)
            {
                content.Add(new P(new Text("wanted.lastSearch"), new ValueText(": " + searchedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))));
            }

            if (TempData["WantedSearched"] is string outcome)
            {
                content.Add(new P(new Text("acquisition.outcome." + outcome), new ValueText(TempData["WantedTaken"] is string taken ? ": " + taken : "")).Attribute("role", "status"));
            }

            if (TempData["WantedGrabbed"] is true) content.Add(new P(new Text("wanted.grabQueued")).Attribute("role", "status"));
            var actions = new List<IUiComponent>();
            if (CanAcquire)
            {
                // Seasons, episodes and volumes are chosen on the search page or in their rows.
                actions.Add(AutomaticForm(new SearchWantedDataV1(), null, csrf));
                actions.Add(new A(new Text("wanted.searchManual")).Class("button").Attribute("href", WantedStatus.Home(Id) + "/Search"));
            }

            actions.Add(new Form<UpdateWantedDataV1>(form =>
            {
                form.Hidden(data => data.IsPaused);
                form.Hidden(data => data.Version);
                form.Hidden(data => data.AcquisitionProfileId);
                form.Hidden(data => data.LibraryRootId);
                form.Submit(item.IsPaused ? "wanted.resume" : "wanted.pause");
            }, Address("Choice"), csrf, new UpdateWantedDataV1(!item.IsPaused, item.Version, item.AcquisitionProfileId, item.LibraryRootId)));
            content.Add(new Div([.. actions]).Class("wanted-actions"));
            return content;
        }

        // Without a choice the defaults of the subtype, then of the type apply.
        private AcquisitionProfileDetailsV1[] Options(WantedDetailsV1 item) =>
            [.. _profiles.Where(profile => profile.MediaTypeId == item.MediaTypeId && (profile.MediaSubtypeId is null || profile.MediaSubtypeId == item.MediaSubtypeId))];

        private Form<UpdateWantedDataV1> ChoiceForm(WantedDetailsV1 item, AcquisitionProfileDetailsV1[] options, string? csrf) => new(form =>
        {
            form.Hidden(data => data.IsPaused);
            form.Hidden(data => data.Version);
            form.Select(data => data.AcquisitionProfileId, "wanted.profile", [new SelectOption("", texts["wanted.profile.default"]),
                .. options.Select(profile => new SelectOption(profile.Id.ToString(CultureInfo.InvariantCulture), profile.Name))]);
            form.Select(data => data.LibraryRootId, "wanted.library", [new SelectOption("", texts["wanted.library.default"]),
                .. _libraries.Select(library => new SelectOption(library.Id.ToString(CultureInfo.InvariantCulture), library.Name))]);
            form.Submit("common.save");
        }, Address("Choice"), csrf, new UpdateWantedDataV1(item.IsPaused, item.Version, item.AcquisitionProfileId, item.LibraryRootId));

        // Seasons open their episodes; every row is monitored and searched on its own.
        private List<IUiComponent> Units(WantedDetailsV1 item, string? csrf)
        {
            var seasons = _units.Where(unit => unit.WorkUnitTypeId == WorkUnitType.Season).ToDictionary(unit => unit.Id, unit => unit.Number);
            var content = new List<IUiComponent>
            {
                new Form<UpdateWantedDataV1>(form =>
                {
                    form.Select(data => data.MonitorsNewUnits, "wanted.monitorsNewUnits",
                        [new SelectOption("True", texts["wanted.unit.monitored"]), new SelectOption("False", texts["wanted.unit.unmonitored"])]);
                    form.Submit("common.save");
                }, Address("MonitorsNewUnits"), csrf, new UpdateWantedDataV1(item.IsPaused, item.Version, item.AcquisitionProfileId, item.LibraryRootId, item.MonitorsNewUnits)),
                new Table<WantedUnitV1>(_units)
                    .Content("wanted.unit", unit => unit.WorkUnitTypeId == WorkUnitType.Season
                        ? new A(new ValueText(UnitName(unit, seasons))).Attribute("href", WantedStatus.Home(Id) + "?SeasonId=" + unit.Id.ToString(CultureInfo.InvariantCulture))
                        : new ValueText(UnitName(unit, seasons)))
                    .Content("media.title", unit => new ValueText(unit.Title ?? ""))
                    .Content("wanted.unit.releasedOn", unit => new ValueText(unit.ReleasedOn?.ToString("d", CultureInfo.CurrentCulture) ?? ""))
                    .Content("table.status", unit => unit.WorkUnitTypeId == WorkUnitType.Season ? new Span() : new Text(unit.HasMedia ? "wanted.unit.present" : "library.missing"))
                    .Content("wanted.monitoring", unit => new Text(MonitoringKey(unit)))
                    .Content("common.actions", unit => new Div([.. UnitActions(unit, seasons, csrf)]).Class("wanted-actions"))
            };
            return content;
        }

        private IEnumerable<IUiComponent> UnitActions(WantedUnitV1 unit, Dictionary<long, decimal> seasons, string? csrf)
        {
            var name = UnitName(unit, seasons);
            if (unit.WorkUnitTypeId != WorkUnitType.Season || unit.Episodes > 0)
            {
                yield return new Form<UpdateWantedUnitDataV1>(form =>
                {
                    form.Hidden(data => data.IsMonitored);
                    form.Submit(unit.IsMonitored ? "wanted.unit.stop" : "wanted.unit.start", name, valueHidden: true);
                }, Address("Unit") + "&workUnitId=" + unit.Id.ToString(CultureInfo.InvariantCulture), csrf, new UpdateWantedUnitDataV1(!unit.IsMonitored));
            }

            if (!CanAcquire || Scope(unit, seasons) is not { } scope) yield break;
            yield return AutomaticForm(scope, name, csrf);
            yield return new Form<SearchWantedDataV1>(form =>
            {
                form.Hidden(data => data.ParentUnitNumber);
                form.Hidden(data => data.UnitNumber);
                form.Submit("wanted.searchManual", name, valueHidden: true);
            }, SearchAddress(), csrf, scope);
        }

        // A season, an episode of its season or a volume; fractional numbers are searched by hand.
        private static SearchWantedDataV1? Scope(WantedUnitV1 unit, Dictionary<long, decimal> seasons)
        {
            static int? Whole(decimal number) => number == decimal.Truncate(number) && number is >= 0 and <= int.MaxValue ? (int)number : null;
            return unit.WorkUnitTypeId switch
            {
                WorkUnitType.Season => Whole(unit.Number) is { } season ? new SearchWantedDataV1(season) : null,
                _ when unit.ParentWorkUnitId is { } parent => seasons.TryGetValue(parent, out var season) && Whole(season) is { } seasonNumber && Whole(unit.Number) is { } episode
                    ? new SearchWantedDataV1(seasonNumber, episode) : null,
                _ => Whole(unit.Number) is { } volume ? new SearchWantedDataV1(null, volume) : null
            };
        }

        private Form<SearchWantedDataV1> AutomaticForm(SearchWantedDataV1 scope, string? name, string? csrf) => new(form =>
        {
            form.Hidden(data => data.ParentUnitNumber);
            form.Hidden(data => data.UnitNumber);
            if (name is null) form.Submit("wanted.searchAutomatic");
            else form.Submit("wanted.searchAutomatic", name, valueHidden: true);
        }, Address("Automatic"), csrf, scope);

        private List<IUiComponent> History(string? csrf)
        {
            if (_history.Count == 0) return [new P(new Text("wanted.history.empty"))];
            return
            [
                new Table<WantedReleaseV1>(_history)
                    .Text(item => item.Title, "wanted.search.release")
                    .Text(item => item.ConnectionName, "wanted.search.indexer")
                    .Text(item => item.QualityKey, "wanted.search.quality")
                    .Resource(item => item.AcquisitionReleaseStatusTypeId, "table.status", status => "acquisition.release." + status)
                    .Content("wanted.search.reason", item => item.ReasonKey is { } reason ? new Text("acquisition.outcome." + reason) : new Span())
                    .Content("wanted.progress", item => new ValueText(item.ProgressPercent?.ToString(CultureInfo.InvariantCulture) ?? ""))
                    .Content("wanted.history.date", item => new ValueText(item.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)))
                    .Content("common.actions", item => item.AcquisitionReleaseStatusTypeId == AcquisitionReleaseStatusType.Grabbed
                        ? new Span()
                        : new Form<MarkWantedReleaseDataV1>(form =>
                        {
                            form.Hidden(data => data.AcquisitionReleaseStatusTypeId);
                            form.Submit(item.IsBlocked ? "wanted.release.unblock" : "wanted.release.block");
                        }, Address("Mark") + "&acquisitionReleaseId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf,
                            new MarkWantedReleaseDataV1(item.IsBlocked ? AcquisitionReleaseStatusType.Allowed : AcquisitionReleaseStatusType.Blocked)))
            ];
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostChoiceAsync([BindRequired] bool isPaused, [BindRequired] long version, long? acquisitionProfileId, long? libraryRootId)
        {
            await update.ExecuteAsync<UpdateResult>(new UpdateWantedParametersV1(Id), new UpdateWantedDataV1(isPaused, version, acquisitionProfileId, libraryRootId), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostAutomaticAsync(int? parentUnitNumber, int? unitNumber)
        {
            var result = await automatic.ExecuteAsync<AutomaticSearchResultV1>(new WantedItemParametersV1(Id), new SearchWantedDataV1(parentUnitNumber, unitNumber), HttpContext.RequestAborted);
            TempData["WantedSearched"] = result.OutcomeKey;
            if (result.Title is { } title) TempData["WantedTaken"] = title;
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostUnitAsync([BindRequired] long workUnitId, [BindRequired] bool isMonitored)
        {
            await updateUnit.ExecuteAsync<UpdateResult>(new WantedUnitParametersV1(Id, workUnitId), new UpdateWantedUnitDataV1(isMonitored), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        // Only the choice for new units changes; the other settings are read fresh.
        public async Task<IActionResult> OnPostMonitorsNewUnitsAsync([BindRequired] bool monitorsNewUnits)
        {
            var current = await readItem.ExecuteAsync<WantedDetailsV1>(new WantedItemParametersV1(Id), HttpContext.RequestAborted);
            await update.ExecuteAsync<UpdateResult>(new UpdateWantedParametersV1(current.Id),
                new UpdateWantedDataV1(current.IsPaused, current.Version, current.AcquisitionProfileId, current.LibraryRootId, monitorsNewUnits), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostMarkAsync([BindRequired] long acquisitionReleaseId, [BindRequired] AcquisitionReleaseStatusType acquisitionReleaseStatusTypeId)
        {
            await mark.ExecuteAsync<UpdateResult>(new WantedReleaseParametersV1(Id, acquisitionReleaseId), new MarkWantedReleaseDataV1(acquisitionReleaseStatusTypeId), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _item = await readItem.ExecuteAsync<WantedDetailsV1>(new WantedItemParametersV1(Id), ct);
            _history = await releases.ExecuteAsync<BaseDataList<WantedReleaseV1>>(new WantedItemParametersV1(Id), ct);
            if (_item.HasUnits) _units = await readUnits.ExecuteAsync<BaseDataList<WantedUnitV1>>(new ReadWantedUnitsParametersV1(Id, SeasonId), ct);
            if (CanAcquire)
            {
                _profiles = await profiles.ExecuteAsync<BaseDataList<AcquisitionProfileDetailsV1>>(new ReadAcquisitionProfilesParametersV1(), ct);
                _libraries = await libraries.ExecuteAsync<BaseDataList<WantedLibraryV1>>(new WantedItemParametersV1(Id), ct);
            }
        }

        private static string MonitoringKey(WantedUnitV1 unit) => unit.WorkUnitTypeId != WorkUnitType.Season
            ? unit.IsMonitored ? "wanted.unit.monitored" : "wanted.unit.unmonitored"
            : unit.MonitoredEpisodes == 0 ? "wanted.unit.unmonitored" : unit.MonitoredEpisodes < unit.Episodes ? "wanted.unit.partly" : "wanted.unit.monitored";

        private string UnitName(WantedUnitV1 unit, Dictionary<long, decimal> seasons) => MediaUnitLabel.Format(texts, unit.WorkUnitTypeId, unit.Number,
            unit.ParentWorkUnitId is { } season && seasons.TryGetValue(season, out var parent) ? parent : null)!;

        // Searching needs Arr+; Requests alone stay manual.
        private bool CanAcquire => texts.CanAcquire();

        private string SearchAddress() => WantedStatus.Home(Id) + "/Search?handler=Search";

        // The open season stays open across actions.
        private string Address(string? handler = null)
        {
            var values = new List<string>();
            if (SeasonId is { } season) values.Add("SeasonId=" + season.ToString(CultureInfo.InvariantCulture));
            if (handler is not null) values.Add("handler=" + handler);
            return WantedStatus.Home(Id) + (values.Count == 0 ? "" : "?" + string.Join('&', values));
        }
    }
}
