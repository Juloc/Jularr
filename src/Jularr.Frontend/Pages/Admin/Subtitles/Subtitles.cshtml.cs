using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Library.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Subtitles
{
    // Subtitles of one video searched at the providers and chosen by hand.
    public sealed class SubtitlesModel(ReadSubtitleFileV1 read, SearchSubtitlesV1 search, DownloadSubtitleV1 download, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private SubtitleFileV1? _file;
        private SearchSubtitlesDataV1? _input;
        private IReadOnlyList<SubtitleOfferV1>? _offers;

        [BindProperty(SupportsGet = true)]
        public long StoredFileId { get; set; }

        protected override void Define(Page page)
        {
            page.Title("details.subtitles");
            page.Navigation("nav.admin");
            if (_file is not { } file)
            {
                if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
                return;
            }

            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            page.Content.Add(new P(new ValueText(file.RelativePath)));
            page.Content.Add(new P(new Text("subtitles.present"), new ValueText(" " + (file.PresentLanguages.Length == 0 ? "–" : string.Join(", ", file.PresentLanguages)))));
            if (TempData["SubtitleSaved"] is bool saved)
            {
                page.Content.Add(new P(new Text(saved ? "subtitles.saved" : "subtitles.exists")).Attribute("role", "status"));
            }

            // The first wanted language the file lacks is asked first.
            var missing = file.WantedLanguages.FirstOrDefault(language => !file.PresentLanguages.Contains(language, StringComparer.OrdinalIgnoreCase)) ?? file.WantedLanguages.FirstOrDefault() ?? "";
            page.Content.Add(new Form<SearchSubtitlesDataV1>(form =>
            {
                form.Text(item => item.Language, "subtitles.language");
                form.Submit("subtitles.search");
            }, Address("Search"), csrf, _input ?? new SearchSubtitlesDataV1(missing), _offers is null ? FormError : null));

            if (_offers is { } offers)
            {
                page.Content.Add(offers.Count == 0 ? new P(new Text("subtitles.none")).Attribute("role", "status") : new Table<SubtitleOfferV1>(offers)
                    .Text(offer => offer.ProviderName, "subtitles.provider")
                    .Content("subtitles.release", offer => new ValueText(offer.ReleaseName ?? ""))
                    .Content("subtitles.hearingImpaired", offer => new Text(offer.IsHearingImpaired ? "common.yes" : "common.no"))
                    .Content("subtitles.downloads", offer => new ValueText(offer.Downloads.ToString(CultureInfo.CurrentCulture)))
                    .Content("common.actions", offer => new Form<DownloadSubtitleDataV1>(form =>
                    {
                        form.Hidden(item => item.OfferId);
                        form.Submit("subtitles.download", offer.ReleaseName ?? offer.ProviderName, valueHidden: true);
                    }, Address("Download"), csrf, new DownloadSubtitleDataV1(offer.OfferId))));
            }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostSearchAsync(string? language)
        {
            _input = new SearchSubtitlesDataV1(language ?? "");
            await LoadDataAsync();
            _offers = await search.ExecuteAsync<BaseDataList<SubtitleOfferV1>>(new StoredFileParametersV1(StoredFileId), _input, HttpContext.RequestAborted);
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostDownloadAsync([BindRequired] string offerId)
        {
            TempData["SubtitleSaved"] = (await download.ExecuteAsync<UpdateResult>(new StoredFileParametersV1(StoredFileId), new DownloadSubtitleDataV1(offerId), HttpContext.RequestAborted)).Success;
            return LocalRedirect(Address());
        }

        protected override async Task LoadDataAsync()
        {
            _file = await read.ExecuteAsync<SubtitleFileV1>(new StoredFileParametersV1(StoredFileId), HttpContext.RequestAborted);
        }

        private string Address(string? handler = null) =>
            "/Admin/Library/Subtitles/" + StoredFileId.ToString(CultureInfo.InvariantCulture) + (handler is null ? "" : "?handler=" + handler);
    }
}
