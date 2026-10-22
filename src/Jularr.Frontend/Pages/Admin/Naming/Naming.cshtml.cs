using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Library.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Naming
{
    // How files and folders are named, per media type: the arr syntax with its placeholders, an example that follows what is typed, and how values become names. All forms are saved by the page's pill.
    public sealed class NamingModel(ReadNamingPatternsV1 readPatterns, UpdateNamingPatternV1 updatePattern, UpdateNamingSubtypePatternV1 updateSubtypePattern,
        ReadNamingSettingsV1 readSettings, UpdateNamingSettingsV1 updateSettings, FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private const string Address = "/Admin/Instance/Naming";

        // Why a typed pattern cannot be used, as the preview names it; the page writes each in its language for the script.
        private static readonly string[] Problems = ["naming_unknown_token", "naming_incomplete", "naming_invalid_path"];
        private IReadOnlyList<NamingPatternV1> _patterns = [];
        private IReadOnlyList<NamingSettingsV1> _settings = [];
        private UpdateNamingPatternDataV1? _patternInput;
        private (MediaType Type, MediaSubtype? Subtype)? _failedPattern;
        private UpdateNamingSettingsDataV1? _settingsInput;
        private MediaType? _failedSettings;

        protected override void Define(Page page)
        {
            page.Title("library.tab.Naming");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Naming/Naming.entry.scss");
            page.Asset("Pages/Admin/Naming/Naming.entry.ts");
            page.Asset("Framework/Components/Drawer.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var colons = Enum.GetValues<ColonReplacement>().Select(mode => new SelectOption(((byte)mode).ToString(CultureInfo.InvariantCulture), texts["library.naming.colon." + mode]));

            // One panel per media type: its folders and file name (and its subtypes' own), then how values become names.
            foreach (var type in _patterns.Select(pattern => pattern.MediaTypeId).Distinct())
            {
                var items = new List<IUiComponent>();
                foreach (var pattern in _patterns.Where(pattern => pattern.MediaTypeId == type).OrderBy(pattern => pattern.MediaSubtypeId))
                {
                    var failed = _failedPattern == (pattern.MediaTypeId, pattern.MediaSubtypeId);
                    if (pattern.MediaSubtypeId is { } subtype) items.Add(new H3(new Text("library.naming." + type + "." + subtype)));
                    items.Add(new Form<UpdateNamingPatternDataV1>(form =>
                    {
                        form.Hidden(item => item.Version);
                        form.Text(item => item.Folder, "library.naming.folder");
                        form.Text(item => item.File, "library.naming.file");
                        form.Save();
                    }, Address + "?handler=Pattern&mediaTypeId=" + pattern.MediaTypeId + (pattern.MediaSubtypeId is { } kind ? "&mediaSubtypeId=" + kind : ""), csrf,
                        failed && _patternInput is { } input ? input : new UpdateNamingPatternDataV1(pattern.Folder, pattern.File, pattern.Version), failed ? FormError : null));
                    var example = new Span(new ValueText(pattern.Example)).Class("naming-example").Attribute("data-naming-example", "");
                    foreach (var problem in Problems) example.TextAttribute("data-problem-" + problem, "errors.validation." + problem);
                    items.Add(new P(new Text("library.naming.example"), new ValueText(" "), example));
                }

                if (_settings.FirstOrDefault(settings => settings.MediaTypeId == type) is { } current)
                {
                    var failed = _failedSettings == type;
                    items.Add(new Form<UpdateNamingSettingsDataV1>(form =>
                    {
                        form.Hidden(item => item.Version);
                        form.Toggle(item => item.ReplacesIllegalCharacters, "library.naming.replacesIllegal");
                        form.Select(item => item.ColonReplacementId, "library.naming.colon", colons);
                        form.Text(item => item.CustomColonReplacement, "library.naming.colonCustom");
                        if (type == MediaType.Series) form.Text(item => item.SpecialsFolder, "library.naming.specials");
                        form.Save();
                    }, Address + "?handler=Settings&mediaTypeId=" + type, csrf, failed && _settingsInput is { } entered ? entered
                        : new UpdateNamingSettingsDataV1(current.ReplacesIllegalCharacters, current.ColonReplacementId, current.CustomColonReplacement, current.SpecialsFolder, current.Version),
                        failed ? FormError : null));
                }

                page.Content.Add(new UiSection("library.naming." + type, items));
            }

            if (FormError is not null && _failedPattern is null && _failedSettings is null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));

            // The placeholders open in a drawer; a click puts one into the field that was last used.
            var tokens = NamingTokens.All.SelectMany(token => new IUiComponent[]
            {
                new Button(new ValueText(NamingTokens.Display(token))).Class("naming-token").Attribute("type", "button").Attribute("data-naming-token", NamingTokens.Display(token)),
                new Span(new Text("library.naming.token." + token.Replace(" ", "", StringComparison.Ordinal)))
            }).ToArray();
            var body = new IUiComponent[] { new P(new Text("library.naming.rules")), new Div(tokens).Class("naming-tokens") };
            page.Content.Add(new Drawer(assets, "naming-tokens", [new DrawerPane("tokens", "library.naming.placeholders", "library.naming.placeholders", "tag", body, [])], sheet: true));
            page.Content.Add(new ActionDock(assets, "library.naming.actions", [Drawer.Opener(new Button(new Icon("tag", assets, 16), new Span(new Text("library.naming.placeholders")).Class("action-dock__text")), "naming-tokens")]));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!texts.CanManageStorage()) return LocalRedirect("/Admin/Instance");
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostPatternAsync([BindRequired] MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string? folder, string? file, long version)
        {
            _failedPattern = (mediaTypeId, mediaSubtypeId);
            _patternInput = new UpdateNamingPatternDataV1(folder, file ?? "", version);
            if (mediaSubtypeId is { } subtype)
            {
                await updateSubtypePattern.ExecuteAsync<UpdateResult>(new NamingSubtypePatternParametersV1(mediaTypeId, subtype), _patternInput, HttpContext.RequestAborted);
            }
            else
            {
                await updatePattern.ExecuteAsync<UpdateResult>(new NamingPatternParametersV1(mediaTypeId), _patternInput, HttpContext.RequestAborted);
            }

            return LocalRedirect(Address);
        }

        public async Task<IActionResult> OnPostSettingsAsync([BindRequired] MediaType mediaTypeId, bool replacesIllegalCharacters, ColonReplacement colonReplacementId, string? customColonReplacement,
            string? specialsFolder, long version)
        {
            _failedSettings = mediaTypeId;
            _settingsInput = new UpdateNamingSettingsDataV1(replacesIllegalCharacters, colonReplacementId, customColonReplacement, specialsFolder, version);
            await updateSettings.ExecuteAsync<UpdateResult>(new NamingSettingsParametersV1(mediaTypeId), _settingsInput, HttpContext.RequestAborted);
            return LocalRedirect(Address);
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _patterns = await readPatterns.ExecuteAsync<BaseDataList<NamingPatternV1>>(new ReadNamingPatternsParametersV1(), ct);
            _settings = await readSettings.ExecuteAsync<BaseDataList<NamingSettingsV1>>(new ReadNamingSettingsParametersV1(), ct);
        }
    }
}
