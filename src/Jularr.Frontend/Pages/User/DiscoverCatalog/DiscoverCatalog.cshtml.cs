using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Backend.Service.Public.Localization.V1;
using Jularr.Backend.Service.User.Discover.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.User.DiscoverCatalog
{
    // A catalog title from Discover; the request imports it as a Work, an anime season as its TMDB series.
    public sealed class DiscoverCatalogModel(RequestDiscoverCatalogItemV1 request, ReadDiscoverScopesV1 readScopes, ReadDiscoverCatalogEntryV1 readEntry, ReadUiLocalesV1 locales,
        IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        // Narō's catalog name: its titles are web novels, not published light novels.
        public const string WebNovelSource = "syosetu";

        private DiscoverScopeV1? _scope;
        private DiscoverCatalogEntryV1? _entry;
        private BaseDataList<UiLocaleDetailsV1>? _locales;
        private CreateRequestDataV1? _input;

        [BindProperty(SupportsGet = true)]
        public string Type { get; set; } = "";

        [BindProperty(SupportsGet = true)]
        public string Source { get; set; } = "";

        [BindProperty(SupportsGet = true)]
        public string ExternalId { get; set; } = "";

        protected override void Define(Page page)
        {
            page.Title("details.title");
            page.Navigation("nav.home");
            page.Asset("Pages/User/DiscoverItem/DiscoverItem.entry.scss");
            if (_entry is not { } entry) return;
            // A Narō web novel names itself and its ncode, apart from any published edition.
            var source = Source == WebNovelSource ? texts["discover.webNovel"] + " " + ExternalId : null;
            var meta = string.Join(" · ", new[] { entry.ReleaseYear?.ToString(CultureInfo.InvariantCulture), entry.Author, source }.Where(value => !string.IsNullOrWhiteSpace(value)));
            page.Content.Add(new MediaHero(entry.Title, entry.BackdropUrl, meta.Length == 0 ? null : meta, entry.Overview));
            if (entry.TrailerKey is { } trailer) page.Content.Add(new Div(new TrailerLink(trailer)).Class("discover-item__trailer"));
            page.Content.Add(new P(new Text("media.state." + MediaStateType.NotRequested)).Attribute("role", "status"));

            if (_locales is { } languages)
            {
                var options = languages.Select(locale => new SelectOption(locale.Id.ToString(CultureInfo.InvariantCulture), locale.Name));
                var selected = languages.FirstOrDefault(locale => string.Equals(locale.Locale, texts.Locale, StringComparison.OrdinalIgnoreCase));
                page.Content.Add(new Form<CreateRequestDataV1>(form =>
                {
                    form.Select(data => data.UiLocaleId, "request.language", options);
                    form.Submit("request.create");
                }, Address(), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, _input ?? (selected is null ? null : new CreateRequestDataV1(selected.Id)), FormError));
            }
            else if (FormError is not null)
            {
                page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
            }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();

            // A title that already is a Work, or a season of one, opens it.
            if (_entry?.WorkId is { } workId)
            {
                return LocalRedirect("/Details/" + workId.ToString(CultureInfo.InvariantCulture));
            }

            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] long uiLocaleId)
        {
            _input = new CreateRequestDataV1(uiLocaleId);
            var scope = await ScopeAsync();
            await request.ExecuteAsync<CreateRequestResultV1>(new DiscoverParametersV1(), new RequestDiscoverCatalogDataV1(scope.MediaTypeId, Source, ExternalId, uiLocaleId, scope.MediaSubtypeId),
                HttpContext.RequestAborted);
            return RedirectToPage("/User/Requests/Requests");
        }

        protected override async Task LoadDataAsync()
        {
            var scope = await ScopeAsync();
            _entry = await readEntry.ExecuteAsync<DiscoverCatalogEntryV1>(new DiscoverParametersV1(),
                new ReadDiscoverCatalogEntryDataV1(scope.MediaTypeId, Source, ExternalId, scope.MediaSubtypeId), HttpContext.RequestAborted);

            if (texts.CanRequest())
            {
                _locales = await locales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: HttpContext.RequestAborted);
            }
        }

        private async Task<DiscoverScopeV1> ScopeAsync() => _scope ??=
            (await readScopes.ExecuteAsync<BaseDataList<DiscoverScopeV1>>(new DiscoverParametersV1(), HttpContext.RequestAborted)).FirstOrDefault(scope => scope.Slug == Type && scope.IsCatalog)
            ?? throw new KeyNotFoundException("Unknown media type.");

        private string Address() => "/Discover/" + Uri.EscapeDataString(Type) + "/" + Uri.EscapeDataString(Source) + "/" + Uri.EscapeDataString(ExternalId);
    }
}
