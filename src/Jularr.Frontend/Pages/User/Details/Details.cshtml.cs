using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
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
    public sealed class DetailsModel(ReadWorkV1 read, ReadWorkAvailabilityV1 readAvailability, UpdateWatchlistV1 update, CreateRequestV1 request, ReadUiLocalesV1 locales,
        IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private WorkDetailsV1? _work;
        private PageResult<WorkAvailabilityV1>? _availability;
        private BaseDataList<UiLocaleDetailsV1>? _locales;
        private CreateRequestDataV1? _requestInput;

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        protected override void Define(Page page)
        {
            page.Title("details.title");
            page.Navigation("nav.home");
            page.Asset("Pages/User/Details/Details.entry.scss");

            // Persisted media identity.
            if (_work is { } work)
            {
                var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                // Plex plays what Jularr cannot play here.
                var plex = work.PlexUrl is not null && (texts.Capabilities?.CanPlay is not true || _availability?.Items.Any(item => item.IsAvailable) is not true) ? work.PlexUrl : null;
                page.Content.Add(new MediaHero(work.CanonicalTitle, work.BackdropUrl, string.Join(" · ", new[] { texts["media.type." + work.MediaTypeId], work.ReleaseYear?.ToString(CultureInfo.InvariantCulture) }
                    .Where(part => part is not null)), work.Overview, plex is null ? null : "media.openInPlex", plex));
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

                // Stored inventory, no storage access.
                if (_availability is { Items.Count: > 0 } availability)
                {
                    page.Content.Add(new UiSection("details.availability", [new Table<WorkAvailabilityV1>(availability.Items)
                        .Content("details.unit", item => new ValueText(MediaUnitLabel.Format(texts, item.WorkUnitTypeId, item.UnitNumber, item.ParentUnitNumber) ?? texts["details.wholeWork"]))
                        .Resource(item => item.MediaAssetTypeId, "table.type", type => "media.asset." + type)
                        .Content("details.quality", item => new ValueText(item.Height is { } height ? height.ToString(CultureInfo.InvariantCulture) + "p" : ""))
                        .Content("details.audio", item => new ValueText(string.Join(", ", item.AudioLanguages)))
                        .Content("details.subtitles", item => new ValueText(string.Join(", ", item.SubtitleLanguages)))
                        .Content("details.duration", item => new ValueText(item.DurationMs is { } duration ? TimeSpan.FromMilliseconds(duration).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : ""))
                        .Content("table.status", item => new Text(item.IsAvailable ? "library.present" : "library.missing"))
                        .Content("common.actions", Play)]));
                    if (availability.Items.Count == availability.PageSize) page.Content.Add(new P(new Text("details.availabilityMore")));
                }
            }
            else if (Error is not null) page.Content.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
        }

        private IUiComponent Play(WorkAvailabilityV1 item)
        {
            // Books open in the reader.
            if (item.MediaAssetTypeId == MediaAssetType.Ebook)
            {
                return new A(new Text("details.read")).Attribute("href", "/Read/" + item.Id.ToString(CultureInfo.InvariantCulture));
            }

            // Continue where this profile stopped.
            if (!item.IsAvailable || texts.Capabilities?.CanPlay is not true)
            {
                return new Span();
            }

            var resume = item.ResumePositionMs is > 0 ? TimeSpan.FromMilliseconds(item.ResumePositionMs.Value).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : null;
            var label = resume is null ? new Text(item.IsCompleted ? "details.playAgain" : "details.play") : (IUiComponent)new Span(new Text("details.resume"), new ValueText(" " + resume));
            return new A(label).Attribute("href", "/Player/" + item.Id.ToString(CultureInfo.InvariantCulture));
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
            _availability = await readAvailability.ExecuteAsync<PageResult<WorkAvailabilityV1>>(new ReadWorkAvailabilityParametersV1(Id, pageSize: PageRequest.MaximumPageSize), HttpContext.RequestAborted);
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
