using Jularr.Web.Features.Artwork;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Artwork;

public sealed class AnimeModel : PageModel
{
    public IActionResult OnGet(Guid id, string kind)
    {
        if (!AnimeArtworkSlot.TryParse(kind, out var slot))
        {
            return NotFound();
        }

        var path = AnimeArtworkCache.Default.FindPath(id, slot);
        if (path is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        Response.Headers.XContentTypeOptions = "nosniff";
        return PhysicalFile(
            path,
            AnimeArtworkFiles.GetContentType(path));
    }
}
