using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.MediaTypes
{
    // The media types and subtypes the instance offers, as cards in the areas people know (watch, read, hear, play); each card's switch is a form of the page's one pill.
    public sealed class MediaTypesModel(ReadInstanceMediaV1 read, UpdateInstanceMediaV1 update, FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private const string Address = "/Admin/Instance/MediaTypes";

        // The catalog key of each slug the media-type bar uses.
        private static readonly Dictionary<string, string> Keys = new()
        {
            ["movies"] = "Movie",
            ["series"] = "Series",
            ["anime"] = "Anime",
            ["books"] = "Book",
            ["manga"] = "Manga",
            ["lightnovels"] = "LightNovel",
            ["music"] = "Music",
            ["games"] = "Game"
        };

        private IReadOnlyList<InstanceMediaDetailsV1> _media = [];
        private UpdateInstanceMediaDataV1? _input;

        protected override void Define(Page page)
        {
            page.Title("admin.instance.media");
            page.Navigation("nav.admin");
            page.Asset("Framework/Components/SettingCard.entry.scss");
            page.Asset("Framework/Components/Truncated.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            foreach (var area in MediaAreas.All)
            {
                var cards = new List<IUiComponent>();
                foreach (var key in area.Slugs.Select(slug => Keys[slug]))
                {
                    if (_media.FirstOrDefault(item => item.Key == key) is not { } media) continue;
                    var failed = _input?.Key == media.Key;
                    cards.Add(new Form<UpdateInstanceMediaDataV1>(form =>
                    {
                        form.Hidden(item => item.Key);
                        form.Hidden(item => item.Version);
                        form.Switch(item => item.IsEnabled, MediaTypeLabel.Key(media.Key, media.IsSubtype), control => Card(media, area, control, failed ? _input!.IsEnabled : media.IsEnabled));
                        form.Save();
                    }, Address + "?handler=Media", csrf, failed ? _input : new UpdateInstanceMediaDataV1(media.Key, media.IsEnabled, media.Version), failed ? FormError : null));
                }

                if (cards.Count > 0) page.Content.Add(new UiSection(area.LabelKey, [new Div([.. cards]).Class("setting-cards")]));
            }

            if (FormError is not null && _input is null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        // A subtype takes its Works out of the lists of its parent; an enabled type needs a library of its own to receive anything.
        private IUiComponent Card(InstanceMediaDetailsV1 media, MediaArea area, IUiComponent control, bool isOn)
        {
            var tags = new List<IUiComponent>();
            if (!media.HasLibrary) tags.Add(new Tag(new Truncated(texts["admin.instance.media.noLibrary"])).Class(isOn ? "tag tag--warning" : "tag"));
            return new SettingCard(new IconTile(new Icon(area.Icon, assets, 17)), new Truncated(texts[MediaTypeLabel.Key(media.Key, media.IsSubtype)]), control, isOn,
                "admin.providers.enabled", "admin.providers.disabled", tags);
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostMediaAsync(string? key, [BindRequired] bool isEnabled, long version)
        {
            _input = new UpdateInstanceMediaDataV1(key ?? "", isEnabled, version);
            await update.ExecuteAsync<UpdateResult>(new UpdateInstanceMediaParametersV1(), _input, HttpContext.RequestAborted);
            return LocalRedirect(Address);
        }

        protected override async Task LoadDataAsync()
        {
            _media = await read.ExecuteAsync<BaseDataList<InstanceMediaDetailsV1>>(new ReadInstanceMediaParametersV1(), HttpContext.RequestAborted);
        }
    }
}
