using System.Globalization;
using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Library;

/// <summary>Pure presentation rules behind the Movie and Series detail pages (docs/mockups/movie-detail and anime-series-detail). The primary action itself is resolved by <see cref="PrimaryActionResolver"/>.</summary>
public static class VideoDetailView
{
    public const string WatchPath = "/Library/Watch";

    /// <summary>How many genres the hero names; the About section lists every genre when there are more.</summary>
    public const int HeroGenreCount = 3;

    /// <summary>The culture of a UI locale; the invariant culture when the locale is empty or unknown.</summary>
    public static CultureInfo CultureOf(string locale)
    {
        try
        {
            return string.IsNullOrWhiteSpace(locale) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(locale);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    /// <summary>A Movie runtime the way Library cards and the Movie hero write it: "1h 52m", "48m".</summary>
    public static string RuntimeText(int minutes) => minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes}m";

    /// <summary>A provider rating on the 0-10 scale with one decimal, as every rating chip writes it.</summary>
    public static string RatingText(double rating) => rating.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>A vote count in the short form of a rating fact: 640, 12K, 1.2M.</summary>
    public static string CompactCount(int count) => count switch
    {
        >= 1_000_000 => $"{(count / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture)}M",
        >= 1_000 => $"{(count / 1_000d).ToString("0.#", CultureInfo.InvariantCulture)}K",
        _ => count.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>The initials of a person for the portrait placeholder: the first letters of the first and the last word, or one letter for a single word.</summary>
    public static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return "·";
        }

        var first = StringInfo.GetNextTextElement(words[0]);
        return (words.Length == 1 ? first : first + StringInfo.GetNextTextElement(words[^1])).ToUpperInvariant();
    }

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
