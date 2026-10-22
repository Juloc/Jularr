using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Profiles
{
    // The quality rules, one pane each: profiles, custom formats, quality sizes, release profiles and tags. Lists are the list kit, editors are dialogs, sizes is one form with the page's save pill.
    public sealed partial class ProfilesModel(
        ReadAcquisitionProfilesV1 profiles,
        ReadCustomFormatsV1 formats, ReadCustomFormatV1 format, SaveCustomFormatV1 saveFormat, DeleteCustomFormatV1 deleteFormat, ImportCustomFormatsV1 importFormats,
        ReadQualityDefinitionsV1 sizes, SaveQualityDefinitionsV1 saveSizes,
        ReadReleaseProfilesV1 releases, ReadReleaseProfileV1 release, SaveReleaseProfileV1 saveRelease, DeleteReleaseProfileV1 deleteRelease,
        ReadAcquisitionTagsV1 tags, ReadAcquisitionTagV1 tag, SaveAcquisitionTagV1 saveTag, DeleteAcquisitionTagV1 deleteTag,
        ReadAcquisitionConnectionsV1 connections, ReadAcquisitionConnectionV1 connection,
        FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        internal static readonly (MediaType Type, MediaSubtype? Subtype)[] Kinds =
        [
            (MediaType.Movie, null), (MediaType.Series, null), (MediaType.Book, null),
            (MediaType.Book, MediaSubtype.Manga), (MediaType.Book, MediaSubtype.LightNovel),
            (MediaType.Music, null), (MediaType.Game, null)
        ];

        [BindProperty(SupportsGet = true, Name = "tab")] public string? SelectedTab { get; set; }
        private string Tab { get => SelectedTab ?? DownloadPages.Profiles; set => SelectedTab = value; }
        [BindProperty(SupportsGet = true)] public MediaType MediaTypeId { get; set; } = MediaType.Movie;
        [BindProperty(SupportsGet = true)] public long? Edit { get; set; }
        [BindProperty(SupportsGet = true, Name = "new")] public bool IsNew { get; set; }
        [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public int TagPage { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public int IndexerPage { get; set; } = 1;
        private SelectOption[] _tagChoices = [];
        private SelectOption[] _indexerChoices = [];
        private PageResult<AcquisitionTagDetailsV1>? _tagPage;
        private PageResult<AcquisitionConnectionDetailsV1>? _indexerPage;
        private IReadOnlyList<AcquisitionProfileDetailsV1> _profiles = [];
        private PageResult<CustomFormatDetailsV1>? _formats;
        private PageResult<ReleaseProfileDetailsV1>? _releases;
        private PageResult<AcquisitionTagDetailsV1>? _tags;
        private SaveCustomFormatDataV1? _format;
        private SaveReleaseProfileDataV1? _release;
        private SaveAcquisitionTagDataV1? _tag;
        private SaveQualityDefinitionsDataV1? _sizes;
        private ImportCustomFormatsDataV1? _import;

        protected override void Define(Page page)
        {
            page.Title(Tab switch
            {
                DownloadPages.Formats => "arr.formats",
                DownloadPages.Sizes => "arr.sizes",
                DownloadPages.Release => "arr.releaseProfiles",
                DownloadPages.Tags => "arr.tags",
                _ => "admin.profiles.title"
            });
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Profiles/Profiles.entry.scss");
            page.Asset("Framework/Components/Modal.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            page.Asset("Framework/Components/SelectLinkMenu.entry.ts");
            var isSetup = SetupWizard.IsActive(Request);
            if (isSetup) page.Content.Add(SetupWizard.Frame(SetupStepType.Profiles));

            // One row of navigation; the quality rules are scoped by a media type (tags belong to every type).
            page.NoPageRow();
            var chips = isSetup ? DownloadPages.SetupTabs(Tab, MediaTypeId, Home(Request)) : AdminMenu.Row(texts, Request, null, Tab, ((byte)MediaTypeId).ToString(CultureInfo.InvariantCulture));
            page.Content.Add(Tab == DownloadPages.Tags ? new ListToolbar(chips) : new ListToolbar(chips, ScopeMenu()));
            if (Tab == DownloadPages.Profiles) page.Content.Add(ProfileFigures());
            switch (Tab)
            {
                case DownloadPages.Profiles: DefineProfiles(page); break;
                case DownloadPages.Formats: DefineFormats(page); break;
                case DownloadPages.Sizes: DefineSizes(page); break;
                case DownloadPages.Release: DefineRelease(page); break;
                case DownloadPages.Tags: DefineTags(page); break;
            }
        }

        // The media type the lists and editors are about; the other panes keep the choice.
        private IUiComponent ScopeMenu() => new SelectLinkMenu(assets, [.. Enum.GetValues<MediaType>().Select(type =>
            new NavigationTab(MediaTypeLabel.Key(type, null), DownloadPages.PaneAddress(Tab, type, Home(Request)), type == MediaTypeId))], "arr.scope");

        private IUiComponent ProfileFigures() => new KpiTiles(
            new KpiTile("profile.kpi.total", Count(_profiles.Count), Glyph("layers"), UiTone.Accent),
            new KpiTile("profile.kpi.inUse", Count(_profiles.Count(item => item.WantedCount > 0)), Glyph("check"), UiTone.Success),
            new KpiTile("profile.kpi.scored", Count(_profiles.Count(item => item.FormatScores.Any(score => score.Score != 0))), Glyph("sliders"), UiTone.Info),
            new KpiTile("profile.kpi.upgrading", Count(_profiles.Count(item => item.UpgradeAllowed)), Glyph("sync"), UiTone.Warning)).Strip();

        private void DefineProfiles(Page page)
        {
            var empty = new Div(new IconTile(Glyph("layers", 22), UiTone.Neutral), new P(new Text("profile.none")).Attribute("role", "status"));
            var columns = new List<ListColumn>
            {
                new("profile.name", ListRole.Main), new("table.status", ListRole.Status), new("profile.cutoff", ListRole.Data, 1), new("arr.minimumScore", ListRole.Data, 2),
                new("profile.scores", ListRole.Data, 2), new("common.actions", ListRole.Actions)
            };
            var rows = _profiles.Select(item =>
            {
                var qualities = string.Join(", ", item.AllowedQualities.Select(quality => QualityName(item, quality)));
                var editor = Home(Request) + "/" + Number(item.Id);
                var identity = new ListIdentity(new IconTile(Glyph("layers", 18), UiTone.Accent), new A(new ValueText(item.Name)).Class("list-identity__title").Attribute("href", editor),
                    new Truncated(qualities));
                var cutoff = item.UpgradeAllowed && item.UpgradeCutoffQuality is { } stop ? new Span(new ValueText(QualityName(item, stop))).Class("list-value") : new Span(new Text("profile.noCutoff")).Class("list-value list-value--dim");
                var scored = item.FormatScores.Count(score => score.Score != 0);
                var state = item.IsDefault ? new StateChip(new Text("profile.default"), UiTone.Success, Glyph("check", 14))
                    : item.WantedCount > 0 ? new StateChip(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["profile.usedBy"], item.WantedCount)), UiTone.Info, Glyph("requests", 14))
                    : new StateChip(new Text("profile.unused"), UiTone.Neutral, Glyph("clock", 14));
                var caption = new List<IUiComponent> { ListPanel.Fact("profile.cutoff", cutoff), ListPanel.Fact("profile.scores", Count(scored)) };
                return new ListRow([identity, state, cutoff, new Span(new ValueText(item.MinimumFormatScore.ToString(CultureInfo.CurrentCulture))).Class("list-value"), new Span(Count(scored)).Class("list-value"),
                    new Div(EditLink(editor)).Class("list-actions")], caption);
            }).ToList();
            page.Content.Add(new ListPanel("admin.profiles.title", columns, rows, empty).Modifier("profiles-list"));

            // One add action for every kind of the media type (books also have manga and light novels).
            var actions = Kinds.Where(kind => kind.Type == MediaTypeId).Select(kind => (IUiComponent)new A(Glyph("plus", 16), new Span(new Text("profile.add." + (kind.Subtype?.ToString() ?? kind.Type.ToString()))).Class("action-dock__text"))
                .Class("button").Attribute("href", Home(Request) + "/New?type=" + kind.Type + (kind.Subtype is { } subtype ? "&subtype=" + subtype : ""))).ToList();
            page.Content.Add(new ActionDock(assets, "arr.actions", actions));
        }

        // The name of a quality or of a quality group.
        private static string QualityName(AcquisitionProfileDetailsV1 item, string quality) => item.QualityGroups.FirstOrDefault(group => group.Key == quality)?.Name ?? quality;

        private IUiComponent EditLink(string href) => new A(Glyph("pencil", 16)).Class("button button--icon").TextAttribute("aria-label", "common.edit").TextAttribute("title", "common.edit").Attribute("href", href);

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);

        private static IUiComponent Count(int value) => new ValueText(value.ToString("N0", CultureInfo.CurrentCulture));

        private string Address(string? handler = null, long? edit = null, int? page = null, bool isNew = false) =>
            DownloadPages.PaneAddress(Tab, MediaTypeId, Home(Request)) + "&PageNumber=" + Number(page ?? PageNumber)
            + (edit is { } id ? "&edit=" + Number(id) : "") + (isNew ? "&new=true" : "") + (handler is null ? "" : "&handler=" + handler);

        private string? Token => antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);


        private IUiComponent AddAction(string labelKey) => new A(Glyph("plus", 16), new Span(new Text(labelKey)).Class("action-dock__text")).Class("button").Attribute("href", Address(isNew: true));

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            if (!Enum.IsDefined(MediaTypeId) || !DownloadPages.Panes.Contains(Tab)) throw new KeyNotFoundException();
            var cancellation = HttpContext.RequestAborted;
            switch (Tab)
            {
                case DownloadPages.Profiles:
                    _profiles = await profiles.ExecuteAsync<BaseDataList<AcquisitionProfileDetailsV1>>(new ReadAcquisitionProfilesParametersV1(mediaTypeId: MediaTypeId), cancellation);
                    break;
                case DownloadPages.Formats:
                    _formats = await formats.ExecuteAsync<PageResult<CustomFormatDetailsV1>>(new ReadCustomFormatsParametersV1(MediaTypeId, PageNumber), cancellation);
                    _profiles = await profiles.ExecuteAsync<BaseDataList<AcquisitionProfileDetailsV1>>(new ReadAcquisitionProfilesParametersV1(mediaTypeId: MediaTypeId), cancellation);
                    if (_format is null && Edit is { } formatId)
                    {
                        var item = await format.ExecuteAsync<CustomFormatDetailsV1>(new CustomFormatParametersV1(formatId), cancellation);
                        if (item.MediaTypeId != MediaTypeId) throw new KeyNotFoundException();
                        _format = new(item.Id, item.MediaTypeId, item.Name, item.IncludeWhenRenaming, item.Conditions, item.Version);
                    }
                    break;
                case DownloadPages.Sizes:
                    _sizes ??= new(await sizes.ExecuteAsync<BaseDataList<QualityDefinitionV1>>(new QualityDefinitionsParametersV1(MediaTypeId), cancellation));
                    break;
                case DownloadPages.Release:
                    _releases = await releases.ExecuteAsync<PageResult<ReleaseProfileDetailsV1>>(new ReadAcquisitionPolicyParametersV1(MediaTypeId, PageNumber), cancellation);
                    if (_release is null && Edit is { } releaseId)
                    {
                        var item = await release.ExecuteAsync<ReleaseProfileDetailsV1>(new ReleaseProfileParametersV1(releaseId), cancellation);
                        if (item.MediaTypeId is { } type && type != MediaTypeId) throw new KeyNotFoundException();
                        _release = new(item.Id, item.MediaTypeId, item.Name, item.IsEnabled, item.Required, item.Ignored, item.AcquisitionConnectionId, item.TagIds, item.Version);
                    }
                    break;
                case DownloadPages.Tags:
                    _tags = await tags.ExecuteAsync<PageResult<AcquisitionTagDetailsV1>>(new ReadAcquisitionTagsParametersV1(PageNumber), cancellation);
                    if (_tag is null && Edit is { } tagId)
                    {
                        var item = await tag.ExecuteAsync<AcquisitionTagDetailsV1>(new AcquisitionTagParametersV1(tagId), cancellation);
                        _tag = new(item.Id, item.Label, item.Version);
                    }
                    break;
            }
            if (Tab == DownloadPages.Release)
            {
                _tagPage = await tags.ExecuteAsync<PageResult<AcquisitionTagDetailsV1>>(new ReadAcquisitionTagsParametersV1(TagPage), cancellation);
                var choices = _tagPage.Items.ToList();
                foreach (var id in (_release?.TagIds ?? []).Where(id => !choices.Any(choice => choice.Id == id)).Take(100))
                    choices.Add(await tag.ExecuteAsync<AcquisitionTagDetailsV1>(new AcquisitionTagParametersV1(id), cancellation));
                _tagChoices = choices.Select(item => new SelectOption(Number(item.Id), item.Label)).ToArray();
                _indexerPage = await connections.ExecuteAsync<PageResult<AcquisitionConnectionDetailsV1>>(
                    new ReadAcquisitionConnectionsParametersV1(IndexerPage, acquisitionConnectionRoleId: AcquisitionConnectionRole.Search), cancellation);
                var indexers = _indexerPage.Items.ToList();
                foreach (var id in new[] { _release?.AcquisitionConnectionId }.Concat((_releases?.Items ?? []).Select(item => item.AcquisitionConnectionId)).OfType<long>().Distinct()
                    .Where(id => !indexers.Any(item => item.Id == id)).Take(100))
                    indexers.Add(await connection.ExecuteAsync<AcquisitionConnectionDetailsV1>(new AcquisitionConnectionParametersV1(id), cancellation));
                _indexerChoices = new[] { new SelectOption("", texts["arr.anyIndexer"]) }
                    .Concat(indexers.Select(item => new SelectOption(Number(item.Id), item.Name))).ToArray();
            }
        }

        internal static string Home(Microsoft.AspNetCore.Http.HttpRequest request) => SetupWizard.Address(request, SetupStepType.Profiles, DownloadPages.ProfilesAddress);
    }
}
