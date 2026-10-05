using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Library;

/// <summary>Pure presentation rules behind the Movie and Series detail pages (docs/mockups/movie-detail and anime-series-detail). The primary action itself is resolved by <see cref="PrimaryActionResolver"/>.</summary>
public static class VideoDetailView
{
    public const string WatchPath = "/Library/Watch";

    /// <summary>The canonical web player of a Movie (a Work) or one episode (a WorkEpisode).</summary>
    public static string WatchHref(Guid workId, Guid? workEpisodeId) => workEpisodeId is { } episodeId ? $"{WatchPath}/{workId}/{episodeId}" : $"{WatchPath}/{workId}";

    /// <summary>The catalog key of a media type's name, for the hero, the Request dialog's identity and related Works.</summary>
    public static string KindKey(WorkMediaType mediaType) => mediaType switch
    {
        WorkMediaType.Movie => "library.mediaCard.kind.movie",
        WorkMediaType.Series => "library.mediaCard.kind.series",
        WorkMediaType.Anime => "library.mediaCard.kind.anime",
        WorkMediaType.Manga => "library.mediaCard.kind.manga",
        WorkMediaType.LightNovel => "library.mediaCard.kind.lightNovel",
        _ => "library.mediaCard.kind.book"
    };

    /// <summary>The catalog key of the hero's play-style action.</summary>
    public static string PlayKey(PrimaryAction action) => action.Kind switch
    {
        PrimaryActionKind.Continue => "library.mediaCard.continueWatching",
        PrimaryActionKind.StartWatching => "library.mediaCard.startWatching",
        PrimaryActionKind.WatchNow => "library.video.watchNow",
        PrimaryActionKind.Play when action.IsRewatch => "library.mediaCard.watchAgain",
        _ => "library.watch.play"
    };

    /// <summary>The episodes a Request for missing content would cover: no file yet, not already part of an open request.</summary>
    public static IReadOnlyList<VideoDetailEpisode> RequestableEpisodes(IEnumerable<VideoDetailEpisode> episodes) => [.. episodes.Where(x => x.Availability == AnimeEpisodeAvailability.Unavailable)];
}
