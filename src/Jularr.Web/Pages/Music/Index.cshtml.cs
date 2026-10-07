using Jularr.Web.Data;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Music;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Music;

/// <summary>The Music library: the artists Jularr knows and how much of their discography is in the library. Playback is not offered here.</summary>
public sealed class IndexModel(AppDbContext db, MusicQuery query) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public IReadOnlyList<MusicArtistRow> Artists { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Artists = await query.ListArtistsAsync(cancellationToken);
    }

    /// <summary>The initial of an artist as the cover tile shows it: MusicBrainz provides no artwork, so a letter stands in.</summary>
    public static string Initial(string name) => name.Length == 0 ? "♪" : char.ToUpperInvariant(name.TrimStart()[0]).ToString();
}
