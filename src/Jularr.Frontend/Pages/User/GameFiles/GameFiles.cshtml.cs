using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.User.Game.V1;
using Jularr.Backend.Service.User.Game.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.GameFiles
{
    // Every file of the games libraries with its system and size; what the browser plays starts here, a file with its game through the game's saves.
    public sealed class GameFilesModel(ReadGameFilesV1 read) : JularrPage
    {
        private const int PageSize = 100;
        private PageResult<GameLibraryFileV1>? _files;

        [BindProperty(SupportsGet = true, Name = PagedList.DefaultKey)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("game.files.title");
            page.Parent("nav.library", "/Library?type=games");
            page.Navigation("nav.library");
            page.Asset("Pages/User/GameFiles/GameFiles.entry.scss");
            if (_files is not { } files)
            {
                return;
            }

            if (files.Items.Count == 0)
            {
                page.Content.Add(new P(new Text("game.files.empty")).Attribute("role", "status"));
            }

            page.Content.Add(new Table<GameLibraryFileV1>(files.Items)
                .Content("game.files.name", Name)
                .Content("game.files.system", file => new ValueText(file.System ?? "–"))
                .Content("game.files.size", file => new ValueText(ByteSize.Format(file.SizeBytes)))
                .Content("game.files.play", Play)
                .Paged(PageSize));
        }

        // The file's name, and its game where it has one.
        private static IUiComponent Name(GameLibraryFileV1 file) => file.WorkId is { } workId && file.Title is { } title
            ? new Div(new Span(new ValueText(file.FileName)), new A(new ValueText(title)).Class("game-files__work").Attribute("href", "/Details/" + workId.ToString(CultureInfo.InvariantCulture)))
                .Class("game-files__name")
            : new Span(new ValueText(file.FileName)).Class("game-files__name");

        private static IUiComponent Play(GameLibraryFileV1 file)
        {
            if (file.Emulator is null)
            {
                return new Span(new Text(file.NeedsBios ? "game.files.needsBios" : "game.files.notPlayable")).Class("game-files__note");
            }

            var href = file.MediaAssetId is { } asset ? "/Game/" + asset.ToString(CultureInfo.InvariantCulture) : "/Game/Files/" + file.StoredFileId.ToString(CultureInfo.InvariantCulture);
            return new A(new Text("game.files.start")).Class("button button--compact").Attribute("href", href);
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _files = await read.ExecuteAsync<PageResult<GameLibraryFileV1>>(new ReadGameFilesParametersV1(Math.Max(1, PageNumber), PageSize), HttpContext.RequestAborted);
        }
    }
}
