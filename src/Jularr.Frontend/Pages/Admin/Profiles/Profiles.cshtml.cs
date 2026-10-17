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
    public sealed partial class ProfilesModel(
        ReadAcquisitionProfilesV1 profiles,
        ReadCustomFormatsV1 formats, ReadCustomFormatV1 format, SaveCustomFormatV1 saveFormat, DeleteCustomFormatV1 deleteFormat, ImportCustomFormatsV1 importFormats,
        ReadQualityDefinitionsV1 sizes, SaveQualityDefinitionsV1 saveSizes,
        ReadReleaseProfilesV1 releases, ReadReleaseProfileV1 release, SaveReleaseProfileV1 saveRelease, DeleteReleaseProfileV1 deleteRelease,
        ReadAcquisitionTagsV1 tags, ReadAcquisitionTagV1 tag, SaveAcquisitionTagV1 saveTag, DeleteAcquisitionTagV1 deleteTag,
        ReadAcquisitionConnectionsV1 connections, ReadAcquisitionConnectionV1 connection,
        IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        internal static readonly (MediaType Type, MediaSubtype? Subtype)[] Kinds =
        [
            (MediaType.Movie, null), (MediaType.Series, null), (MediaType.Book, null),
            (MediaType.Book, MediaSubtype.Manga), (MediaType.Book, MediaSubtype.LightNovel),
            (MediaType.Music, null), (MediaType.Game, null)
        ];

        [BindProperty(SupportsGet = true, Name = "tab")] public string? SelectedTab { get; set; }
        private string Tab { get => SelectedTab ?? "profiles"; set => SelectedTab = value; }
        [BindProperty(SupportsGet = true)] public MediaType MediaTypeId { get; set; } = MediaType.Movie;
        [BindProperty(SupportsGet = true)] public long? Edit { get; set; }
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
            page.Title("arr.settings");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Profiles/Profiles.entry.scss");
            page.Asset("Framework/Components/Form.entry.ts");
            if (SetupWizard.IsActive(Request)) page.Content.Add(SetupWizard.Frame(SetupStepType.Profiles));
            page.Content.Add(AcquisitionSettingsNavigation.Frame(Request, Tab, MediaTypeId));
            switch (Tab)
            {
                case "profiles": DefineProfiles(page); break;
                case "formats": DefineFormats(page); break;
                case "sizes": DefineSizes(page); break;
                case "release": DefineRelease(page); break;
                case "tags": DefineTags(page); break;
            }

        }

        private void DefineProfiles(Page page)
        {
            page.Content.Add(new Table<AcquisitionProfileDetailsV1>(_profiles)
                .Text(item => item.Name, "profile.name")
                .Content("table.type", item => new Text(MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId)))
                .Content("profile.qualities", item => new ValueText(string.Join(", ", item.AllowedQualities
                    .Select(quality => item.QualityGroups.FirstOrDefault(group => group.Key == quality)?.Name ?? quality))))
                .Content("profile.usage", item => item.IsDefault ? new Text("profile.default") : new ValueText(item.WantedCount.ToString(CultureInfo.InvariantCulture)))
                .Link(item => item.Id, "common.actions", id => Home(Request) + "/" + Number(id), "common.edit"));
            page.Content.Add(new Div(Kinds.Where(kind => kind.Type == MediaTypeId).Select(kind => (IUiComponent)new A(new Text("collections.add"), new ValueText(" "), new Text(MediaTypeLabel.Key(kind.Type, kind.Subtype)))
                .Class("button").Attribute("href", Home(Request) + "/New?type=" + kind.Type + (kind.Subtype is { } subtype ? "&subtype=" + subtype : ""))
                .Attribute("aria-label", texts[MediaTypeLabel.Key(kind.Type, kind.Subtype)])).ToArray()).Class("page-actions"));
        }

        private string Address(string? handler = null, long? edit = null, int? page = null) =>
            AcquisitionSettingsNavigation.Address(Request, Tab, MediaTypeId) + "&PageNumber=" + Number(page ?? PageNumber)
            + (edit is { } id ? "&edit=" + Number(id) : "") + (handler is null ? "" : "&handler=" + handler);
        private string? Token => antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        private IUiComponent EditLink(long id) => new A(new Text("common.edit")).Attribute("href", Address(edit: id));
        private void Paging<T>(Page page, PageResult<T> result) where T : BaseData
        {
            var links = new List<IUiComponent>();
            if (result.Page > 1) links.Add(new A(new Text("common.previous")).Class("button").Attribute("href", Address(page: result.Page - 1)));
            if (result.Items.Count == result.PageSize) links.Add(new A(new Text("common.next")).Class("button").Attribute("href", Address(page: result.Page + 1)));
            page.Content.Add(new Div(links.ToArray()).Class("page-actions"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            if (!Enum.IsDefined(MediaTypeId) || !AcquisitionSettingsNavigation.Panes.Any(pane => pane.Id == Tab)) throw new KeyNotFoundException();
            var cancellation = HttpContext.RequestAborted;
            switch (Tab)
            {
                case "profiles":
                    _profiles = await profiles.ExecuteAsync<BaseDataList<AcquisitionProfileDetailsV1>>(new ReadAcquisitionProfilesParametersV1(mediaTypeId: MediaTypeId), cancellation);
                    break;
                case "formats":
                    _formats = await formats.ExecuteAsync<PageResult<CustomFormatDetailsV1>>(new ReadCustomFormatsParametersV1(MediaTypeId, PageNumber), cancellation);
                    if (_format is null && Edit is { } formatId)
                    {
                        var item = await format.ExecuteAsync<CustomFormatDetailsV1>(new CustomFormatParametersV1(formatId), cancellation);
                        if (item.MediaTypeId != MediaTypeId) throw new KeyNotFoundException();
                        _format = new(item.Id, item.MediaTypeId, item.Name, item.IncludeWhenRenaming, item.Conditions, item.Version);
                    }
                    break;
                case "sizes":
                    _sizes ??= new(await sizes.ExecuteAsync<BaseDataList<QualityDefinitionV1>>(new QualityDefinitionsParametersV1(MediaTypeId), cancellation));
                    break;
                case "release":
                    _releases = await releases.ExecuteAsync<PageResult<ReleaseProfileDetailsV1>>(new ReadAcquisitionPolicyParametersV1(MediaTypeId, PageNumber), cancellation);
                    if (_release is null && Edit is { } releaseId)
                    {
                        var item = await release.ExecuteAsync<ReleaseProfileDetailsV1>(new ReleaseProfileParametersV1(releaseId), cancellation);
                        if (item.MediaTypeId is { } type && type != MediaTypeId) throw new KeyNotFoundException();
                        _release = new(item.Id, item.MediaTypeId, item.Name, item.IsEnabled, item.Required, item.Ignored, item.AcquisitionConnectionId, item.TagIds, item.Version);
                    }
                    break;
                case "tags":
                    _tags = await tags.ExecuteAsync<PageResult<AcquisitionTagDetailsV1>>(new ReadAcquisitionTagsParametersV1(PageNumber), cancellation);
                    if (_tag is null && Edit is { } tagId)
                    {
                        var item = await tag.ExecuteAsync<AcquisitionTagDetailsV1>(new AcquisitionTagParametersV1(tagId), cancellation);
                        _tag = new(item.Id, item.Label, item.Version);
                    }
                    break;
            }
            if (Tab == "release")
            {
                _tagPage = await tags.ExecuteAsync<PageResult<AcquisitionTagDetailsV1>>(new ReadAcquisitionTagsParametersV1(TagPage), cancellation);
                var choices = _tagPage.Items.ToList();
                foreach (var id in (_release?.TagIds ?? []).Where(id => !choices.Any(choice => choice.Id == id)).Take(100))
                    choices.Add(await tag.ExecuteAsync<AcquisitionTagDetailsV1>(new AcquisitionTagParametersV1(id), cancellation));
                _tagChoices = choices.Select(item => new SelectOption(Number(item.Id), item.Label)).ToArray();
            }
            if (Tab == "release")
            {
                _indexerPage = await connections.ExecuteAsync<PageResult<AcquisitionConnectionDetailsV1>>(
                    new ReadAcquisitionConnectionsParametersV1(IndexerPage, acquisitionConnectionRoleId: AcquisitionConnectionRole.Search), cancellation);
                var choices = _indexerPage.Items.ToList();
                if (_release?.AcquisitionConnectionId is { } id && !choices.Any(item => item.Id == id))
                    choices.Add(await connection.ExecuteAsync<AcquisitionConnectionDetailsV1>(new AcquisitionConnectionParametersV1(id), cancellation));
                _indexerChoices = new[] { new SelectOption("", texts["arr.anyIndexer"]) }
                    .Concat(choices.Select(item => new SelectOption(Number(item.Id), item.Name))).ToArray();
            }
        }

        internal static string Home(Microsoft.AspNetCore.Http.HttpRequest request) => SetupWizard.Address(request, Backend.Data.Enums.Instance.SetupStepType.Profiles, "/Admin/Acquisition/Profiles");
    }
}
