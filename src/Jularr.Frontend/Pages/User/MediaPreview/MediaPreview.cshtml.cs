using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.User.Discover.V1;
using Jularr.Backend.Service.User.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Frontend.Pages.User.MediaPreview
{
    // A card's delayed hover preview as an HTML fragment: the description and, where the title has one, its trailer.
    // A Work comes from the library. A catalog or TMDB title gets its description from the local catalog at once; a provider that answers within
    // a short time adds the trailer (TMDB titles, anime), one that does not leaves the description alone.
    public sealed class MediaPreviewModel(ReadWorkV1 readWork, ReadDiscoverScopesV1 readScopes, ReadDiscoverCatalogEntryV1 readCatalogEntry, ReadDiscoverItemV1 readItem,
        ReadDiscoverPreviewV1 readLocal, UiText texts, FrontendAssets assets) : PageModel
    {
        private static readonly TimeSpan ProviderLimit = TimeSpan.FromSeconds(3);

        [BindProperty(SupportsGet = true, Name = "work")]
        public long? WorkId { get; set; }

        [BindProperty(SupportsGet = true, Name = "scope")]
        public string? Scope { get; set; }

        [BindProperty(SupportsGet = true, Name = "namespace")]
        public string? Namespace { get; set; }

        [BindProperty(SupportsGet = true, Name = "id")]
        public string? ExternalId { get; set; }

        [BindProperty(SupportsGet = true, Name = "type")]
        public MediaType? Type { get; set; }

        [BindProperty(SupportsGet = true, Name = "tmdb")]
        public int? TmdbId { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!texts.CanReadMedia()) return Forbid();

            var token = HttpContext.RequestAborted;
            if (WorkId is { } workId)
            {
                var work = await readWork.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(workId), token);
                return Fragment(work.CanonicalTitle, "/Details/" + workId.ToString(CultureInfo.InvariantCulture), work.BackdropUrl, work.PosterUrl, work.Overview, work.TrailerKey,
                    MediaCardPreview.Facts(texts, work.ReleaseYear, work.MediaTypeId, work.MediaSubtypeId, work.RuntimeMinutes));
            }

            if (Scope is not null && Namespace is not null && ExternalId is not null)
            {
                var scope = (await readScopes.ExecuteAsync<BaseDataList<DiscoverScopeV1>>(new DiscoverParametersV1(), token)).FirstOrDefault(item => item.Slug == Scope && item.IsCatalog);
                if (scope is null) return NotFound();

                var href = "/Discover/" + scope.Slug + "/" + Uri.EscapeDataString(Namespace) + "/" + Uri.EscapeDataString(ExternalId);

                // Only an anime season has a trailer to ask its provider for.
                if (scope.MediaSubtypeId == MediaSubtype.Anime)
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                    limit.CancelAfter(ProviderLimit);
                    try
                    {
                        var entry = await readCatalogEntry.ExecuteAsync<DiscoverCatalogEntryV1>(new DiscoverParametersV1(),
                            new ReadDiscoverCatalogEntryDataV1(scope.MediaTypeId, Namespace, ExternalId, scope.MediaSubtypeId), limit.Token);
                        return Fragment(entry.Title, entry.WorkId is { } known ? "/Details/" + known.ToString(CultureInfo.InvariantCulture) : href, entry.BackdropUrl, entry.PosterUrl, entry.Overview, entry.TrailerKey,
                            MediaCardPreview.Facts(texts, entry.ReleaseYear, scope.MediaTypeId, scope.MediaSubtypeId, null));
                    }
                    catch (Exception exception) when (IsProviderFailure(exception, token))
                    {
                        // The description of the local catalog remains.
                    }
                }

                return await LocalAsync(Namespace, ExternalId, href, scope.MediaTypeId, scope.MediaSubtypeId, token);
            }

            if (Type is MediaType.Movie or MediaType.Series && TmdbId is { } tmdbId)
            {
                var href = "/Discover/" + (Type == MediaType.Movie ? "movies" : "series") + "/" + tmdbId.ToString(CultureInfo.InvariantCulture);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(ProviderLimit);
                try
                {
                    var item = await readItem.ExecuteAsync<DiscoverItemV1>(new DiscoverItemParametersV1(Type.Value, tmdbId), default, limit.Token);
                    return Fragment(item.Title, item.WorkId is { } known ? "/Details/" + known.ToString(CultureInfo.InvariantCulture) : href, item.BackdropUrl, item.PosterUrl, item.Overview, item.TrailerKey,
                        MediaCardPreview.Facts(texts, item.ReleaseYear, item.MediaTypeId, null, null));
                }
                catch (Exception exception) when (IsProviderFailure(exception, token))
                {
                    // The description of the local catalog remains.
                }

                return await LocalAsync(Type == MediaType.Movie ? "tmdb_movie" : "tmdb_tv", tmdbId.ToString(CultureInfo.InvariantCulture), href, Type.Value, null, token);
            }

            return BadRequest();
        }

        private static bool IsProviderFailure(Exception exception, CancellationToken requestAborted) =>
            exception is ServiceUnavailableException || exception is OperationCanceledException && !requestAborted.IsCancellationRequested;

        // What the local catalog keeps of the title; a title it does not know needs no preview.
        private async Task<IActionResult> LocalAsync(string @namespace, string externalId, string href, MediaType type, MediaSubtype? subtype, CancellationToken token)
        {
            try
            {
                var local = await readLocal.ExecuteAsync<DiscoverPreviewV1>(new ReadDiscoverPreviewParametersV1(@namespace, externalId), token);
                return Fragment(local.Title, href, local.BackdropUrl, local.PosterUrl, local.Overview, null, MediaCardPreview.Facts(texts, local.ReleaseYear, type, subtype, null));
            }
            catch (KeyNotFoundException)
            {
                return new NoContentResult();
            }
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
