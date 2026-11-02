using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Matches
{
    public sealed class MatchesModel(ReadIdentityReviewsV1 reviews, ResolveIdentityReviewV1 resolve, IAntiforgery antiforgery) : JularrPage
    {
        private PageResult<IdentityReviewV1>? _reviews;

        [BindProperty(SupportsGet = true)]
        public long? WorkId { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("admin.matches.title");
            page.Navigation("nav.admin");
            if (_reviews is not { } reviews)
            {
                if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
                return;
            }

            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            page.Content.Add(new P(new Text(reviews.Items.Count == 0 ? "admin.matches.empty" : "admin.matches.hint")).Attribute("role", "status"));

            // Each Work: its candidates, an entry by number, or no AniList entry at all.
            foreach (var review in reviews.Items)
            {
                var content = new List<IUiComponent>
                {
                    new P(new ValueText(review.CanonicalTitle), new Span(new ValueText(" · ")),
                        new Text(review.IsWebNovel ? "discover.webNovel" : MediaTypeLabel.Key(review.MediaTypeId, review.MediaSubtypeId)))
                };
                foreach (var (id, label) in review.CandidateIds.Zip(review.CandidateLabels))
                {
                    content.Add(new P(new ValueText(label + " · AniList " + id.ToString(CultureInfo.InvariantCulture))));
                    content.Add(new Form<ResolveIdentityReviewDataV1>(form =>
                    {
                        form.Hidden(item => item.AniListId);
                        form.Submit("admin.matches.accept");
                    }, Address(review.WorkId), csrf, new ResolveIdentityReviewDataV1(id)));
                }

                content.Add(new Form<ResolveIdentityReviewDataV1>(form =>
                {
                    form.Number(item => item.AniListId, "admin.matches.aniListId");
                    form.Submit("admin.matches.accept");
                }, Address(review.WorkId), csrf, new ResolveIdentityReviewDataV1(null), WorkId == review.WorkId ? FormError : null));
                content.Add(new Form<ResolveIdentityReviewDataV1>(form => form.Submit("admin.matches.dismiss"), Address(review.WorkId) + "&handler=Dismiss", csrf,
                    new ResolveIdentityReviewDataV1(null, true)));
                page.Content.Add(new Section([.. content]).Class("ui-section"));
            }

            page.Content.Add(new Span().Paged(reviews.Items.Count));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] int aniListId)
        {
            await resolve.ExecuteAsync<UpdateResult>(new IdentityReviewParametersV1(WorkId ?? 0), new ResolveIdentityReviewDataV1(aniListId), HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Matches/Matches");
        }

        public async Task<IActionResult> OnPostDismissAsync()
        {
            await resolve.ExecuteAsync<UpdateResult>(new IdentityReviewParametersV1(WorkId ?? 0), new ResolveIdentityReviewDataV1(null, true), HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Matches/Matches");
        }

        protected override async Task LoadDataAsync()
        {
            _reviews = await reviews.ExecuteAsync<PageResult<IdentityReviewV1>>(new ReadIdentityReviewsParametersV1(Math.Max(1, PageNumber)), HttpContext.RequestAborted);
        }

        private static string Address(long workId) => "/Admin/Works/Matches?workId=" + workId.ToString(CultureInfo.InvariantCulture);
    }
}
