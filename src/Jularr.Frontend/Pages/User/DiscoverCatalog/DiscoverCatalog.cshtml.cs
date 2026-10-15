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
    public sealed class DiscoverCatalogModel(RequestDiscoverCatalogItemV1 request, ReadDiscoverAnimeV1 readAnime, ReadUiLocalesV1 locales, IAntiforgery antiforgery, UiText texts)
        : JularrPage
    {
        private DiscoverAnimeV1? _anime;
        private BaseDataList<UiLocaleDetailsV1>? _locales;
        private CreateRequestDataV1? _input;

        [BindProperty(SupportsGet = true)]
        public string Type { get; set; } = "";

        [BindProperty(SupportsGet = true)]
        public string Source { get; set; } = "";

        [BindProperty(SupportsGet = true)]
        public string ExternalId { get; set; } = "";

        [BindProperty(SupportsGet = true, Name = "title")]
        public string? TitleText { get; set; }

        [BindProperty(SupportsGet = true, Name = "meta")]
        public string? Meta { get; set; }

        protected override void Define(Page page)
        {
            page.Title("details.title");
            page.Navigation("nav.home");
            page.Asset("Pages/User/DiscoverItem/DiscoverItem.entry.scss");
            page.Content.Add(_anime is { } anime
                ? new MediaHero(anime.Title, anime.BackdropUrl, anime.ReleaseYear?.ToString(CultureInfo.InvariantCulture), anime.Overview)
                : new MediaHero(TitleText ?? ExternalId, null, Meta, null, null, null));
            if (_anime?.TrailerKey is { } trailer) page.Content.Add(new Div(new TrailerLink(trailer)).Class("discover-item__trailer"));
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

            // A season that already is part of a Work opens the series.
            if (_anime?.WorkId is { } workId)
            {
                return LocalRedirect("/Details/" + workId.ToString(CultureInfo.InvariantCulture));
            }

            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] long uiLocaleId)
        {
            _input = new CreateRequestDataV1(uiLocaleId);
            var scope = Scope();
            await request.ExecuteAsync<CreateRequestResultV1>(new DiscoverParametersV1(), new RequestDiscoverCatalogDataV1(scope.Type, Source, ExternalId, uiLocaleId, scope.Subtype),
                HttpContext.RequestAborted);
            return RedirectToPage("/User/Requests/Requests");
        }

        protected override async Task LoadDataAsync()
        {
            if (Scope().Subtype == MediaSubtype.Anime && Source == "anilist" && int.TryParse(ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var aniListId))
            {
                _anime = await readAnime.ExecuteAsync<DiscoverAnimeV1>(new DiscoverAnimeParametersV1(aniListId), default(NoData), HttpContext.RequestAborted);
            }

            if (texts.CanRequest())
            {
                _locales = await locales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: HttpContext.RequestAborted);
            }
        }

        private DiscoverType Scope() => DiscoverTypes.Find(Type) is { IsCatalog: true } scope ? scope : throw new KeyNotFoundException("Unknown media type.");

        private string Address() => "/Discover/" + Uri.EscapeDataString(Type) + "/" + Uri.EscapeDataString(Source) + "/" + Uri.EscapeDataString(ExternalId)
            + "?title=" + Uri.EscapeDataString(TitleText ?? "") + (Meta is null ? "" : "&meta=" + Uri.EscapeDataString(Meta));
    }
}
