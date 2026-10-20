using Jularr.Backend.Data.Admin.Speech.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.User.Speech.V1;
using Jularr.Backend.Service.Admin.Speech.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Speech
{
    public sealed class SpeechModel(ReadSpeechModelsV1 read, UpdateSpeechModelV1 update, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private IReadOnlyList<SpeechModelV1> _models = [];
        private string? _hash;

        protected override void Define(Page page)
        {
            page.Title("speech.models.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Speech/Speech.entry.scss");
            page.Content.Add(new A(new Text("admin.instance.modules")).Attribute("href", "/Admin/Instance"));
            page.Content.Add(new P(new Text("speech.models.explanation")));
            if (_models.Count == 0)
            {
                page.Content.Add(new P(new Text("speech.models.empty")).Attribute("role", "status"));
            }
            else
            {
                page.Content.Add(new Table<SpeechModelV1>(_models)
                    .Text(item => item.Name, "speech.model")
                    .Text(item => item.Language, "speech.language")
                    .Content("speech.license", item => new A(new ValueText(item.License)).Attribute("href", item.LicenseUrl).Attribute("target", "_blank").Attribute("rel", "noopener"))
                    .Content("speech.size", item => new ValueText(ByteSize.Format(item.SizeBytes)))
                    .Content("table.status", item => new Text(item.IsEnabled ? "speech.models.active" : "speech.models.inactive"))
                    .Content("speech.models.metadata", item => new Details(new Summary(new Text("speech.models.metadata")),
                        new P(new ValueText(item.Id + " / " + item.Version)),
                        new P(new Text("speech.voice"), new ValueText(": " + item.Voice)),
                        new P(new ValueText(item.Sha256)),
                        new A(new Text("speech.models.source")).Attribute("href", item.Source).Attribute("target", "_blank").Attribute("rel", "noopener")).Class("speech-models__metadata"))
                    .Content("common.actions", item => texts.Can("instance.settings.update", "Reading")
                        ? new Form<UpdateSpeechModelDataV1>(form =>
                        {
                            form.Hidden(value => value.IsEnabled);
                            form.Hidden(value => value.Version);
                            form.Submit(item.IsEnabled ? "speech.models.deactivate" : "speech.models.activate");
                        }, "/Admin/Instance/Speech?hash=" + item.Sha256, antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                            new UpdateSpeechModelDataV1(!item.IsEnabled, item.Revision), _hash == item.Sha256 ? FormError : null)
                        : new Span()));
            }

            if (FormError is not null && _hash is null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string hash, [BindRequired] bool isEnabled, long version)
        {
            _hash = hash;
            await update.ExecuteAsync<UpdateResult>(new SpeechModelParametersV1(hash), new UpdateSpeechModelDataV1(isEnabled, version), HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Speech/Speech");
        }

        protected override async Task LoadDataAsync() => _models = await read.ExecuteAsync<BaseDataList<SpeechModelV1>>(cancellationToken: HttpContext.RequestAborted);
    }
}
