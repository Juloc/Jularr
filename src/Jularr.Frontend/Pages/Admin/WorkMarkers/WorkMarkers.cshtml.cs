using System.Globalization;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.WorkMarkers
{
    // The skip markers of a series' episodes: what each file or sidecar gave, set by hand, copied over a season or removed.
    public sealed class WorkMarkersModel(ReadWorkMarkersV1 read, ReadWorkV1 readWork, SaveWorkMarkerV1 save, DeleteWorkMarkerV1 delete, CopyWorkMarkersV1 copy, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private static readonly MediaMarkerKind[] Kinds = Enum.GetValues<MediaMarkerKind>();
        private WorkDetailsV1? _work;
        private IReadOnlyList<WorkMarkerRowV1> _rows = [];
        private SaveWorkMarkerDataV1? _saveInput;
        private CopyWorkMarkersDataV1? _copyInput;

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        [BindProperty(SupportsGet = true)]
        public int? Season { get; set; }

        [BindProperty(SupportsGet = true)]
        public long? Unit { get; set; }

        protected override void Define(Page page)
        {
            page.Title("marker.title");
            page.Parent("admin.works.title", "/Admin/Works");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/WorkMarkers/WorkMarkers.entry.scss");
            page.Asset("Framework/Components/Form.entry.ts");
            if (_work is null) return;

            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var seasons = _rows.Select(row => row.SeasonNumber).Distinct().Order().ToArray();
            page.Content.Add(new A(new ValueText(_work.CanonicalTitle)).Class("marker-back").Attribute("href", "/Admin/Works/" + Id.ToString(CultureInfo.InvariantCulture)));
            if (seasons.Length == 0)
            {
                page.Content.Add(new P(new Text("marker.empty")).Attribute("role", "status"));
                return;
            }

            var season = Season is { } asked && seasons.Contains(asked) ? asked : seasons[0];
            if (seasons.Length > 1)
            {
                page.Content.Add(new Nav([.. seasons.Select(number => (IUiComponent)new A(new ValueText(texts["library.unit.Season"] + " " + number.ToString(CultureInfo.InvariantCulture)))
                    .Class(number == season ? "navigation-tabs__link navigation-tabs__link--active" : "navigation-tabs__link").Attribute("href", Address(number)))])
                    .Class("navigation-tabs").Attribute("aria-label", texts["marker.seasons"]));
            }

            if (TempData["MarkersCopied"] is int copied)
            {
                page.Content.Add(new P(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["marker.copied"], copied))).Attribute("role", "status"));
            }

            // One row per episode of the season.
            var episodes = _rows.Where(row => row.SeasonNumber == season).GroupBy(row => row.WorkUnitId).Select(group => group.First()).ToArray();
            if (Unit is { } unit && episodes.FirstOrDefault(row => row.WorkUnitId == unit) is { StoredFileId: not null } chosen)
            {
                page.Content.Add(Editor(chosen, season, csrf));
            }

            var table = new Table<WorkMarkerRowV1>(episodes).Content("marker.episode", row => Episode(row, season));
            foreach (var kind in Kinds)
            {
                table.Content("marker.kind." + kind, row => Found(row.WorkUnitId, kind));
            }

            page.Content.Add(table.Content("marker.chapters", row => new ValueText(row.ChapterCount.ToString(CultureInfo.CurrentCulture))));
        }

        // An episode with a file opens its editor; one without has nothing to mark.
        private IUiComponent Episode(WorkMarkerRowV1 row, int season)
        {
            var label = Label(row);
            return row.StoredFileId is null ? new ValueText(label) : new A(new ValueText(label)).Attribute("href", Address(season, row.WorkUnitId));
        }

        private IUiComponent Found(long workUnitId, MediaMarkerKind kind) =>
            _rows.FirstOrDefault(row => row.WorkUnitId == workUnitId && row.MediaMarkerKindTypeId == kind) is { } marker
                ? new Span(new ValueText(Range(marker)), new Tag(new Text("marker.source." + marker.MediaMarkerSourceTypeId))).Class("marker-found") : new ValueText("");

        // The four kinds of the episode, each with what is known and the ways to change it.
        private IUiComponent Editor(WorkMarkerRowV1 episode, int season, string? csrf)
        {
            var parts = new List<IUiComponent> { new H2(new ValueText(Label(episode))) };
            foreach (var kind in Kinds)
            {
                var found = _rows.FirstOrDefault(row => row.WorkUnitId == episode.WorkUnitId && row.MediaMarkerKindTypeId == kind);
                var input = _saveInput is { } failed && failed.MediaMarkerKindTypeId == kind ? failed
                    : new SaveWorkMarkerDataV1(kind, found?.StartMs is { } start ? start / 1000m : 0m, found?.EndMs is { } end ? end / 1000m : 0m);
                var offset = _copyInput is { } tried && tried.MediaMarkerKindTypeId == kind ? tried : new CopyWorkMarkersDataV1(kind, 0m);
                var shown = FormError is not null && (_saveInput?.MediaMarkerKindTypeId == kind || _copyInput?.MediaMarkerKindTypeId == kind);
                var forms = new List<IUiComponent>
                {
                    new Form<SaveWorkMarkerDataV1>(form =>
                    {
                        form.Hidden(item => item.MediaMarkerKindTypeId);
                        form.Number(item => item.StartSeconds, "marker.start");
                        form.Number(item => item.EndSeconds, "marker.end");
                        form.Save();
                    }, Address(season, episode.WorkUnitId, "Save"), csrf, input, shown && _saveInput is not null ? FormError : null),
                    new Form<CopyWorkMarkersDataV1>(form =>
                    {
                        form.Hidden(item => item.MediaMarkerKindTypeId);
                        form.Number(item => item.OffsetSeconds, "marker.offset");
                        form.Submit("marker.copy");
                    }, Address(season, episode.WorkUnitId, "Copy"), csrf, offset, shown && _copyInput is not null ? FormError : null)
                };
                if (found?.MediaMarkerSourceTypeId == MediaMarkerSource.Manual)
                {
                    forms.Add(new Form<CopyWorkMarkersDataV1>(form =>
                    {
                        form.Hidden(item => item.MediaMarkerKindTypeId);
                        form.Hidden(item => item.OffsetSeconds);
                        form.Submit("marker.delete");
                    }, Address(season, episode.WorkUnitId, "Delete"), csrf, new CopyWorkMarkersDataV1(kind, 0m)));
                }

                parts.Add(new Div(new Strong(new Text("marker.kind." + kind)), new Div([.. forms]).Class("marker-edit__forms")).Class("marker-edit"));
            }

            return new Section([.. parts]).Class("ui-section");
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostSaveAsync([BindRequired] MediaMarkerKind mediaMarkerKindTypeId, [BindRequired] decimal startSeconds, [BindRequired] decimal endSeconds)
        {
            _saveInput = new SaveWorkMarkerDataV1(mediaMarkerKindTypeId, startSeconds, endSeconds);
            await save.ExecuteAsync<UpdateResult>(new WorkUnitMarkerParametersV1(Id, Unit ?? 0), _saveInput, HttpContext.RequestAborted);
            return LocalRedirect(Address(Season, Unit));
        }

        public async Task<IActionResult> OnPostCopyAsync([BindRequired] MediaMarkerKind mediaMarkerKindTypeId, [BindRequired] decimal offsetSeconds)
        {
            _copyInput = new CopyWorkMarkersDataV1(mediaMarkerKindTypeId, offsetSeconds);
            var result = await copy.ExecuteAsync<CopyWorkMarkersResultV1>(new WorkUnitMarkerParametersV1(Id, Unit ?? 0), _copyInput, HttpContext.RequestAborted);
            TempData["MarkersCopied"] = result.CopiedCount;
            return LocalRedirect(Address(Season, Unit));
        }

        public async Task<IActionResult> OnPostDeleteAsync([BindRequired] MediaMarkerKind mediaMarkerKindTypeId)
        {
            await delete.ExecuteAsync<UpdateResult>(new WorkUnitMarkerKindParametersV1(Id, Unit ?? 0, mediaMarkerKindTypeId), default, HttpContext.RequestAborted);
            return LocalRedirect(Address(Season, Unit));
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _work = await readWork.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(Id), ct);
            _rows = await read.ExecuteAsync<BaseDataList<WorkMarkerRowV1>>(new WorkParametersV1(Id), ct);
        }

        private string Address(int? season, long? unit = null, string? handler = null)
        {
            var values = new List<string>();
            if (season is { } number) values.Add("Season=" + number.ToString(CultureInfo.InvariantCulture));
            if (unit is { } id) values.Add("Unit=" + id.ToString(CultureInfo.InvariantCulture));
            if (handler is not null) values.Add("handler=" + handler);
            return "/Admin/Works/" + Id.ToString(CultureInfo.InvariantCulture) + "/Markers" + (values.Count == 0 ? "" : "?" + string.Join('&', values));
        }

        private string Label(WorkMarkerRowV1 row) => MediaUnitLabel.Format(texts, WorkUnitType.Episode, row.Number, null, row.Title) ?? "";

        // m:ss to m:ss, in whole seconds.
        private static string Range(WorkMarkerRowV1 marker) => Clock(marker.StartMs ?? 0) + " – " + Clock(marker.EndMs ?? 0);

        private static string Clock(long milliseconds)
        {
            var total = milliseconds / 1000;
            return total >= 3600 ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", total / 3600, total / 60 % 60, total % 60)
                : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", total / 60, total % 60);
        }
    }
}
