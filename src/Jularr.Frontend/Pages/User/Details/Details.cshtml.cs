using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.User.Media.V1;
using Jularr.Backend.Service.User.Request.V1;
using Jularr.Backend.Service.Public.Localization.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.User.Details
{
    public sealed class DetailsModel(ReadWorkV1 read, UpdateWatchlistV1 update, CreateRequestV1 request, ReadUiLocalesV1 locales, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private WorkDetailsV1? _work;
        private BaseDataList<UiLocaleDetailsV1>? _locales;
        private CreateRequestDataV1? _requestInput;

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        protected override void Define(Page page)
        {
            page.Title("details.title");
            page.Navigation("nav.home");

            // Persisted media identity.
            if (_work is { } work)
            {
                var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                page.Content.Add(new P(new ValueText(work.CanonicalTitle)));
                page.Content.Add(new P(new Text("media.type." + work.MediaTypeId)));
                page.Content.Add(new Form<UpdateWatchlistDataV1>(form =>
                {
                    form.Hidden(item => item.IsWatchlisted);
                    form.Submit(work.IsWatchlisted ? "media.watchlist.remove" : "media.watchlist.add");
                }, "/Details/" + work.Id.ToString(CultureInfo.InvariantCulture), csrf,
                    new UpdateWatchlistDataV1(!work.IsWatchlisted), Error));

                // Explicit desired content language.
                if (_locales is { } languages)
                {
                    var options = languages.Select(locale => new SelectOption(locale.Id.ToString(CultureInfo.InvariantCulture), locale.Name));
                    var selected = languages.FirstOrDefault(locale => string.Equals(locale.Locale, texts.Locale, StringComparison.OrdinalIgnoreCase));
                    page.Content.Add(new Form<CreateRequestDataV1>(form =>
                    {
                        form.Select(item => item.UiLocaleId, "request.language", options);
                        form.Submit("request.create");
                    }, "/Details/" + work.Id.ToString(CultureInfo.InvariantCulture) + "?handler=Request", csrf,
                        _requestInput ?? (selected is null ? null : new CreateRequestDataV1(selected.Id)), _requestInput is null ? null : Error));
                }
            }
            else if (Error is not null) page.Content.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] bool isWatchlisted)
        {
            await update.ExecuteAsync<UpdateResult>(new WorkParametersV1(Id), new UpdateWatchlistDataV1(isWatchlisted), HttpContext.RequestAborted);
            return RedirectToPage("/User/Details/Details", new { id = Id });
        }

        protected override async Task LoadDataAsync()
        {
            _work = await read.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(Id), HttpContext.RequestAborted);
            if (texts.Capabilities?.CanRequest is true)
            {
                _locales = await locales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: HttpContext.RequestAborted);
            }
        }

        public async Task<IActionResult> OnPostRequestAsync([BindRequired] long uiLocaleId)
        {
            _requestInput = new CreateRequestDataV1(uiLocaleId);
            await request.ExecuteAsync<CreateRequestResultV1>(new CreateRequestParametersV1(Id), _requestInput, HttpContext.RequestAborted);
            return RedirectToPage("/User/Requests/Requests");
        }
    }
}
