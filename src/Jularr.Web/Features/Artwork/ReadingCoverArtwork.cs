using Jularr.Web.Features.Storage;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.Novels;

namespace Jularr.Web.Features.Artwork;

/// <summary>
/// The cover of a Light Novel or Manga series kept beside its media on the media type's NAS library
/// root (issue #581), through the one <see cref="BesideMediaArtworkStore"/> write/read path Books
/// and anime use -- there is no second artwork write path for reading media. A matched series
/// carries its provider cover URL until the cover has been stored beside the media; from then on
/// the series carries Jularr's own cover route (<see cref="RouteFor"/>), which serves the
/// beside-media file through the local thumbnail cache (#570) so covers keep rendering while the NAS
/// is asleep. Without a library root, or before the series is on it, nothing changes: the provider
/// URL stays, exactly as before.
/// </summary>
public sealed class ReadingCoverArtwork(
    AppDbContext db,
    AnimeImportSettingsStore importSettings,
    BesideMediaArtworkStore store,
    BesideMediaArtworkCache cache,
    IHttpClientFactory httpClientFactory,
    ILogger<ReadingCoverArtwork> logger,
    LibraryRootRoutingService? routing = null)
{
    /// <summary>The beside-media artwork kind: <c>cover.*</c> in the series folder.</summary>
    public const string CoverKind = "cover";

    /// <summary>Width of the cached WebP derivative served when a request names none.</summary>
    public const int DefaultThumbnailWidth = 512;

    /// <summary>The route Jularr serves a series' beside-media cover from.</summary>
    public static string RouteFor(MediaAcquisitionKind kind, Guid workId) =>
        kind switch
        {
            MediaAcquisitionKind.LightNovel => $"/Novels/Cover/{workId}",
            MediaAcquisitionKind.Manga => $"/Manga/Cover/{workId}",
            _ => throw Unsupported(kind)
        };

    private static ArgumentOutOfRangeException Unsupported(MediaAcquisitionKind kind) =>
        new(nameof(kind), kind, "Only Light Novels and Manga keep series covers here.");

    private static string ScopeFor(MediaAcquisitionKind kind) =>
        kind switch
        {
            MediaAcquisitionKind.LightNovel => MediaArtworkScopes.LightNovel,
            MediaAcquisitionKind.Manga => MediaArtworkScopes.Manga,
            _ => throw Unsupported(kind)
        };

    /// <summary>
    /// Stores a series' provider cover beside its media and returns the cover URL the series should
    /// now carry: <see cref="RouteFor"/> once the cover lives there (also when the user's own
    /// <c>cover.*</c> is already in the folder, which is never replaced), otherwise
    /// <paramref name="providerCoverUrl"/> unchanged -- no library root configured, the series not
    /// yet on it, the NAS unavailable, or an unusable image. Never throws for those cases, so the
    /// metadata step this runs in still succeeds.
    /// </summary>
    public async Task<string?> PersistProviderCoverAsync(
        MediaAcquisitionKind kind,
        Guid workId,
        string? providerCoverUrl,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(providerCoverUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return providerCoverUrl;
        }

        var folder = await ResolveFolderAsync(kind, workId, cancellationToken);
        if (folder is null)
        {
            return providerCoverUrl;
        }

        byte[]? bytes;
        try
        {
            using var client = httpClientFactory.CreateClient(AnimeArtworkLibrary.HttpClientName);
            bytes = await AnimeArtworkFiles.DownloadImageAsync(client, uri, cancellationToken);
        }
        catch (Exception exception) when (
            exception is HttpRequestException ||
            (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogDebug(exception, "Provider cover {Url} could not be downloaded.", providerCoverUrl);
            return providerCoverUrl;
        }

        if (bytes is null)
        {
            return providerCoverUrl;
        }

        var scope = ScopeFor(kind);
        var outcome = await store.PersistAsync(
            scope,
            workId,
            CoverKind,
            folder,
            bytes,
            MediaArtworkSources.Provider,
            providerCoverUrl,
            cancellationToken);
        if (outcome == ArtworkPersistOutcome.Failed)
        {
            logger.LogWarning(
                "The {Kind} cover of {WorkId} could not be written beside its media in {Folder}.",
                kind,
                workId,
                folder);
        }

        return outcome is ArtworkPersistOutcome.Saved or ArtworkPersistOutcome.Current or ArtworkPersistOutcome.KeptCustom &&
               await store.ResolveAsync(scope, workId, CoverKind, folder, cancellationToken) is not null
            ? RouteFor(kind, workId)
            : providerCoverUrl;
    }

    /// <summary>The cover file beside the series' media, or null (no root, not on it, NAS offline).</summary>
    public async Task<string?> ResolveAsync(
        MediaAcquisitionKind kind,
        Guid workId,
        CancellationToken cancellationToken)
    {
        var folder = await ResolveFolderAsync(kind, workId, cancellationToken);
        return folder is null
            ? null
            : await store.ResolveAsync(ScopeFor(kind), workId, CoverKind, folder, cancellationToken);
    }

    /// <summary>
    /// The cover as a small cached WebP derivative (#570), generated once from the beside-media file
    /// and then served from <c>/data</c> -- including while the NAS is offline or the media root was
    /// removed. Null only when there is neither a derivative nor a source file.
    /// </summary>
    public Task<ArtworkThumbnail?> GetThumbnailAsync(
        MediaAcquisitionKind kind,
        Guid workId,
        int? width,
        CancellationToken cancellationToken) =>
        cache.GetThumbnailForSourceAsync(
            BesideMediaArtworkCache.CacheKey(ScopeFor(kind), workId, CoverKind),
            () => ResolveAsync(kind, workId, cancellationToken),
            width is > 0 ? width.Value : DefaultThumbnailWidth,
            cancellationToken);

    private async Task<string?> ResolveFolderAsync(
        MediaAcquisitionKind kind,
        Guid workId,
        CancellationToken cancellationToken)
    {
        var settings = await importSettings.LoadAsync(cancellationToken);
        if (routing is not null)
        {
            settings = await routing.WithRoutedLibrariesAsync(settings, cancellationToken);
        }

        return kind switch
        {
            MediaAcquisitionKind.LightNovel => await NovelArtworkFolders.ResolveAsync(db, settings, workId, cancellationToken),
            MediaAcquisitionKind.Manga => await MangaArtworkFolders.ResolveAsync(db, settings, workId, cancellationToken),
            _ => throw Unsupported(kind)
        };
    }
}
