using System.Globalization;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.User.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Frontend.Pages.User.MediaPreview
{
    // A card's delayed hover preview as an HTML fragment: the Work's description and, where it has one, its trailer; a catalog title without details shows what its catalog says.
    public sealed class MediaPreviewModel(ReadWorkV1 readWork, UiText texts, FrontendAssets assets) : PageModel
    {
        [BindProperty(SupportsGet = true, Name = "work")]
        public long? WorkId { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!texts.CanReadMedia()) return Forbid();
            if (WorkId is not { } workId) return BadRequest();

            var work = await readWork.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(workId), HttpContext.RequestAborted);
            return Fragment(work.CanonicalTitle, "/Details/" + workId.ToString(CultureInfo.InvariantCulture), work.BackdropUrl, work.PosterUrl, work.Overview, work.TrailerKey,
                MediaCardPreview.Facts(texts, work.ReleaseYear, work.MediaTypeId, work.MediaSubtypeId, work.RuntimeMinutes));
        }

        // A title without anything to show in the preview needs none.
        private IActionResult Fragment(string title, string href, string? backdropUrl, string? posterUrl, string? overview, string? trailerKey, IReadOnlyList<string> facts) =>
            string.IsNullOrWhiteSpace(overview) && TrailerId(trailerKey) is null
                ? new NoContentResult()
                : Partial("_UiComponent", new MediaCardPreview(assets, title, href, backdropUrl, posterUrl, overview, TrailerId(trailerKey), facts));

        // Only a well-formed YouTube video key is embedded.
        private static string? TrailerId(string? key) =>
            key is { Length: 11 } && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ? key : null;
    }
}
