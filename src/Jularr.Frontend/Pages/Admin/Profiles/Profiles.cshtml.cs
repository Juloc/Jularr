using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Profiles
{
    // The quality rules as Sonarr has them, in five tabs and without a media-type bar: one list of every profile (the media types it applies to, those it is the
    // default of and whether anything uses it), the custom formats once for every type, the release groups with a live search, the release profiles with the tags,
    // and the quality sizes as sliders.
    // A profile has its own page; adding is a dialog the address opens, settings of a card are inputs saved by the page's pill.
    public sealed partial class ProfilesModel(
        ReadAcquisitionProfilesV1 profiles, ReadMonitoredLanguagesV1 readMonitored, SaveMonitoredLanguagesV1 saveMonitored, ReadLanguageRulesV1 readRules, SaveLanguageRuleV1 saveRule,
        ReadCustomFormatsV1 formats, ReadCustomFormatV1 format, SaveCustomFormatV1 saveFormat, DeleteCustomFormatV1 deleteFormat, ImportCustomFormatsV1 importFormats, TestReleaseV1 testRelease,
        ReadQualityDefinitionsV1 sizes, SaveQualityDefinitionsV1 saveSizes,
        ReadReleaseProfilesV1 releases, ReadReleaseProfileV1 release, SaveReleaseProfileV1 saveRelease, DeleteReleaseProfileV1 deleteRelease,
        ReadReleaseGroupsV1 groups, AddReleaseGroupV1 addGroup, RateReleaseGroupV1 rateGroup,
        ReadAcquisitionTagsV1 tags, ReadAcquisitionTagV1 tag, SaveAcquisitionTagV1 saveTag, DeleteAcquisitionTagV1 deleteTag,
        ReadAcquisitionConnectionsV1 connections, ReadAcquisitionConnectionV1 connection, ReadInstanceMediaV1 media,
        FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        // The items of the custom formats tab an address may open or remove.
        private const string FormatItem = "format";
        private const string ReleaseItem = "release";
        private const string TagItem = "tag";
        private const string GroupItem = "group";
        private const string ProfileItem = "profile";

        // Every value of the address is read leniently: an unknown tab, item or number shows the page, never a refused request.
        [BindProperty(SupportsGet = true, Name = "tab")] public string? SelectedTab { get; set; }
        [BindProperty(SupportsGet = true, Name = "edit")] public string? EditText { get; set; }
        [BindProperty(SupportsGet = true, Name = "new")] public string? NewText { get; set; }
        [BindProperty(SupportsGet = true, Name = "delete")] public string? DeleteText { get; set; }
        [BindProperty(SupportsGet = true, Name = "item")] public string? ItemText { get; set; }
        [BindProperty(SupportsGet = true, Name = "q")] public string? Search { get; set; }

        // The release groups tab: one rating or not rated, one media type and the order; an unknown value shows every group in the default order.
        [BindProperty(SupportsGet = true, Name = "rating")] public string? RatingText { get; set; }
        [BindProperty(SupportsGet = true, Name = "kind")] public string? KindText { get; set; }
        [BindProperty(SupportsGet = true, Name = "sort")] public string? SortText { get; set; }

        private ReleaseGroupFilter? RatingFilter => Enum.TryParse<ReleaseGroupFilter>(RatingText, true, out var value) && Enum.IsDefined(value) && !byte.TryParse(RatingText, out _) ? value : null;
        private AcquisitionMediaKind? KindFilter => Enum.TryParse<AcquisitionMediaKind>(KindText, true, out var value) && Enum.IsDefined(value) && !byte.TryParse(KindText, out _) ? value : null;
        private ReleaseGroupSort? GroupSort => Enum.TryParse<ReleaseGroupSort>(SortText, true, out var value) && Enum.IsDefined(value) && !byte.TryParse(SortText, out _) ? value : null;

        private string? _tab;
        private string Tab
        {
            get => SetupWizard.IsActive(Request) ? DownloadPages.Profiles : _tab ?? DownloadPages.Current(SelectedTab);
            set => _tab = value;
        }

        private long? Edit => long.TryParse(EditText, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
        private string? Item => ItemText?.Trim().ToLowerInvariant() is FormatItem or ReleaseItem or TagItem or GroupItem or ProfileItem ? ItemText.Trim().ToLowerInvariant() : null;
        private string? NewItem => NewText?.Trim().ToLowerInvariant() is FormatItem or ReleaseItem or TagItem or ProfileItem ? NewText.Trim().ToLowerInvariant() : null;
        private bool ConfirmDelete => string.Equals(DeleteText, "true", StringComparison.OrdinalIgnoreCase);
        private int PageOf(string key) => PagingScope.PageOf(Request.Query, key);

        private SelectOption[] _tagChoices = [];
        private SelectOption[] _indexerChoices = [];
        private PageResult<AcquisitionTagDetailsV1>? _tagPage;
        private PageResult<AcquisitionConnectionDetailsV1>? _indexerPage;
        private IReadOnlyList<AcquisitionProfileDetailsV1> _profiles = [];
        private MonitoredLanguagesV1? _monitored;
        private IReadOnlyList<LanguageRuleV1> _rules = [];
        private SaveLanguageRuleDataV1? _ruleInput;

        // The language whose add-ons the form holds; an unknown value shows an empty form.
        [BindProperty(SupportsGet = true, Name = "rule")] public string? RuleLanguage { get; set; }
        private SaveMonitoredLanguagesDataV1? _monitoredInput;
        private IReadOnlyList<AcquisitionMediaKind> _kinds = [];
        private PageResult<CustomFormatDetailsV1>? _formats;
        private PageResult<ReleaseProfileDetailsV1>? _releases;
        private PageResult<AcquisitionTagDetailsV1>? _tags;
        private PageResult<ReleaseGroupDetailsV1>? _groups;
        private AddReleaseGroupDataV1? _group;
        private SaveCustomFormatDataV1? _format;
        private SaveReleaseProfileDataV1? _release;
        private SaveAcquisitionTagDataV1? _tag;
        private readonly Dictionary<MediaType, SaveQualityDefinitionsDataV1> _sizes = [];
        private MediaType? _sizesPosted;
        private ImportCustomFormatsDataV1? _import;
        private ReleaseTestV1? _test;

        protected override void Define(Page page)
        {
            page.Title(Tab switch
            {
                DownloadPages.Formats => "arr.formats",
                DownloadPages.Groups => "arr.releaseGroups",
                DownloadPages.Releases => "arr.releaseProfiles",
                DownloadPages.Quality => "arr.quality",
                _ => "admin.profiles.title"
            });
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Profiles/Profiles.entry.scss");
            page.Asset("Framework/Components/Modal.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            if (Tab == DownloadPages.Quality) page.Asset("Framework/Components/SizeRange.entry.ts");
            if (Tab is DownloadPages.Groups or DownloadPages.Formats) page.Asset("Framework/Components/LiveSearch.entry.ts");
            if (Tab == DownloadPages.Groups) page.Asset("Framework/Components/ChoiceSelect.entry.ts");

            // One row of tabs; the setup shows the profiles alone. The groups tab puts its search and filters beside the tabs.
            page.NoPageRow();
            if (!SetupWizard.IsActive(Request) && Tab != DownloadPages.Groups) page.Content.Add(new ListToolbar(AdminMenu.Row(texts, Request, null, Tab)));
            switch (Tab)
            {
                case DownloadPages.Formats: DefineFormats(page); break;
                case DownloadPages.Groups: DefineGroups(page); break;
                case DownloadPages.Releases: DefineReleases(page); break;
                case DownloadPages.Quality: DefineQuality(page); break;
                default: DefineProfiles(page); break;
            }
        }

        // One list of every profile: the media types it applies to, those it is the default of, the titles that chose it and where upgrades stop.
        private void DefineProfiles(Page page)
        {
            var empty = new Div(new IconTile(Glyph("layers", 22), UiTone.Neutral), new P(new Text("profile.none")).Attribute("role", "status"));
            var columns = new List<ListColumn>
            {
                new("profile.name", ListRole.Main), new("table.status", ListRole.Status), new("profile.appliesTo", ListRole.Data, 1), new("profile.defaultFor", ListRole.Data, 1),
                new("profile.cutoff", ListRole.Data, 2), new("common.actions", ListRole.Actions)
            };
            var rows = _profiles.Select(item =>
            {
                var qualities = string.Join(", ", item.AllowedQualities.Select(quality => QualityName(item, quality)));
                var editor = Home(Request) + "/" + Number(item.Id);
                var identity = new ListIdentity(new IconTile(Glyph("layers", 18), item.DefaultFor.Length > 0 ? UiTone.Success : UiTone.Accent),
                    new A(new ValueText(item.Name)).Class("list-identity__title").Attribute("href", editor), new Truncated(qualities));
                var appliesTo = KindTags(item.MediaKinds, false);
                var defaultFor = item.DefaultFor.Length == 0 ? new Span(new ValueText("–")).Class("list-value list-value--dim") : KindTags(item.DefaultFor, true);
                var cutoff = item.UpgradeAllowed && item.UpgradeCutoffQuality is { } stop ? new Span(new ValueText(QualityName(item, stop))).Class("list-value")
                    : new Span(new Text("profile.noCutoff")).Class("list-value list-value--dim");
                var caption = new List<IUiComponent> { ListPanel.Fact("profile.appliesTo", new ValueText(string.Join(", ", item.MediaKinds.Select(KindName)))) };
                if (item.DefaultFor.Length > 0) caption.Add(ListPanel.Fact("profile.defaultFor", new ValueText(string.Join(", ", item.DefaultFor.Select(KindName)))));
                return new ListRow([identity, Use(item), appliesTo, defaultFor, cutoff, new Div(EditLink(editor)).Class("list-actions")], caption, item.Name);
            }).ToList();
            page.Content.Add(new ListPanel("admin.profiles.title", columns, rows, empty).Modifier("profiles-list"));
            if (_monitored is not null && !SetupWizard.IsActive(Request)) page.Content.Add(MonitoredLanguages(_monitored));
            if (!SetupWizard.IsActive(Request)) page.Content.Add(LanguageRules());

            // A new profile starts with the media type it is for; the type decides its list of qualities.
            if (NewItem == ProfileItem)
            {
                var choices = _kinds.Select(kind => (IUiComponent)new A(new IconTile(Glyph(KindIcon(kind), 18), UiTone.Accent), new Span(new ValueText(KindName(kind))))
                    .Class("profile-kind").Attribute("href", Home(Request) + "/New?kind=" + kind)).ToList();
                page.Content.Add(new Modal(assets, "profile-new", "profile.new", [new P(new Text("profile.new.kind")), new Div([.. choices]).Class("profile-kinds")], open: true, returnTo: Address()));
            }

            page.Content.Add(new ActionDock(assets, "arr.actions", [AddAction("profile.new", ProfileItem)]));
        }

        // Whether anything uses the profile: the titles that chose it, else its open requests, else nothing (a request rule that offers it does not count).
        private IUiComponent Use(AcquisitionProfileDetailsV1 item) =>
            item.WantedCount > 0 ? new StateChip(new ValueText(string.Format(CultureInfo.CurrentCulture, texts[item.WantedCount == 1 ? "profile.use.title" : "profile.use.titles"],
                    item.WantedCount.ToString("N0", CultureInfo.CurrentCulture))), UiTone.Success, Glyph("check", 14))
            : item.OpenRequestCount > 0 ? new StateChip(new ValueText(string.Format(CultureInfo.CurrentCulture, texts[item.OpenRequestCount == 1 ? "profile.use.request" : "profile.use.requests"],
                    item.OpenRequestCount.ToString("N0", CultureInfo.CurrentCulture))), UiTone.Info, Glyph("clock", 14))
            : item.DefaultFor.Length > 0 ? new StateChip(new Text("profile.use.default"), UiTone.Success, Glyph("check", 14))
            : new StateChip(new Text("profile.use.none"), UiTone.Neutral, Glyph("minus", 14));

        // The languages of the titles nobody requested: the first language of every profile, or one chain.
        private UiSection MonitoredLanguages(MonitoredLanguagesV1 current)
        {
            var input = _monitoredInput ?? new SaveMonitoredLanguagesDataV1(current.ModeId, current.FirstLanguage, current.SecondLanguage, current.UsesOriginal);
            IEnumerable<SelectOption> Languages(string emptyKey) => [new SelectOption("", texts[emptyKey]), .. Pages.Admin.Work.WorkLanguages.Choices.Select(code => new SelectOption(code, Pages.Admin.Work.WorkLanguages.Name(code)))];
            var form = new Form<SaveMonitoredLanguagesDataV1>(fields =>
            {
                fields.Select(data => data.ModeId, "arr.monitoredLanguages.mode", Enum.GetValues<MonitoredLanguageMode>().Select(mode => new SelectOption(((int)mode).ToString(CultureInfo.InvariantCulture), texts["arr.monitoredLanguages.mode." + mode])));
                fields.Select(data => data.FirstLanguage, "work.languages.first", Languages("work.languages.none"));
                fields.Select(data => data.SecondLanguage, "work.languages.second", Languages("work.languages.none"));
                fields.Toggle(data => data.UsesOriginal, "profile.field.languageUsesOriginal");
                fields.Submit("common.save");
            }, Address("MonitoredLanguages"), Token, input, _monitoredInput is null ? null : FormError);
            var profileLanguages = current.Languages.Length == 0 ? "–" : string.Join(", ", current.Languages.Select(Pages.Admin.Work.WorkLanguages.Name));
            return new UiSection("arr.monitoredLanguages.title", [new P(new Text("arr.monitoredLanguages.info")), new P(new Text("arr.monitoredLanguages.profiles"), new ValueText(": " + profileLanguages)), form]);
        }

        public async Task<IActionResult> OnPostMonitoredLanguagesAsync(MonitoredLanguageMode modeId, string? firstLanguage, string? secondLanguage, bool usesOriginal)
        {
            _monitoredInput = new SaveMonitoredLanguagesDataV1(modeId, string.IsNullOrWhiteSpace(firstLanguage) ? null : firstLanguage, string.IsNullOrWhiteSpace(secondLanguage) ? null : secondLanguage, usesOriginal);
            await saveMonitored.ExecuteAsync<UpdateResult>(new MonitoredLanguagesParametersV1(), _monitoredInput, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        // The media types as small chips, as the media-type bar names them; a default is tinted.
        private IUiComponent KindTags(IEnumerable<AcquisitionMediaKind> kinds, bool accent) =>
            new Div([.. kinds.Select(kind => (IUiComponent)(accent ? new Tag(new ValueText(KindName(kind))).Accent() : new Tag(new ValueText(KindName(kind)))))]).Class("profile-kinds-cell");

        internal static string KindSlug(AcquisitionMediaKind kind) => MediaAreas.Keys.First(pair => pair.Value == kind.ToString()).Key;

        private string KindName(AcquisitionMediaKind kind) => texts["library.scope." + KindSlug(kind)];

        private static string KindIcon(AcquisitionMediaKind kind) => MediaAreas.Icons[KindSlug(kind)];

        // The name of a quality or of a quality group.
        private static string QualityName(AcquisitionProfileDetailsV1 item, string quality) => item.QualityGroups.FirstOrDefault(group => group.Key == quality)?.Name ?? quality;

        private IUiComponent EditLink(string href) => new A(Glyph("pencil", 16)).Class("button button--icon").TextAttribute("aria-label", "common.edit").TextAttribute("title", "common.edit").Attribute("href", href);

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);

        private static IUiComponent Count(int value) => new ValueText(value.ToString("N0", CultureInfo.CurrentCulture));

        // The address of the tab with what it opens; the pages of its lists stay as they are.
        private string Address(string? handler = null, long? edit = null, string? item = null, string? isNew = null)
        {
            var query = new List<string>();
            if (Tab != DownloadPages.Profiles) query.Add("tab=" + Tab);
            foreach (var key in new[] { PagedList.DefaultKey, "releases", "groups", "tags" })
            {
                if (PageOf(key) > 1) query.Add(key + "=" + Number(PageOf(key)));
            }

            if (edit is { } id) query.Add("edit=" + Number(id));
            if (item is not null) query.Add("item=" + item);
            if (isNew is not null) query.Add("new=" + isNew);
            if (!string.IsNullOrWhiteSpace(Search)) query.Add("q=" + Uri.EscapeDataString(Search.Trim()));
            if (Tab == DownloadPages.Groups)
            {
                foreach (var (key, value) in GroupFilters()) query.Add(key + "=" + value);
            }

            if (handler is not null) query.Add("handler=" + handler);
            return Home(Request) + (query.Count == 0 ? "" : "?" + string.Join("&", query));
        }

        private string? Token => antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        private IUiComponent AddAction(string labelKey, string item) =>
            new A(Glyph("plus", 16), new Span(new Text(labelKey)).Class("action-dock__text")).Class("button").Attribute("href", Address(isNew: item));

        public async Task<IActionResult> OnGetAsync()
        {
            // An address of the former panes and media-type bar leads to the tab that holds it now, without its type.
            if (DownloadPages.Legacy(Request.Query, Home(Request)) is { } current) return LocalRedirect(current);
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            var cancellation = HttpContext.RequestAborted;
            switch (Tab)
            {
                case DownloadPages.Profiles:
                    _profiles = await profiles.ExecuteAsync<BaseDataList<AcquisitionProfileDetailsV1>>(new ReadAcquisitionProfilesParametersV1(), cancellation);
                    _monitored = await readMonitored.ExecuteAsync<MonitoredLanguagesV1>(new MonitoredLanguagesParametersV1(), cancellation);
                    _rules = await readRules.ExecuteAsync<BaseDataList<LanguageRuleV1>>(new LanguageRulesParametersV1(), cancellation);
                    if (NewItem == ProfileItem)
                    {
                        // The types the instance offers: a type that is switched off has no new profiles.
                        var offered = await media.ExecuteAsync<BaseDataList<InstanceMediaDetailsV1>>(new ReadInstanceMediaParametersV1(), cancellation);
                        _kinds = [.. Enum.GetValues<AcquisitionMediaKind>().Where(kind => offered.Any(item => item.IsEnabled && item.Key == kind.ToString()))];
                    }
                    break;
                case DownloadPages.Formats:
                    await LoadFormatsAsync(cancellation);
                    break;
                case DownloadPages.Groups:
                    _groups = await groups.ExecuteAsync<PageResult<ReleaseGroupDetailsV1>>(
                        new ReadReleaseGroupsParametersV1(Search, PageOf("groups"), filter: RatingFilter, mediaKind: KindFilter, sort: GroupSort), cancellation);
                    break;
                case DownloadPages.Releases:
                    await LoadReleasesAsync(cancellation);
                    break;
                case DownloadPages.Quality:
                    foreach (var type in QualityTypes)
                    {
                        if (!_sizes.ContainsKey(type)) _sizes[type] = new(await sizes.ExecuteAsync<BaseDataList<QualityDefinitionV1>>(new QualityDefinitionsParametersV1(type), cancellation));
                    }
                    break;
            }
        }

        internal static string Home(Microsoft.AspNetCore.Http.HttpRequest request) => SetupWizard.Address(request, SetupStepType.Profiles, DownloadPages.ProfilesAddress);
    }
}
