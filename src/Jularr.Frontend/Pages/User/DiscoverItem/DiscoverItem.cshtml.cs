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

namespace Jularr.Frontend.Pages.User.DiscoverItem
{
    public sealed class DiscoverItemModel(ReadDiscoverScopesV1 readScopes, ReadDiscoverItemV1 read, RequestDiscoverItemV1 request, ReadUiLocalesV1 locales, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private DiscoverItemV1? _item;
        private BaseDataList<UiLocaleDetailsV1>? _locales;
        private CreateRequestDataV1? _input;

        [BindProperty(SupportsGet = true)]
        public string Type { get; set; } = "";

        [BindProperty(SupportsGet = true)]
        public int TmdbId { get; set; }

        protected override void Define(Page page)
        {
            page.Title("details.title");
            page.Navigation("nav.home");
            page.Asset("Pages/User/DiscoverItem/DiscoverItem.entry.scss");
            if (_item is not { } item)
            {
                if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
                return;
            }

            // A provider title; it becomes a Work only through a request.
            page.Content.Add(new MediaHero(item.Title, item.BackdropUrl, item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), item.Overview,
                item.WorkId is null ? null : "media.details", item.WorkId is { } workId ? "/Details/" + workId.ToString(CultureInfo.InvariantCulture) : null));
            page.Content.Add(new P(new Text("media.state." + item.MediaStateTypeId)).Attribute("role", "status"));

            if (item.MediaStateTypeId == MediaStateType.NotRequested && _locales is { } languages)
            {
                var options = languages.Select(locale => new SelectOption(locale.Id.ToString(CultureInfo.InvariantCulture), locale.Name));
                var selected = languages.FirstOrDefault(locale => string.Equals(locale.Locale, texts.Locale, StringComparison.OrdinalIgnoreCase));
                page.Content.Add(new Form<CreateRequestDataV1>(form =>
                {
                    form.Select(data => data.UiLocaleId, "request.language", options);
                    form.Submit("request.create");
                }, Address(), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, _input ?? (selected is null ? null : new CreateRequestDataV1(selected.Id)), FormError));
            }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            var scopes = await readScopes.ExecuteAsync<BaseDataList<DiscoverScopeV1>>(new DiscoverParametersV1(), HttpContext.RequestAborted);
            var scope = scopes.FirstOrDefault(item => item.Slug == Type && !item.IsCatalog)
                ?? throw new KeyNotFoundException("Unknown media category.");
            var source = scope.MediaTypeId == MediaType.Movie ? "tmdb_movie" : "tmdb_tv";
            return LocalRedirect("/Details?source=" + source
                + "&externalId=" + TmdbId.ToString(CultureInfo.InvariantCulture)
                + "&type=" + Uri.EscapeDataString(Type));
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] long uiLocaleId)
        {
            _input = new CreateRequestDataV1(uiLocaleId);
            await request.ExecuteAsync<CreateRequestResultV1>(await ParametersAsync(), _input, HttpContext.RequestAborted);
            return RedirectToPage("/User/Requests/Requests");
        }

        protected override async Task LoadDataAsync()
        {
            _item = await read.ExecuteAsync<DiscoverItemV1>(await ParametersAsync(), default, HttpContext.RequestAborted);
            if (texts.CanRequest())
            {
                _locales = await locales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: HttpContext.RequestAborted);
            }
        }

        private async Task<DiscoverItemParametersV1> ParametersAsync() =>
            new((await readScopes.ExecuteAsync<BaseDataList<DiscoverScopeV1>>(new DiscoverParametersV1(), HttpContext.RequestAborted))
                .FirstOrDefault(scope => scope.Slug == Type && !scope.IsCatalog)?.MediaTypeId ?? throw new KeyNotFoundException("Unknown media type."), TmdbId);

        private string Address() => "/Discover/" + Type + "/" + TmdbId.ToString(CultureInfo.InvariantCulture);
    }
}
