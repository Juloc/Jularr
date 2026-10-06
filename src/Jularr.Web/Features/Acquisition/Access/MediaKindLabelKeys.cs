namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>
/// UI catalog keys for the per-media-type labels. Every switch is exhaustive on purpose: a new
/// <see cref="MediaAcquisitionKind"/> value must get a label here instead of falling through to
/// another media type's text or to the raw enum name.
/// </summary>
public static class MediaKindLabelKeys
{
    /// <summary>The generic media type name (plural), shared by requests, path mappings and folders.</summary>
    public static string Name(MediaAcquisitionKind kind) =>
        $"admin.requests.kind.{AcquisitionAccessNames.Kind(kind)}";

    /// <summary>The singular media type value shown on an operation.</summary>
    public static string Operation(MediaAcquisitionKind kind) => kind switch
    {
        MediaAcquisitionKind.Anime => "admin.operation.media.anime",
        MediaAcquisitionKind.Manga => "admin.operation.media.manga",
        MediaAcquisitionKind.LightNovel => "admin.operation.media.lightNovel",
        MediaAcquisitionKind.Book => "admin.operation.media.book",
        MediaAcquisitionKind.Movie => "admin.operation.media.movie",
        MediaAcquisitionKind.Tv => "admin.operation.media.tv",
        MediaAcquisitionKind.Audiobook => "admin.operation.media.audiobook",
        MediaAcquisitionKind.Music => "admin.operation.media.music",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>The sub-heading of a media type in the Media folders panel.</summary>
    public static string Folders(MediaAcquisitionKind kind) => kind switch
    {
        MediaAcquisitionKind.Manga => "settings.acquisition.mediaFolders.manga",
        MediaAcquisitionKind.LightNovel => "settings.acquisition.mediaFolders.lightNovel",
        MediaAcquisitionKind.Book => "settings.acquisition.mediaFolders.book",
        MediaAcquisitionKind.Anime
            or MediaAcquisitionKind.Movie
            or MediaAcquisitionKind.Tv
            or MediaAcquisitionKind.Audiobook
            or MediaAcquisitionKind.Music => Name(kind),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>The label of a media type's download client category.</summary>
    public static string ClientCategory(MediaAcquisitionKind kind) => kind switch
    {
        MediaAcquisitionKind.Anime => "admin.usenet.animeCategory",
        MediaAcquisitionKind.Book => "admin.usenet.booksCategory",
        MediaAcquisitionKind.Manga => "settings.downloadClients.field.mangaCategory",
        MediaAcquisitionKind.LightNovel => "settings.downloadClients.field.lightNovelCategory",
        MediaAcquisitionKind.Movie => "admin.usenet.movieCategory",
        MediaAcquisitionKind.Tv => "admin.usenet.tvCategory",
        MediaAcquisitionKind.Audiobook => "admin.usenet.audiobookCategory",
        MediaAcquisitionKind.Music => "admin.usenet.musicCategory",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
