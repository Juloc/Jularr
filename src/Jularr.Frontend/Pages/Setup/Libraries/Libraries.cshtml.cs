using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Backend.Service.Admin.Library.V1;
using Jularr.Backend.Service.Admin.Storage.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Setup.Libraries
{
    // The libraries step: a folder for every media type that is switched on, chosen on a connection of the storage step.
    public sealed class LibrariesModel(ReadInstanceMediaV1 readMedia, ReadLibraryRootsV1 readRoots, CreateLibraryRootV1 createRoot, DeleteLibraryRootV1 deleteRoot,
        ReadStorageMountsV1 mounts, ReadStorageMountV1 mount, ReadStorageDirectoriesV1 directories, FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        // The type a subtype keeps its folder under.
        private static readonly Dictionary<string, MediaType> Parents = new() { ["Anime"] = MediaType.Series, ["Manga"] = MediaType.Book, ["LightNovel"] = MediaType.Book };

        private IReadOnlyList<InstanceMediaDetailsV1> _media = [];
        private IReadOnlyList<LibraryRootDetailsV1> _roots = [];
        private CreateLibraryRootDataV1? _input;

        protected override void Define(Page page)
        {
            page.Title("setup.step.Libraries");
            page.Asset("Framework/Components/FolderPicker.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var media = _media.Where(item => item.IsEnabled).ToDictionary(item => item.Key);
            var rows = new List<IUiComponent>();
            foreach (var area in MediaAreas.All)
            {
                foreach (var key in area.Slugs.Select(slug => MediaAreas.Keys[slug]).Where(media.ContainsKey))
                {
                    var item = media[key];
                    var own = _roots.Where(root => item.IsSubtype ? root.MediaSubtypeId?.ToString() == key : root.MediaTypeId.ToString() == key && root.MediaSubtypeId is null).ToList();
                    rows.Add(Row(item, area.Icon, own, csrf));
                }
            }

            if (FormError is not null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
            page.Content.Add(new Div([.. rows]).Class("setup-libraries"));
        }

        // One media type: its name and state, the folders it has and the picker for another.
        private IUiComponent Row(InstanceMediaDetailsV1 item, string icon, IReadOnlyList<LibraryRootDetailsV1> own, string? csrf)
        {
            var name = texts["setup.type.name." + item.Key];
            var type = item.IsSubtype ? Parents[item.Key] : Enum.Parse<MediaType>(item.Key);
            MediaSubtype? subtype = item.IsSubtype ? Enum.Parse<MediaSubtype>(item.Key) : null;
            var state = own.Count > 0 ? new StateChip(new Text("setup.libraries.ready"), UiTone.Success, new Icon("check", assets, 14))
                : new StateChip(new Text("setup.libraries.off"), UiTone.Warning, new Icon("alert", assets, 14));
            var roots = own.Select(root => (IUiComponent)new Div(new Span(new Truncated(root.StorageMountName + " /" + root.RelativePath)).Class("setup-libraries__path"),
                new PostForm("/Setup/Libraries?handler=Remove", csrf, [("libraryRootId", root.Id.ToString(CultureInfo.InvariantCulture)), ("version", root.Version.ToString(CultureInfo.InvariantCulture))],
                    new Button(new Icon("trash", assets, 16)).IconOnly(texts["storage.paths.remove"]).Submit())).Class("setup-libraries__root")).ToArray();
            var input = _input is not null && _input.MediaTypeId == type && _input.MediaSubtypeId == subtype ? _input : new CreateLibraryRootDataV1(name, type, 0, "", subtype);
            var form = new Form<CreateLibraryRootDataV1>(form =>
            {
                form.Hidden(data => data.Name);
                form.Hidden(data => data.MediaTypeId);
                form.Hidden(data => data.MediaSubtypeId);
                form.Hidden(data => data.StorageMountId);
                form.Hidden(data => data.RelativePath);
                form.Content(new FolderPicker("/Setup/Libraries?handler=Folders", "", nameof(CreateLibraryRootDataV1.RelativePath), "/Setup/Storage", storageMountFieldName: nameof(CreateLibraryRootDataV1.StorageMountId),
                    choosesStorage: true, selectedLabel: texts["downloadStorage.unset"]));
                form.Submit(new Button(new Icon("plus", assets, 14), new Text("setup.libraries.add")).Soft());
            }, "/Setup/Libraries?handler=Add", csrf, input, _input is not null && _input.MediaTypeId == type && _input.MediaSubtypeId == subtype ? FormError : null);
            return new Div(new Div(new IconTile(new Icon(icon, assets, 18), UiTone.Accent), new Strong(new Text("setup.type.name." + item.Key)), state).Class("setup-libraries__who"),
                new Div([.. roots, form]).Class("setup-libraries__folders")).Class("setup-libraries__row");
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAddAsync(string? name, [BindRequired] MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, long storageMountId, string? relativePath)
        {
            _input = new CreateLibraryRootDataV1(name ?? "", mediaTypeId, storageMountId, relativePath ?? "", mediaSubtypeId);
            await createRoot.ExecuteAsync<CreateLibraryRootResultV1>(_input, HttpContext.RequestAborted);
            return LocalRedirect("/Setup/Libraries");
        }

        public async Task<IActionResult> OnPostRemoveAsync([BindRequired] long libraryRootId, [BindRequired] long version)
        {
            await deleteRoot.ExecuteAsync<UpdateResult>(new DeleteLibraryRootParametersV1(libraryRootId, version), default, HttpContext.RequestAborted);
            return LocalRedirect("/Setup/Libraries");
        }

        public async Task<IActionResult> OnGetFoldersAsync(long? storageMountId, string? path, string? afterName, int mountPage = 1)
        {
            var content = await StorageBrowser.FragmentAsync("/Setup/Libraries?handler=Folders", storageMountId, path, afterName, mountPage, false, mounts, mount, directories, texts, HttpContext.RequestAborted);
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            content.WriteTo(writer, HtmlEncoder.Default, texts);
            Response.Headers.CacheControl = "no-store";
            return Content(writer.ToString(), "text/html");
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _media = await readMedia.ExecuteAsync<BaseDataList<InstanceMediaDetailsV1>>(new ReadInstanceMediaParametersV1(), ct);
            _roots = (await readRoots.ExecuteAsync<PageResult<LibraryRootDetailsV1>>(new ReadLibraryRootsParametersV1(null, 1, 100), ct)).Items;
        }
    }
}
