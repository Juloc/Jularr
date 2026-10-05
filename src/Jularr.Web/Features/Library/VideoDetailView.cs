using Jularr.Web.Features.MediaCore;
using Jularr.Web.Ui;

namespace Jularr.Web.Features.Library;

public enum VideoHeroKind
{
    Play,
    Continue,
    WatchAgain,

    /// <summary>Nothing is playable, the profile may request it and nothing is requested yet.</summary>
    Request,

    /// <summary>Nothing is playable and a request is open: the hero shows the request's state instead of an action.</summary>
    RequestState,

    None
}

/// <summary>The one primary action of a detail hero, derived from the canonical state: <see cref="Href"/> is the player for the play kinds.</summary>
public sealed record VideoHero(VideoHeroKind Kind, string? Href, VideoDetailEpisode? Episode);

/// <summary>Pure rules behind the Movie and Series detail pages (docs/mockups/movie-detail and anime-series-detail).</summary>
public static class VideoDetailView
{
    public const string WatchPath = "/Library/Watch";

    /// <summary>The canonical web player of a Movie (a Work) or one episode (a WorkEpisode).</summary>
    public static string WatchHref(Guid workId, Guid? workEpisodeId) => workEpisodeId is { } episodeId ? $"{WatchPath}/{workId}/{episodeId}" : $"{WatchPath}/{workId}";

    /// <summary>
    /// Play or Continue when something is playable (a Movie resumes from its saved position, a Series continues with the
    /// next playable unfinished episode); otherwise Request when the profile may request, otherwise the open request's
    /// state. The visible acquisition action is always Request, whatever approves it.
    /// </summary>
    public static VideoHero Hero(VideoDetail detail, bool canRequest)
    {
        ArgumentNullException.ThrowIfNull(detail);

        if (detail.MediaType == WorkMediaType.Movie && detail.Versions.Count > 0)
        {
            var kind = detail.MovieProgress switch
            {
                { IsCompleted: true } => VideoHeroKind.WatchAgain,
                { ResumePositionMs: > 0 } => VideoHeroKind.Continue,
                _ => VideoHeroKind.Play
            };
            return new VideoHero(kind, WatchHref(detail.WorkId, null), null);
        }

        if (detail.Next is { } next && detail.Episodes.FirstOrDefault(x => x.Id == next.EpisodeId) is { } episode)
        {
            var kind = next.State switch
            {
                MediaBannerProgressState.InProgress => VideoHeroKind.Continue,
                MediaBannerProgressState.Completed => VideoHeroKind.WatchAgain,
                _ => VideoHeroKind.Play
            };
            return new VideoHero(kind, WatchHref(detail.WorkId, episode.Id), episode);
        }

        if (detail.Request?.Open is not null)
        {
            return new VideoHero(VideoHeroKind.RequestState, null, null);
        }

        return new VideoHero(canRequest && detail.Request is not null ? VideoHeroKind.Request : VideoHeroKind.None, null, null);
    }

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

    /// <summary>The catalog key of the hero's play action.</summary>
    public static string PlayKey(VideoHeroKind kind) => kind switch
    {
        VideoHeroKind.Continue => "library.mediaCard.continueWatching",
        VideoHeroKind.WatchAgain => "library.mediaCard.watchAgain",
        _ => "library.watch.play"
    };

    /// <summary>The episodes a Request for missing content would cover: no file yet, not already part of an open request.</summary>
    public static IReadOnlyList<VideoDetailEpisode> RequestableEpisodes(IEnumerable<VideoDetailEpisode> episodes) => [.. episodes.Where(x => x.Availability == AnimeEpisodeAvailability.Unavailable)];
}
