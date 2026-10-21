using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Backend.Service.User.Profile.V1;
using Jularr.Backend.Service.User.Request.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.User.Requests
{
    // The profile's own requests: one row each with its live steps, narrowed by status and media type, and a side panel for one request with its history and withdrawal.
    public sealed class RequestsModel(ReadAuthenticationV1 authentication, ReadProfileMediaV1 readMedia, ReadRequestsV1 read, ReadRequestStepsV1 readSteps, WithdrawRequestV1 withdraw,
        SaveRequestSourceLinkV1 sourceLink, IAntiforgery antiforgery, UiText texts, FrontendAssets assets) : JularrPage
    {
        private PageResult<RequestDetailsV1>? _requests;
        private IReadOnlyList<ProfileMediaV1> _media = [];
        private IReadOnlyList<RequestStepV1> _steps = [];

        [BindProperty(SupportsGet = true, Name = "state")]
        public RequestListFilterType State { get; set; } = RequestListFilterType.All;

        [BindProperty(SupportsGet = true, Name = "type")]
        public string? TypeSlug { get; set; }

        [BindProperty(SupportsGet = true, Name = "sort")]
        public RequestSortType Sort { get; set; } = RequestSortType.Newest;

        [BindProperty(SupportsGet = true, Name = "view")]
        public string? Layout { get; set; }

        [BindProperty(SupportsGet = true, Name = "p")]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true, Name = "request")]
        public long? RequestId { get; set; }

        private bool Cards => Layout == "cards";

        protected override void Define(Page page)
        {
            page.Title("requests.title");
            page.Navigation("nav.home");
            page.Asset("Framework/Components/LiveProgress.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            page.Asset("Pages/User/Requests/Requests.entry.scss");
            page.Asset("Pages/User/Requests/Requests.entry.ts");
            if (_requests is not { } requests) return;

            var scope = LibraryScopes.Find(TypeSlug);
            page.Content.Add(new Div(Tabs(), new Div(TypeMenu(scope), SortMenu(), Layouts()).Class("requests-tools")).Class("requests-bar"));

            // Active-profile requests only.
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            if (requests.Items.Count == 0)
            {
                var filtered = State != RequestListFilterType.All || scope.Slug != "all";
                page.Content.Add(new Div(new P(new Text("request.empty")), filtered ? new A(new Text("requests.reset")).Class("button button--compact").Attribute("href", Address(RequestListFilterType.All, "all")) : new Span())
                    .Class("requests-empty"));
            }
            else
            {
                page.Content.Add(new Ul([.. requests.Items.Select(item => Row(item, csrf))]).Class(Cards ? "requests-list requests-list--cards" : "requests-list"));
            }

            var paging = new List<IUiComponent>();
            if (requests.Page > 1) paging.Add(new A(new Text("common.previous")).Class("button").Attribute("href", Address(page: requests.Page - 1)));
            if (requests.Items.Count == requests.PageSize) paging.Add(new A(new Text("common.next")).Class("button").Attribute("href", Address(page: requests.Page + 1)));
            if (paging.Count > 0) page.Content.Add(new Nav([.. paging]).Class("page-actions").Attribute("aria-label", texts["library.paging"]));

            if (RequestId is { } selected && requests.Items.FirstOrDefault(item => item.Id == selected) is { } chosen) page.Content.Add(Drawer(chosen, csrf));
            if (FormError is not null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
        }

        // The one row of tabs: how far the requests are.
        private NavigationTabs Tabs() => new([.. Enum.GetValues<RequestListFilterType>().Select(state => new NavigationTab("requests.filter." + state, Address(state), state == State))], "requests.filter", "requests");

        private IUiComponent TypeMenu(LibraryScope current) => Menu("filter", "discover.mediaTypes", "library.scope." + current.Slug,
            [.. LibraryScopes.Listed(_media).Select(scope => new MenuItem("library.scope." + scope.Slug, Address(type: scope.Slug), scope.Slug == current.Slug))]);

        private IUiComponent SortMenu() => Menu("swap", "library.sort", "requests.sort." + Sort,
            [.. Enum.GetValues<RequestSortType>().Select(sort => new MenuItem("requests.sort." + sort, Address(sort: sort), sort == Sort))]);

        // A button that opens a short list of links; each link is a page address, so it works without a script.
        private IUiComponent Menu(string icon, string labelKey, string currentKey, IReadOnlyList<MenuItem> items) =>
            new Framework.Details(
                new Summary(new Icon(icon, assets, 18), new Span(new Text(currentKey)).Class("requests-menu__label")).Class("button button--compact requests-menu__trigger").Attribute("aria-label", texts[labelKey] + ": " + texts[currentKey]),
                new Div([.. items.Select(item => (IUiComponent)(item.Selected
                    ? new A(new Text(item.LabelKey)).Attribute("href", item.Href).Attribute("aria-current", "true")
                    : new A(new Text(item.LabelKey)).Attribute("href", item.Href)))]).Class("requests-menu__panel")).Class("requests-menu");

        private IUiComponent Layouts() => new Div(
            LayoutLink("list", "requests.layout.rows", !Cards, "rows"),
            LayoutLink("grid", "requests.layout.cards", Cards, "cards")).Class("requests-layout").Attribute("role", "group").TextAttribute("aria-label", "requests.layout");

        private IUiComponent LayoutLink(string icon, string labelKey, bool selected, string layout)
        {
            var link = new A(new Icon(icon, assets, 18)).Class("button button--icon button--compact").Attribute("href", Address(layout: layout, page: PageNumber)).TextAttribute("aria-label", labelKey).TextAttribute("title", labelKey);
            return selected ? link.Attribute("aria-current", "true") : link;
        }

        private IUiComponent Row(RequestDetailsV1 item, string? csrf)
        {
            var details = "/Details/" + item.WorkId.ToString(CultureInfo.InvariantCulture);
            var actions = new List<IUiComponent>();
            if (item.AcquisitionRequestStatusTypeId == AcquisitionRequestStatusType.Completed) actions.Add(new A(new Text("common.open")).Class("button button--compact button--primary").Attribute("href", details));
            actions.Add(new A(new Text("media.details")).Class("button button--compact").Attribute("href", Address(request: item.Id, page: PageNumber)).Attribute("aria-label", texts["media.details"] + ": " + item.CanonicalTitle));

            var row = new List<IUiComponent>
            {
                Poster(item, details),
                new Div(
                    new A(new Truncated(item.CanonicalTitle)).Class("request-row__title").Attribute("href", details),
                    new Div(new Truncated(Meta(item))).Class("request-row__meta"),
                    Preferences(item)).Class("request-row__info"),
                new Div(
                    new LiveProgress(LiveKeys.Request(item.Id)).Request(item.AcquisitionRequestStatusTypeId).Steps().Compact(),
                    item is { LatestStepTypeId: { } step, LatestStepAt: { } at }
                        ? new Div(new Truncated(texts["request.step." + step] + " · " + at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))).Class("request-row__step")
                        : new Span()).Class("request-row__status"),
                new Div([.. actions]).Class("request-row__actions")
            };

            // The person passes the source's check in their own browser; the server takes the file link from there.
            if (item is { ManualUrl: { } manual, OperationId: { } operationId }) row.Add(new Div(ManualLink(item.Id, operationId, manual, csrf)).Class("request-row__manual"));
            return new Li([.. row]).Class("request-row");
        }

        private IUiComponent Poster(RequestDetailsV1 item, string href)
        {
            var cover = MediaImage.Or(assets, item.PosterUrl is { } url && IsImageUrl(url) ? url : null, "", 56, 80);
            return new A(cover).Class("request-row__poster").Attribute("href", href).Attribute("tabindex", "-1").Attribute("aria-hidden", "true");
        }

        private static bool IsImageUrl(string url) =>
            url.Length > 1 && (url[0] == '/' && url[1] != '/' || Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0);

        // The type and the day the request was made.
        private string Meta(RequestDetailsV1 item) => LiveFacts.Join(texts["library.scope." + ScopeOf(item).Slug], item.CreatedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture));

        private static LibraryScope ScopeOf(RequestDetailsV1 item) =>
            LibraryScopes.All.FirstOrDefault(scope => scope.Subtype is not null && scope.Subtype == item.MediaSubtypeId) ?? LibraryScopes.All.FirstOrDefault(scope => scope.Type == item.MediaTypeId && scope.Subtype is null) ?? LibraryScopes.All[0];

        private IUiComponent Preferences(RequestDetailsV1 item)
        {
            var tags = new List<IUiComponent> { new Tag(new ValueText(item.RequestedLanguage)) };
            if (item.QualityName is { } quality) tags.Add(new Tag(new ValueText(quality)));
            if (item.ReadsTranslated && texts.CanTranslate()) tags.Add(new Tag(new Text("request.readsTranslated")));
            return new Div([.. tags]).Class("request-row__prefs");
        }

        // The side panel of one request: its progress with every step named, what was asked, what happened, and taking it back.
        private IUiComponent Drawer(RequestDetailsV1 item, string? csrf)
        {
            var facts = new List<IUiComponent>
            {
                new Dt(new Text("table.type")), new Dd(new Text("library.scope." + ScopeOf(item).Slug)),
                new Dt(new Text("request.language")), new Dd(new ValueText(item.RequestedLanguage))
            };
            if (item.QualityName is { } quality) facts.AddRange([new Dt(new Text("request.quality")), new Dd(new ValueText(quality))]);
            facts.AddRange([new Dt(new Text("table.requestedAt")), new Dd(new ValueText(item.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)))]);

            var body = new List<IUiComponent>
            {
                new LiveProgress(LiveKeys.Request(item.Id)).Request(item.AcquisitionRequestStatusTypeId).Steps(),
                new Dl([.. facts]).Class("request-drawer__facts")
            };
            if (_steps.Count > 0)
            {
                body.Add(new Div(new H3(new Text("requests.activity")),
                    new Ul([.. _steps.Select(step => (IUiComponent)new Li(new Span(new Text("request.step." + step.ActivityEventTypeId)),
                        new Span(new ValueText(step.OccurredAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))).Class("request-drawer__time")))]).Class("request-drawer__steps")).Class("request-drawer__section"));
            }

            var footer = new List<IUiComponent>();
            if (item.IsWithdrawable)
            {
                footer.Add(new Framework.Details(new Summary(new Icon("close", assets, 16), new Text("requests.withdraw")).Class("button button--compact"),
                    new Div(new P(new Text("requests.withdraw.confirm")),
                        new PostForm(Address(handler: "Withdraw", page: PageNumber) + "&requestId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, [],
                            new Button(new Text("requests.withdraw.submit")).Primary().Compact().Submit())).Class("request-drawer__confirm")).Class("request-drawer__withdraw"));
            }

            footer.Add(new A(new Text("common.close")).Class("button button--compact").Attribute("href", Address(page: PageNumber)));
            return new Div(
                new A(new ValueText("")).Class("request-drawer__scrim").Attribute("href", Address(page: PageNumber)).Attribute("tabindex", "-1").Attribute("aria-hidden", "true"),
                new Aside(
                    new Header(new Div(new H2(new ValueText(item.CanonicalTitle)).Attribute("id", "request-drawer-title"), new Span(new ValueText(Meta(item))).Class("request-drawer__meta")),
                        new A(new Icon("close", assets, 18)).Class("button button--icon button--compact").Attribute("href", Address(page: PageNumber)).TextAttribute("aria-label", "common.close")).Class("request-drawer__head"),
                    new Div([.. body]).Class("request-drawer__body"),
                    new Div([.. footer]).Class("request-drawer__foot")).Class("request-drawer").Attribute("role", "dialog").Attribute("aria-modal", "true").Attribute("aria-labelledby", "request-drawer-title"))
                .Class("request-drawer-host");
        }

        // The person passes the source's check in their own browser; the server takes the file link from there.
        private IUiComponent ManualLink(long requestId, long operationId, string manual, string? csrf) => new Div(
            new A(new Text("activity.openSource")).Class("button").Attribute("href", manual).Attribute("target", "_blank").Attribute("rel", "noopener noreferrer"),
            new Form<SaveRequestSourceLinkDataV1>(form =>
            {
                form.Hidden(data => data.OperationId);
                form.Text(data => data.SourceLinkUrl, "activity.fileLink");
                form.Submit("activity.continueDownload");
            }, Address(handler: "SourceLink", page: PageNumber) + "&requestId=" + requestId.ToString(CultureInfo.InvariantCulture), csrf, new SaveRequestSourceLinkDataV1(operationId, ""),
                FormError?.Field == nameof(SaveRequestSourceLinkDataV1.SourceLinkUrl) ? FormError : null));

        public async Task<IActionResult> OnPostSourceLinkAsync([BindRequired] long requestId, [BindRequired] long operationId, string? sourceLinkUrl)
        {
            await sourceLink.ExecuteAsync<UpdateResult>(new RequestParametersV1(requestId), new SaveRequestSourceLinkDataV1(operationId, sourceLinkUrl ?? ""), HttpContext.RequestAborted);
            return LocalRedirect(Address(page: PageNumber));
        }

        // Taking a request back: only the person's own, until it is imported.
        public async Task<IActionResult> OnPostWithdrawAsync([BindRequired] long requestId)
        {
            await withdraw.ExecuteAsync<UpdateResult>(new RequestParametersV1(requestId), default, HttpContext.RequestAborted);
            return LocalRedirect(Address(page: PageNumber));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            var profileId = (await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: HttpContext.RequestAborted)).ProfileId
                ?? throw new UnauthorizedAccessException("An active profile is required.");
            _media = await readMedia.ExecuteAsync<BaseDataList<ProfileMediaV1>>(new ReadProfileParametersV1(profileId), HttpContext.RequestAborted);
            var scope = LibraryScopes.Find(TypeSlug);
            _requests = await read.ExecuteAsync<PageResult<RequestDetailsV1>>(new ReadRequestsParametersV1(Math.Max(1, PageNumber), 24, State, Sort, scope.Type, scope.Subtype), HttpContext.RequestAborted);
            if (RequestId is { } selected && _requests.Items.Any(item => item.Id == selected))
            {
                _steps = await readSteps.ExecuteAsync<BaseDataList<RequestStepV1>>(new RequestParametersV1(selected), HttpContext.RequestAborted);
            }
        }

        // The page's own address with what was chosen; defaults stay out of it.
        private string Address(RequestListFilterType? state = null, string? type = null, RequestSortType? sort = null, string? layout = null, int page = 1, long? request = null, string? handler = null)
        {
            var query = new List<string>();
            if (handler is not null) query.Add("handler=" + handler);
            if ((state ?? State) != RequestListFilterType.All) query.Add("state=" + (state ?? State));
            var scope = type ?? LibraryScopes.Find(TypeSlug).Slug;
            if (scope != "all") query.Add("type=" + scope);
            if ((sort ?? Sort) != RequestSortType.Newest) query.Add("sort=" + (sort ?? Sort));
            if ((layout ?? Layout) == "cards") query.Add("view=cards");
            if (page > 1) query.Add("p=" + page.ToString(CultureInfo.InvariantCulture));
            if (request is { } selected) query.Add("request=" + selected.ToString(CultureInfo.InvariantCulture));
            return "/Requests" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        }

        private sealed record MenuItem(string LabelKey, string Href, bool Selected);
    }
}
