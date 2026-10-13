using System.Globalization;
using Jularr.Backend.Data.User.Game.V1;
using Jularr.Backend.Service.User.Game.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Game
{
    // A retro game in EmulatorJS, served by Jularr itself; saves and play time follow the profile.
    public sealed class GameModel(ReadGameV1 read, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private GameV1? _game;

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        protected override void Define(Page page)
        {
            page.Title("game.title");
            page.Shell = false;
            page.Asset("Modules/Game/game.entry.ts");
            if (_game is not { Emulator: { } emulator } game)
            {
                page.Content.Add(new Div(
                    new P(new Text(_game is null ? FormError?.ResourceKey ?? "game.notFound" : "game.unsupported")).Attribute("role", "alert"),
                    new A(new Text("common.back")).Class("button").Attribute("href", _game is null ? "/Library" : "/Details/" + _game.WorkId.ToString(CultureInfo.InvariantCulture))
                ).Class("game__status"));
                return;
            }

            var asset = game.MediaAssetId.ToString(CultureInfo.InvariantCulture);
            page.Content.Add(new Div(
                new Div(
                    new A(new Text("common.back")).Class("button").Attribute("href", "/Details/" + game.WorkId.ToString(CultureInfo.InvariantCulture)),
                    new H1(new ValueText(game.Title)).Class("game__title"),
                    new Span(new ValueText(PlayedTime(game.PlayedMs))).Class("game__played")
                ).Class("game__top"),
                new Div().Class("game__screen").Attribute("id", "game-screen"),
                new P(new Text("game.saveFailed")).Class("game__note").Attribute("data-role", "save-error").Attribute("role", "status").Attribute("hidden", "hidden")
            ).Class("game")
                .Attribute("data-api", "/api/v1/games/assets/" + asset)
                .Attribute("data-file", "/api/v1/games/assets/" + asset + "/files/" + Uri.EscapeDataString(game.FileName))
                .Attribute("data-emulator", emulator)
                .Attribute("data-title", game.Title)
                .Attribute("data-language", texts.Locale)
                .Attribute("data-antiforgery", antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "")
                .Attribute("data-state-saved", texts["game.stateSaved"])
                .Attribute("data-state-missing", texts["game.stateMissing"]));
        }

        private string PlayedTime(long playedMs)
        {
            var minutes = (long)TimeSpan.FromMilliseconds(playedMs).TotalMinutes;
            return minutes < 60
                ? string.Format(CultureInfo.CurrentCulture, texts["game.playedMinutes"], minutes)
                : string.Format(CultureInfo.CurrentCulture, texts["game.playedHours"], minutes / 60, minutes % 60);
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();

            // Everything the emulator loads comes from Jularr; its fallback to a public CDN stays closed.
            Response.Headers.ContentSecurityPolicy = "connect-src 'self' blob: data:";
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _game = await read.ExecuteAsync<GameV1>(new GameParametersV1(Id), HttpContext.RequestAborted);
        }
    }
}
