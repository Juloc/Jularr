using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Localization.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Localization;
using Jularr.Backend.Service.Admin.Localization.V1;
using ReadUiLocalesV1 = Jularr.Backend.Service.Public.Localization.V1.ReadUiLocalesV1;
using UiLocaleDetailsV1 = Jularr.Backend.Data.Public.Localization.V1.UiLocaleDetailsV1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Localization
{
    public sealed class LocalizationModel(ReadUiLocalesV1 locales, ReadUiMessagesV1 messages, ReadUiMessageV1 message, UpdateUiDescriptionV1 updateDescription,
        SaveUiTranslationV1 translation, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private IReadOnlyList<UiLocaleDetailsV1> _locales = [];
        private PageResult<UiMessageDetailsV1>? _messages;
        private UiMessageDetailsV1? _selected;
        private UpdateUiDescriptionDataV1? _description;
        private SaveUiTranslationDataV1? _translation;

        [BindProperty(SupportsGet = true)]
        public long UiLocaleId { get; set; }

        [BindProperty(SupportsGet = true)]
        public long? UiTranslationMessageId { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("admin.localization.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Localization/Localization.entry.scss");
            page.Content.Add(new A(new Text("admin.languages.title")).Attribute("href", "/Admin/Languages"));

            // Navigate real enabled locales.
            page.Content.Add(new Nav(_locales.Select(locale => (IUiComponent)new A(new ValueText(locale.Name))
                .Attribute("href", Address(locale.Id))).ToArray()).Attribute("aria-label", texts["profile.field.uiLocale"]));

            // Responsive list and editor.
            page.Content.Add(new Split(split =>
            {
                if (_messages is not null)
                {
                    split.Primary.Add(new Table<UiMessageDetailsV1>(_messages.Items)
                        .Text(item => item.Key, "admin.localization.key")
                        .Text(item => item.EnglishText, "admin.localization.english")
                        .Text(item => item.Text, "admin.localization.translation")
                        .Resource(item => item.UiTranslationStatusTypeId, "admin.localization.status", status => "admin.localization.status." + (status ?? UiTranslationStatusType.Missing).ToString().ToLowerInvariant())
                        .Text(item => item.Version, "admin.localization.version")
                        .Link(item => item.Id, "common.actions", id => Address(UiLocaleId, id), "common.edit"));
                    if (PageNumber > 1) split.Primary.Add(new A(new Text("common.previous")).Attribute("href", Address(UiLocaleId, pageNumber: PageNumber - 1)));
                    if (_messages.Items.Count == _messages.PageSize) split.Primary.Add(new A(new Text("common.next")).Attribute("href", Address(UiLocaleId, pageNumber: PageNumber + 1)));
                }

                // Separate source and translation edits.
                if (_selected is not null)
                {
                    split.Secondary.Add(new P(new ValueText(_selected.Key)));
                    split.Secondary.Add(new P(new ValueText(_selected.EnglishText)));
                    split.Secondary.Add(new Form<UpdateUiDescriptionDataV1>(form =>
                    {
                        form.Hidden(item => item.MessageVersion);
                        form.Multiline(item => item.Description, "admin.localization.description");
                        form.Submit("common.save");
                    }, Address(UiLocaleId, _selected.Id, handler: "Description"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                        _description ?? new UpdateUiDescriptionDataV1(_selected.Version, _selected.Description), _description is null ? null : Error));
                    split.Secondary.Add(new Form<SaveUiTranslationDataV1>(form =>
                    {
                        form.Hidden(item => item.MessageVersion);
                        form.Hidden(item => item.TranslationVersion);
                        form.Multiline(item => item.Text, "admin.localization.translation");
                        form.Submit("common.save");
                    }, Address(UiLocaleId, _selected.Id, handler: "Translation"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                        _translation ?? new SaveUiTranslationDataV1(_selected.Version, _selected.TranslationVersion, _selected.Text ?? ""), _translation is null ? null : Error));
                }
            }).MasterDetail(_selected is not null, Address(UiLocaleId)));

            if (Error is not null && _description is null && _translation is null) page.Content.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
            if (TempData["CatalogSaved"] is true) page.Content.Add(new P(new Text("profile.settings.saved")).Attribute("role", "status"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostDescriptionAsync(long messageVersion, string? description)
        {
            _description = new UpdateUiDescriptionDataV1(messageVersion, description);
            await updateDescription.ExecuteAsync<UpdateResult>(new UpdateUiDescriptionParametersV1(SelectedParameters().UiTranslationMessageId), _description, HttpContext.RequestAborted);
            TempData["CatalogSaved"] = true;
            return LocalRedirect(Address(UiLocaleId, UiTranslationMessageId));
        }

        public async Task<IActionResult> OnPostTranslationAsync(long messageVersion, long? translationVersion, string? text)
        {
            _translation = new SaveUiTranslationDataV1(messageVersion, translationVersion, text ?? "");
            await translation.ExecuteAsync<UpdateResult>(SelectedParameters(), _translation, HttpContext.RequestAborted);
            TempData["CatalogSaved"] = true;
            return LocalRedirect(Address(UiLocaleId, UiTranslationMessageId));
        }

        private UiMessageParametersV1 SelectedParameters() => new(UiTranslationMessageId ?? throw new KeyNotFoundException("No catalog message was selected."), UiLocaleId);

        protected override async Task LoadDataAsync()
        {
            var token = HttpContext.RequestAborted;
            _locales = await locales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: token);
            if (UiLocaleId == 0) UiLocaleId = _locales.FirstOrDefault()?.Id ?? throw new KeyNotFoundException("No enabled locale is available.");
            _messages = await messages.ExecuteAsync<PageResult<UiMessageDetailsV1>>(new ReadUiMessagesParametersV1(UiLocaleId, PageNumber), token);
            if (UiTranslationMessageId is not null) _selected = await message.ExecuteAsync<UiMessageDetailsV1>(SelectedParameters(), token);
        }

        private static string Address(long uiLocaleId, long? messageId = null, int pageNumber = 1, string? handler = null)
        {
            var address = "/Admin/Localization?UiLocaleId=" + uiLocaleId.ToString(CultureInfo.InvariantCulture) + "&PageNumber=" + pageNumber.ToString(CultureInfo.InvariantCulture);
            if (messageId is not null) address += "&UiTranslationMessageId=" + messageId.Value.ToString(CultureInfo.InvariantCulture);
            if (handler is not null) address += "&handler=" + handler;
            return address;
        }
    }
}
