using System.Globalization;
using Jularr.Backend.Data.User.Game.V1;
using Jularr.Backend.Service.User.Game.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Game
{
    // A retro game in EmulatorJS, served by Jularr itself; saves and play time follow the profile. A file without its game yet plays with the browser's own saves.
    public sealed class GameModel(ReadGameV1 read, ReadGameStoredFileV1 readFile, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private GameV1? _game;
        private GameLibraryFileV1? _file;

        // The game's file (/Game/{id}) or a file of a games library (/Game/Files/{file}).
        [BindProperty(SupportsGet = true)]
        public long? Id { get; set; }

        [BindProperty(SupportsGet = true)]
        public long? File { get; set; }

        protected override void Define(Page page)
        {
            page.Title("game.title");
            page.Shell = false;
            page.Asset("Modules/Game/game.entry.ts");
            if (File is not null)
            {
                DefineFile(page);
                return;
            }

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

        private void DefineFile(Page page)
        {
            if (_file is not { Emulator: { } emulator } file)
            {
                page.Content.Add(new Div(
                    new P(new Text(_file is null ? FormError?.ResourceKey ?? "game.notFound" : _file.NeedsBios ? "game.files.needsBios" : "game.unsupported")).Attribute("role", "alert"),
                    new A(new Text("common.back")).Class("button").Attribute("href", "/Games/Files")
                ).Class("game__status"));
                return;
            }

            var id = file.StoredFileId.ToString(CultureInfo.InvariantCulture);
            page.Content.Add(new Div(
                new Div(
                    new A(new Text("common.back")).Class("button").Attribute("href", "/Games/Files"),
                    new H1(new ValueText(file.FileName)).Class("game__title")
                ).Class("game__top"),
                new Div().Class("game__screen").Attribute("id", "game-screen"),
                new P(new Text("game.browserSaves")).Class("game__note")
            ).Class("game")
                .Attribute("data-file", "/api/v1/games/files/" + id + "/content/" + Uri.EscapeDataString(file.FileName))
                .Attribute("data-emulator", emulator)
                .Attribute("data-title", Path.GetFileNameWithoutExtension(file.FileName))
                .Attribute("data-language", texts.Locale));
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

            // A file that has its game plays as the game, with the profile's saves.
            if (_file?.MediaAssetId is { } asset)
            {
                return Redirect("/Game/" + asset.ToString(CultureInfo.InvariantCulture));
            }

            PreparePage();

            // Everything the emulator loads comes from Jularr; its fallback to a public CDN stays closed.
            Response.Headers.ContentSecurityPolicy = "connect-src 'self' blob: data:";
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            if (File is { } file)
            {
                _file = await readFile.ExecuteAsync<GameLibraryFileV1>(new GameStoredFileParametersV1(file), HttpContext.RequestAborted);
                return;
            }

            _game = await read.ExecuteAsync<GameV1>(new GameParametersV1(Id ?? 0), HttpContext.RequestAborted);
        }
    }
}
