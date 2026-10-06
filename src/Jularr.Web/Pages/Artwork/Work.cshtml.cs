using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Shell;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Artwork;

/// <summary>
/// Serves one locally cached artwork variant of a Work (#820) from the Jularr-owned derivative cache; it never contacts a provider.
/// A Work of a media type the profile cannot browse is a 404, exactly like its detail page. The address carries the variant's
/// cache version, so the private response can be kept by the browser for good.
/// </summary>
public sealed class WorkModel(WorkMetadataStore store, WorkArtworkCache cache, IAppShellService appShell) : PageModel
{
    public async Task<IActionResult> OnGetAsync(Guid workId, long artworkId, CancellationToken cancellationToken)
    {
        if (await store.FindArtworkFileAsync(workId, artworkId, cancellationToken) is not { } variant)
        {
            return NotFound();
        }

        var access = await appShell.GetMediaAccessAsync(User, cancellationToken);
        if (!access.IsVisible(variant.MediaType) || cache.PathFor(variant.CacheKey) is not { } path || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private,max-age=31536000,immutable";
        return PhysicalFile(path, "image/webp");
    }
}
