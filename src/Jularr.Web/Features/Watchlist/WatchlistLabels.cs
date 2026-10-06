using System.Globalization;

namespace Jularr.Web.Features.Watchlist;

/// <summary>Display helpers for followed works; provider values are never shown raw.</summary>
public static class WatchlistLabels
{
    /// <summary>
    /// The UI key of an AniList format, or null when the media type already says it (MANGA,
    /// NOVEL) or the format is unknown.
    /// </summary>
    public static string? FormatKey(string? format) => format?.Trim().ToUpperInvariant() switch
    {
        "TV" => "watchlist.format.tv",
        "TV_SHORT" => "watchlist.format.tvShort",
        "MOVIE" => "watchlist.format.movie",
        "SPECIAL" => "watchlist.format.special",
        "OVA" => "watchlist.format.ova",
        "ONA" => "watchlist.format.ona",
        "MUSIC" => "watchlist.format.music",
        "ONE_SHOT" => "watchlist.format.oneShot",
        _ => null
    };

    /// <summary>The first letter or digit of a title, for a cover placeholder; provider text that starts with markup or punctuation never becomes the initial.</summary>
    public static string Initial(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "" : FirstLetterOrDigit(title) ?? "·";

    /// <summary>The first text element of <paramref name="text"/> that is a letter or a digit, or null.</summary>
    public static string? FirstLetterOrDigit(string text)
    {
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            if (char.IsLetterOrDigit(element, 0))
            {
                return element;
            }
        }

        return null;
    }
}
