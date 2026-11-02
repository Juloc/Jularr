using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Admin.Wanted;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.WantedSearch
{
    // A manual search of one target, or of its season, episode or volume: every candidate with its decision, the eligible ones to take.
    public sealed class WantedSearchModel(ReadWantedItemV1 readItem, SearchWantedV1 search, GrabWantedReleaseV1 grab, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private WantedDetailsV1? _item;
        private SearchWantedDataV1? _search;
        private IReadOnlyList<WantedReleaseCandidateV1>? _candidates;

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        protected override void Define(Page page)
        {
            if (_item is not { } item) return;
            page.Title("wanted.tab.search");
            page.Parent("admin.wanted.title", "/Admin/Wanted");
            page.ParentName(item.CanonicalTitle, WantedStatus.Home(Id));
            page.Navigation("nav.admin");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            page.Content.Add(new Div(new A(new Text("common.back")).Class("button").Attribute("href", WantedStatus.Home(Id))).Class("page-actions"));
            page.Content.Add(new Form<SearchWantedDataV1>(form =>
            {
                if (item.MediaTypeId == MediaType.Series)
                {
                    form.Number(data => data.ParentUnitNumber, "wanted.search.season");
                    form.Number(data => data.UnitNumber, "wanted.search.episode");
                }
                else if (item.MediaSubtypeId == MediaSubtype.Manga)
                {
                    form.Number(data => data.UnitNumber, "wanted.search.volume");
                }

                form.Submit("wanted.search.start");
            }, WantedStatus.Home(Id) + "/Search?handler=Search", csrf, _search ?? new SearchWantedDataV1()));
            if (FormError is not null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
            if (_candidates is not { } candidates) return;

            // Every candidate stays visible with its decision; rejected ones cannot be taken. The score's rules and points show on hover; books also show kind and volumes, every
            // release its group.
            if (candidates.Count == 0)
            {
                page.Content.Add(new P(new Text("wanted.search.empty")).Attribute("role", "status"));
                return;
            }

            var table = new Table<WantedReleaseCandidateV1>(candidates)
                .Resource(item => item.ReleaseDecisionTypeId, "wanted.search.decision", decision => "acquisition.decision." + decision)
                .Content("wanted.search.score", Score)
                .Text(item => item.Title, "wanted.search.release");
            if (item.MediaTypeId == MediaType.Book && item.MediaSubtypeId != MediaSubtype.Manga)
            {
                table.Content("wanted.search.kind", item => item.BookReleaseKindId is { } kind ? new Text("acquisition.kind." + kind) : new Span())
                    .Content("wanted.search.volumes", Volumes);
            }

            page.Content.Add(table
                .Content("wanted.search.group", item => new ValueText(item.ReleaseGroup ?? ""))
                .Text(item => item.ConnectionName, "wanted.search.indexer")
                .Content("wanted.search.age", item => new ValueText(ReleaseFacts.AgeDays(item.PublishedAt)))
                .Content("wanted.search.size", Size)
                .Text(item => item.QualityKey, "wanted.search.quality")
                .Content("wanted.search.reason", item => item.ReasonValue is { } value
                    ? new Span(new Text("acquisition.reason." + item.ReasonCode), new ValueText(" · " + value)) : new Text("acquisition.reason." + item.ReasonCode))
                .Content("common.actions", item => item.ReleaseDecisionTypeId == ReleaseDecisionType.Rejected ? new Span() : new Form<GrabWantedReleaseDataV1>(form =>
                {
                    form.Hidden(data => data.CandidateId);
                    form.Submit("wanted.search.grab");
                }, WantedStatus.Home(Id) + "/Search?handler=Grab", csrf, new GrabWantedReleaseDataV1(item.CandidateId))));
        }

        // The sum, with every rule and its points on hover.
        private IUiComponent Score(WantedReleaseCandidateV1 item)
        {
            var score = new Span(new ValueText(item.Score.ToString(CultureInfo.CurrentCulture)));
            if (item.ScorePoints.Count == 0) return score;
            // A rule's text names where what it saw goes ({0}, "Group LuCaZ (good)"), else it follows the text.
            var lines = item.ScorePoints.Select(point => Rule(texts["acquisition.points." + point.Code], point.Value) + ": " + point.Points.ToString("+0;-0;0", CultureInfo.CurrentCulture));
            return score.Attribute("title", string.Join("\n", lines));
        }

        private static string Rule(string text, string? value) => value is null ? text
            : text.Contains("{0}", StringComparison.Ordinal) ? string.Format(CultureInfo.CurrentCulture, text, value) : text + " " + value;

        private static IUiComponent Volumes(WantedReleaseCandidateV1 item)
        {
            var range = item.VolumeStart is not { } start ? null
                : item.VolumeEnd is { } end ? start.ToString(CultureInfo.CurrentCulture) + "\u2013" + end.ToString(CultureInfo.CurrentCulture) : start.ToString(CultureInfo.CurrentCulture);
            return (range, item.IsComplete) switch
            {
                (null, false) => new Span(),
                (null, true) => new Text("wanted.search.complete"),
                (_, false) => new ValueText(range),
                _ => new Span(new ValueText(range + " \u00b7 "), new Text("wanted.search.complete"))
            };
        }

        // An outlier of its kind's size per volume is marked, not silent.
        private static IUiComponent Size(WantedReleaseCandidateV1 item)
        {
            var size = new ValueText(ReleaseFacts.Size(item.SizeBytes));
            return item.ReleaseSizeMarkId switch
            {
                ReleaseSizeMark.AboveMaximum or ReleaseSizeMark.BelowMinimum => new Span(size, new ValueText(" "), new StateChip(new Text("acquisition.size." + item.ReleaseSizeMarkId), UiTone.Warning)),
                ReleaseSizeMark.Unknown => new Span(size, new ValueText(" "), new StateChip(new Text("acquisition.size.Unknown"), UiTone.Neutral)),
                _ => size
            };
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostSearchAsync(int? parentUnitNumber, int? unitNumber)
        {
            // Results belong to this response; grabs pick from the server-side search.
            _search = new SearchWantedDataV1(parentUnitNumber, unitNumber);
            await LoadDataAsync();
            _candidates = await search.ExecuteAsync<BaseDataList<WantedReleaseCandidateV1>>(new WantedItemParametersV1(Id), _search, HttpContext.RequestAborted);
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostGrabAsync(string? candidateId)
        {
            await grab.ExecuteAsync<UpdateResult>(new WantedItemParametersV1(Id), new GrabWantedReleaseDataV1(candidateId ?? ""), HttpContext.RequestAborted);
            Hint("wanted.grabQueued");
            return LocalRedirect(WantedStatus.Home(Id));
        }

        protected override async Task LoadDataAsync() =>
            _item = await readItem.ExecuteAsync<WantedDetailsV1>(new WantedItemParametersV1(Id), HttpContext.RequestAborted);
    }
}
