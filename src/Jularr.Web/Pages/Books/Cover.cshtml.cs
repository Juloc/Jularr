using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Books;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Books;

public sealed class CoverModel(
    BookCatalogService books,
    BesideMediaArtworkCache artworkCache) : PageModel
{
    private const string MissingCoverSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" width="300" height="450" viewBox="0 0 300 450">
          <rect width="300" height="450" fill="#dedee3"/>
          <g fill="none" stroke="#858593" stroke-width="10" stroke-linecap="round" stroke-linejoin="round">
            <rect x="75" y="157" width="150" height="135" rx="12"/>
            <circle cx="128" cy="197" r="12"/>
            <path d="m87 271 43-39 27 26 21-17 35 30"/>
            <path d="M82 150 218 299"/>
          </g>
        </svg>
        """;
    public async Task<IActionResult> OnGetAsync(Guid id, int? w, CancellationToken cancellationToken)
    {
        // A requested width serves a small, locally cached WebP thumbnail (#570): generated once
        // from the canonical cover and then served from /data, so library rows keep rendering even
        // when the NAS is offline. Without a width the full canonical cover is served.
        if (w is int width and > 0)
        {
            var thumbnail = await artworkCache.GetThumbnailForSourceAsync(
                $"book-{id:N}",
                () => books.GetLocalCoverPathAsync(id, knownStoragePath: null, cancellationToken),
                width,
                cancellationToken);

            if (thumbnail is not null && System.IO.File.Exists(thumbnail.Path))
            {
                Response.Headers.CacheControl = "public,max-age=604800";
                return PhysicalFile(thumbnail.Path, thumbnail.MediaType);
            }
        }
        else
        {
            var path = await books.GetLocalCoverPathAsync(id, knownStoragePath: null, cancellationToken);
            if (path is not null && System.IO.File.Exists(path))
            {
                return PhysicalFile(path, BookCatalogService.GetCoverContentType(path));
            }
        }

        Response.Headers.CacheControl = "private,max-age=60";
        return Content(MissingCoverSvg, "image/svg+xml");
    }
}
