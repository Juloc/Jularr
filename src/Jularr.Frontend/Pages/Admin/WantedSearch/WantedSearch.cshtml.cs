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
    public sealed class WantedSearchModel(ReadWantedItemV1 readItem, SearchWantedV1 search, GrabWantedReleaseV1 grab, IAntiforgery antiforgery) : JularrPage
    {
        private WantedDetailsV1? _item;
        private SearchWantedDataV1? _search;
        private IReadOnlyList<WantedReleaseCandidateV1>? _candidates;

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        protected override void Define(Page page)
        {
            if (_item is not { } item) return;
            page.Title("wanted.tab.search", item.CanonicalTitle);
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

            // Every candidate stays visible with its decision; rejected ones cannot be taken.
            page.Content.Add(candidates.Count == 0 ? new P(new Text("wanted.search.empty")).Attribute("role", "status") : new Table<WantedReleaseCandidateV1>(candidates)
                .Resource(item => item.ReleaseDecisionTypeId, "wanted.search.decision", decision => "acquisition.decision." + decision)
                .Text(item => item.Score, "wanted.search.score")
                .Text(item => item.Title, "wanted.search.release")
                .Text(item => item.ConnectionName, "wanted.search.indexer")
                .Content("wanted.search.age", item => new ValueText(ReleaseFacts.AgeDays(item.PublishedAt)))
                .Content("wanted.search.size", item => new ValueText(ReleaseFacts.Size(item.SizeBytes)))
                .Text(item => item.QualityKey, "wanted.search.quality")
                .Content("wanted.search.reason", item => item.ReasonValue is { } value
                    ? new Span(new Text("acquisition.reason." + item.ReasonCode), new ValueText(" · " + value)) : new Text("acquisition.reason." + item.ReasonCode))
                .Content("common.actions", item => item.ReleaseDecisionTypeId == ReleaseDecisionType.Rejected ? new Span() : new Form<GrabWantedReleaseDataV1>(form =>
                {
                    form.Hidden(data => data.CandidateId);
                    form.Submit("wanted.search.grab");
                }, WantedStatus.Home(Id) + "/Search?handler=Grab", csrf, new GrabWantedReleaseDataV1(item.CandidateId))));
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
            TempData["WantedGrabbed"] = true;
            return LocalRedirect(WantedStatus.Home(Id));
        }

        protected override async Task LoadDataAsync() =>
            _item = await readItem.ExecuteAsync<WantedDetailsV1>(new WantedItemParametersV1(Id), HttpContext.RequestAborted);
    }
}
