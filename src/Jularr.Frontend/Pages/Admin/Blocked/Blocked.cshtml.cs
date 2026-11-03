using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Admin.Activity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Blocked
{
    // The releases that are not taken again, of every target: the media-type bar and the entry's views on top, one row per release with its title, indexer and why, and the button that lifts the block.
    public sealed class BlockedModel(ReadBlockedReleasesV1 read, MarkWantedReleaseV1 mark, IAntiforgery antiforgery, FrontendAssets assets, UiText texts) : JularrPage
    {
        private PageResult<BlockedReleaseV1>? _releases;
        private ActivityViews? _views;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        // Media types as slugs of the type bar, as on the Queue; none shows all of them.
        [BindProperty(SupportsGet = true, Name = "type")]
        public string[] Types { get; set; } = [];

        private ActivityViews Views => _views ??= new ActivityViews(Types, texts, Request);

        protected override void Define(Page page)
        {
            page.Title("admin.blocked.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Blocked/Blocked.entry.scss");
            page.Asset("Framework/Components/Truncated.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            page.NoPageRow();
            page.Content.Add(new MediaTypeBar(assets, Views.Scopes.ListedSlugs, Views.Scopes.ChosenSlugs, "library.scope.", types => Views.Address("/Admin/Activity/Blocked", types)));
            page.Content.Add(new ListToolbar(Views.Tabs()));
            if (_releases is { } releases)
            {
                var columns = new List<ListColumn> { new("table.work", ListRole.Main), new("blocked.release", ListRole.Data, 1), new("wanted.search.indexer", ListRole.Data, 2), new("wanted.search.reason", ListRole.Data, 1), new("blocked.since"), new("common.actions", ListRole.Actions) };
                var empty = new Div(new IconTile(Glyph("shield-check", 22), UiTone.Neutral), new P(new Text("blocked.empty")));
                page.Content.Add(new ListPanel("blocked.list", columns, [.. releases.Items.Select(item => Row(item, csrf))], empty).Modifier("blocked-list").Paged());
            }

            if (FormError is not null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
            page.Content.Add(new ActionDock(assets, "blocked.actions", []));
        }

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);

        private ListRow Row(BlockedReleaseV1 item, string? csrf)
        {
            IUiComponent title = item.WorkId is { } workId
                ? new A(new Truncated(item.WorkTitle ?? item.Title)).Class("list-identity__title").Attribute("href", "/Admin/Works/" + workId.ToString(CultureInfo.InvariantCulture))
                : new Truncated(item.WorkTitle ?? item.Title);
            var identity = new ListIdentity(new IconTile(Glyph("shield"), UiTone.Danger), title, item.MediaTypeId is { } type ? new Text(MediaTypeLabel.Key(type, null)) : new Span());
            var release = new Span(new Truncated(item.Title)).Class("list-value list-value--fill");
            var source = item.ConnectionName is { Length: > 0 } name ? new Span(new Truncated(name)).Class("list-value list-value--fill") : Dim();
            IUiComponent reason = item.ReasonKey is { } key ? new Span(new Text("acquisition.outcome." + key)).Class("list-value") : new Span(new Text("blocked.byHand")).Class("list-value");
            var since = new Span(new LocalTime(item.BlockedAt)).Class("list-value");
            var unblock = new PostForm(Address("Unblock") + "&wantedItemId=" + item.WantedItemId.ToString(CultureInfo.InvariantCulture) + "&acquisitionReleaseId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, [],
                new Button(Glyph("shield-check", 16), new Span(new Text("wanted.release.unblock")).Class("action-dock__text")).Submit());
            return new ListRow([identity, release, source, reason, since, new Div(unblock).Class("list-actions")], [ListPanel.Fact("blocked.release", new Span(new Truncated(item.Title)).Class("list-value")), ListPanel.Fact("wanted.search.reason", reason), ListPanel.Fact("blocked.since", since, Glyph("clock", 14))]).Tinted(UiTone.Danger);
        }

        private static IUiComponent Dim() => new Span(new ValueText("–")).Class("list-value list-value--dim");

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        // Allowed again, the release can be taken by the next search.
        public async Task<IActionResult> OnPostUnblockAsync([BindRequired] long wantedItemId, [BindRequired] long acquisitionReleaseId)
        {
            await mark.ExecuteAsync<UpdateResult>(new WantedReleaseParametersV1(wantedItemId, acquisitionReleaseId), new MarkWantedReleaseDataV1(AcquisitionReleaseStatusType.Allowed), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        protected override async Task LoadDataAsync()
        {
            var (types, subtypes) = Views.Filter;
            _releases = await read.ExecuteAsync<PageResult<BlockedReleaseV1>>(new ReadBlockedReleasesParametersV1(Math.Max(1, PageNumber), 25, types, subtypes), HttpContext.RequestAborted);
        }

        private string Address(string? handler = null) => Views.Address("/Admin/Activity/Blocked", null, [handler is null ? null : "handler=" + handler]);
    }
}
