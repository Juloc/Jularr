using System.Globalization;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Service.User.Playback.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Player
{
    public sealed class PlayerModel(ReadPlaybackItemV1 read, IAntiforgery antiforgery, FrontendAssets assets, UiText texts) : JularrPage
    {
        private PlaybackItemV1? _item;

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        protected override void Define(Page page)
        {
            page.Title("player.title");
            page.Asset("Modules/Player/player.entry.ts");
            page.Shell = false;
            page.IsPlayer = true;

            // The plan is requested with the browser's capabilities.
            if (_item is { } item)
            {
                var asset = item.MediaAssetId.ToString(CultureInfo.InvariantCulture);
                var context = MediaUnitLabel.Format(texts, item.WorkUnitTypeId, item.UnitNumber, item.ParentUnitNumber);
                var source = new PlayerSource(item.CanonicalTitle, context, "/Details/" + item.WorkId.ToString(CultureInfo.InvariantCulture), "/api/v1/playback/assets/" + asset + "/sessions",
                    "/api/v1/playback/sessions/", antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "");
                page.Content.Add(new UiPlayer(source, assets));
            }
            else if (FormError is not null)
            {
                page.Content.Add(new P(new Text(FormError.ResourceKey)).Class("player__status").Attribute("role", "alert"));
            }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _item = await read.ExecuteAsync<PlaybackItemV1>(new MediaAssetParametersV1(Id), HttpContext.RequestAborted);
        }
    }
}
