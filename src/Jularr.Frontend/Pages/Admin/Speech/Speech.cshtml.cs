using Jularr.Backend.Data.Admin.Speech.V1;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.User.Speech.V1;
using Jularr.Backend.Service.Admin.Speech.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Globalization;
using Jularr.Backend.Data.Enums.Speech;

namespace Jularr.Frontend.Pages.Admin.Speech
{
    public sealed class SpeechModel(ReadSpeechModelsV1 read, UpdateSpeechModelV1 update, RefreshSpeechModelsV1 refresh, IAntiforgery antiforgery, UiText texts, FrontendAssets assets) : JularrPage
    {
        private const string Home = "/Admin/Instance/Speech";
        private PageResult<SpeechModelV1>? _models;
        private IReadOnlyList<string> _languages = [];
        private string? _hash;
        [BindProperty(SupportsGet = true, Name = "q")]
        public string? Query { get; set; }
        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;
        [BindProperty(SupportsGet = true)]
        public string? ModelHash { get; set; }
        [BindProperty(SupportsGet = true)]
        public string? Language { get; set; }
        [BindProperty(SupportsGet = true)]
        public SpeechVoiceGenderType? Gender { get; set; }
        [BindProperty(SupportsGet = true)]
        public bool? IsInstalled { get; set; }

        protected override void Define(Page page)
        {
            page.Title("speech.models.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Speech/Speech.entry.scss");
            page.Asset("Pages/Admin/Speech/Speech.entry.ts");
            page.Asset("Framework/Components/Drawer.entry.ts");
            page.Asset("Framework/Components/ChoiceSelect.entry.ts");
            page.Content.Add(new Div(new ListToolbar(new A(new Text("admin.instance.modules")).Attribute("href", "/Admin/Instance/Modules"),
                new SearchBox(Home, "speech.models.search", Query, keep: Filters(), icon: new Icon("search", assets, 18)),
                new ChoiceSelect(Home, "language", "speech.language", [new("speech.models.allLanguages", "", Language is null),
                    .. _languages.Select(language => new ChoiceOption("speech.language", language, language == Language, CultureInfo.GetCultureInfo(language).DisplayName + " · " + language))], FilterScope("language")),
                new ChoiceSelect(Home, "gender", "speech.models.gender", [new("speech.models.allGenders", "", Gender is null),
                    .. Enum.GetValues<SpeechVoiceGenderType>().Select(gender => new ChoiceOption(GenderKey(gender), ((byte)gender).ToString(CultureInfo.InvariantCulture), Gender == gender))], FilterScope("gender")),
                new ChoiceSelect(Home, "isInstalled", "speech.models.installation", [new("speech.models.allInstallations", "", IsInstalled is null),
                    new("speech.models.installed", "true", IsInstalled == true), new("speech.models.notInstalled", "false", IsInstalled == false)], FilterScope("isInstalled")))).Class("speech-models-toolbar"));
            page.Content.Add(new P(new Text("speech.models.explanation")));
            if (_models is { Items.Count: 0 })
            {
                page.Content.Add(new P(new Text("speech.models.empty")).Attribute("role", "status"));
            }
            else if (_models is not null)
            {
                page.Content.Add(new ListPanel("speech.models.title",
                    [new("speech.model", ListRole.Main), new("speech.language", HideLevel: 1), new("speech.size", HideLevel: 1), new("table.status", ListRole.Status), new("common.actions", ListRole.Actions)],
                    _models.Items.Select(item => new ListRow([
                        new ListIdentity(new IconTile(new Icon("headphones", assets, 20)), new A(new ValueText(item.Name)).Class("list-identity__title")
                            .Attribute("href", Address() + "&modelHash=" + item.Sha256), new Span(License(item), new ValueText(" · "), new Text(GenderKey(item.Gender)))),
                        new ValueText(item.Language), new ValueText(ByteSize.Format(item.SizeBytes)),
                        new Div(new StateChip(new Text(item.IsInstalled ? "speech.models.installed" : "speech.models.notInstalled"), item.IsInstalled ? UiTone.Success : UiTone.Neutral,
                            new Icon(item.IsInstalled ? "check" : "download", assets, 14)), new Span(new Text(item.IsEnabled ? "speech.models.active" : "speech.models.inactive"))),
                        new Div(Sample(item), item.RequiresLicenseAcceptance && !item.IsEnabled
                            ? new A(new Text("speech.models.review")).Class("button button--compact").Attribute("href", Address() + "&modelHash=" + item.Sha256)
                            : Activation(item),
                            new A(new Icon("info", assets, 16), new Span(new Text("speech.models.metadata")).Class("visually-hidden"))
                                .Class("button button--icon").Attribute("href", Address() + "&modelHash=" + item.Sha256)).Class("list-actions")
                    ], [new ValueText(item.Language + " · " + ByteSize.Format(item.SizeBytes))])).ToArray()).Modifier("speech-models"));

                if (_models.Items.FirstOrDefault(item => item.Sha256 == ModelHash) is { } selected)
                {
                    page.Content.Add(new Drawer(assets, "speech-model", [new DrawerPane("package", "speech.models.metadata", "speech.models.metadata", "headphones",
                        [Drawer.Facts(("speech.language", new ValueText(selected.Language)), ("speech.size", new ValueText(ByteSize.Format(selected.SizeBytes))),
                            ("speech.voice", new ValueText(selected.Voice)), ("speech.license", License(selected)),
                            ("speech.models.gender", selected.GenderSource is null ? new Text(GenderKey(selected.Gender)) : new A(new Text(GenderKey(selected.Gender)))
                                .Attribute("href", selected.GenderSource).Attribute("target", "_blank").Attribute("rel", "noopener noreferrer"))),
                            new P(new ValueText(selected.Id + " / " + selected.Version)), new P(new ValueText(selected.Sha256)).Class("speech-models__metadata"),
                            new A(new Text("speech.models.source")).Attribute("href", selected.Source).Attribute("target", "_blank").Attribute("rel", "noopener noreferrer"),
                            new P(new Text("speech.models.deviceNotice")), Activation(selected)], [], TitleValue: selected.Name)], sheet: true, closeHref: Address(), open: true));
                }
            }

            var actions = new List<IUiComponent>();
            if (texts.Can("instance.settings.update", "Reading")) actions.Add(new PostForm(Address() + "&handler=Refresh", antiforgery.GetAndStoreTokens(HttpContext).RequestToken, [],
                new Button(new Icon("sync", assets, 18), new Text("speech.models.refresh")).Submit()));
            page.Content.Add(new ActionDock(assets, "speech.models.title", actions, paging: _models is null ? null : new DockPaging(
                _models.Page > 1 ? Address(_models.Page - 1) : null, _models.Items.Count == _models.PageSize ? Address(_models.Page + 1) : null,
                new ValueText(string.Format(CultureInfo.CurrentCulture, texts["speech.models.page"], _models.Page)))));

            if (FormError is not null && _hash is null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        private static IUiComponent License(SpeechModelV1 item) => new A(item.RequiresLicenseAcceptance ? new Text("speech.models.review") : new ValueText(item.License))
            .Attribute("href", item.LicenseUrl).Attribute("target", "_blank").Attribute("rel", "noopener noreferrer");

        private static string GenderKey(SpeechVoiceGenderType gender) => gender switch
        {
            SpeechVoiceGenderType.Male => "speech.models.male",
            SpeechVoiceGenderType.Female => "speech.models.female",
            _ => "speech.models.unknownGender"
        };

        private IUiComponent Sample(SpeechModelV1 item) => item.SampleUrl is null ? new Span(new Text("speech.models.noSample"))
            : new Div(new Button(new Icon("play", assets, 16), new Span(new Text("speech.models.samplePlay")).Attribute("data-sample-label", "")).Compact()
                .Attribute("data-speech-sample", item.SampleUrl).Attribute("aria-pressed", "false")
                .TextAttribute("data-play", "speech.models.samplePlay").TextAttribute("data-stop", "speech.models.sampleStop").TextAttribute("data-failed", "speech.models.sampleFailed"),
                new Span().Attribute("data-sample-status", "").Attribute("role", "status")).Class("speech-models__sample");

        private IUiComponent Activation(SpeechModelV1 item) => !texts.Can("instance.settings.update", "Reading") ? new Span()
            : new Form<UpdateSpeechModelDataV1>(form =>
            {
                form.Hidden(value => value.IsEnabled);
                form.Hidden(value => value.Version);
                if (!item.IsEnabled && item.RequiresLicenseAcceptance) form.Toggle(value => value.AcceptLicense, "speech.models.acceptLicense");
                form.Submit(item.IsEnabled ? "speech.models.deactivate" : "speech.models.activate");
            }, Address() + "&hash=" + item.Sha256, antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                new UpdateSpeechModelDataV1(!item.IsEnabled, item.Revision), _hash == item.Sha256 ? FormError : null);

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string hash, [BindRequired] bool isEnabled, long version, bool acceptLicense = false)
        {
            _hash = hash;
            ModelHash = hash;
            await update.ExecuteAsync<UpdateResult>(new SpeechModelParametersV1(hash), new UpdateSpeechModelDataV1(isEnabled, version, acceptLicense), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostRefreshAsync()
        {
            await refresh.ExecuteAsync<UpdateResult>(cancellationToken: HttpContext.RequestAborted);
            Hint("speech.models.refreshed");
            return LocalRedirect(Address());
        }

        private Dictionary<string, string> Filters() => new()
        {
            ["language"] = Language ?? "",
            ["gender"] = Gender is { } gender ? ((byte)gender).ToString(CultureInfo.InvariantCulture) : "",
            ["isInstalled"] = IsInstalled?.ToString().ToLowerInvariant() ?? ""
        };

        private Dictionary<string, string> FilterScope(string field)
        {
            var scope = Filters();
            scope.Remove(field);
            scope["q"] = Query ?? "";
            return scope;
        }

        private string Address(int? page = null) => Home + "?q=" + Uri.EscapeDataString(Query ?? "") + "&pageNumber=" + (page ?? PageNumber).ToString(CultureInfo.InvariantCulture)
            + string.Concat(Filters().Select(pair => "&" + pair.Key + "=" + Uri.EscapeDataString(pair.Value)));

        protected override async Task LoadDataAsync()
        {
            var catalog = await read.ExecuteAsync<SpeechModelCatalogV1>(new SearchSpeechModelsParametersV1(Query, Language, PageNumber, gender: Gender, isInstalled: IsInstalled), HttpContext.RequestAborted);
            _models = catalog.Models;
            _languages = catalog.Languages;
        }
    }
}
