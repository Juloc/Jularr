using Jularr.Web.Data;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Music;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Music;

/// <summary>One artist: the albums known of them, newest first, and which are in the library.</summary>
public sealed class ArtistModel(AppDbContext db, MusicQuery query) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public MusicArtistView? View { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        View = await query.GetArtistAsync(id, cancellationToken);
        return View is null ? NotFound() : Page();
    }

    public string StateLabel(MusicAlbumState state) => Ui[$"admin.music.state.{state.ToString().ToLowerInvariant()}"];

    public string TypeLabel(MusicAlbumType type) => Ui[$"admin.music.type.{type.ToString().ToLowerInvariant()}"];

    public static string StateTone(MusicAlbumState state) =>
        state switch
        {
            MusicAlbumState.Available => "success",
            MusicAlbumState.Partial or MusicAlbumState.Downloading or MusicAlbumState.Requested => "warning",
            MusicAlbumState.Failed => "danger",
            _ => "neutral"
        };
}
