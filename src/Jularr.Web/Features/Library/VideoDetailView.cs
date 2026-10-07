using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Watchlist;

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

    /// <summary>A Movie runtime the way Library cards and the Movie hero write it in the viewer's language: "1h 52m", "48m".</summary>
    public static string RuntimeText(int minutes, UiTextBundle ui) => minutes >= 60
        ? ui.Format("library.video.runtimeHoursMinutes", ("hours", minutes / 60), ("minutes", (minutes % 60).ToString("00", CultureInfo.InvariantCulture)))
        : ui.Format("library.video.runtimeMinutes", ("minutes", minutes));

    /// <summary>A provider rating on the 0-10 scale with one decimal in the viewer's number format.</summary>
    public static string RatingText(double rating, CultureInfo culture) => Math.Round(rating, 1, MidpointRounding.AwayFromZero).ToString("0.0", culture);

    /// <summary>A vote count in the short form of a rating fact in the viewer's number format: 640, 12K, 1.2M.</summary>
    public static string CompactCount(int count, CultureInfo culture) => count switch
    {
        >= 1_000_000 => $"{(count / 1_000_000d).ToString("0.#", culture)}M",
        >= 1_000 => $"{(count / 1_000d).ToString("0.#", culture)}K",
        _ => count.ToString(culture)
    };

    /// <summary>
    /// The initials of a person for the portrait placeholder: the first letter or digit of the first and of the last word, or one for a
    /// single word. Markup-like text of a provider and characters that are not letters are skipped, never shown.
    /// </summary>
    public static string Initials(string name)
    {
        var initials = Regex.Replace(name, "<[^>]*>", " ")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(WatchlistLabels.FirstLetterOrDigit)
            .OfType<string>()
            .ToArray();
        return initials.Length switch
        {
            0 => "·",
            1 => initials[0].ToUpperInvariant(),
            _ => (initials[0] + initials[^1]).ToUpperInvariant()
        };
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
        WorkMediaType.Music => "library.mediaCard.kind.music",
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
