using System.Globalization;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Playback;

namespace Jularr.Web.Ui;

public enum MediaBannerKind
{
    Anime,
    Series,
    Movie,
    Manga,
    LightNovel,
    Book
}

public enum MediaBannerUnit
{
    Episode,
    Chapter,
    Volume
}

public enum MediaReleaseStatus
{
    Ongoing,
    Finished,
    Upcoming,
    Hiatus,
    Cancelled
}

public enum MediaBannerProgressState
{
    NotStarted,
    InProgress,
    Completed
}

/// <summary>
/// Where the owner stands in one title. <see cref="NextUrl"/> opens the next unit directly
/// (the resume target, the next unwatched/unread unit, or the first unit when nothing has been
/// started or everything is done). <see cref="NextSeason"/> is set only when the title spans
/// several seasons, so the label can name the season instead of an ambiguous "of N".
/// </summary>
public sealed record MediaBannerProgress(
    MediaBannerProgressState State,
    MediaBannerUnit Unit,
    double NextNumber,
    string NextUrl,
    int? TotalUnits = null,
    int? NextSeason = null,
    int? Percent = null);

/// <summary>
/// Facts behind one "Media Banner" card. Everything except kind, title and link is optional:
/// the card renders only what is present and never invents a value.
/// </summary>
/// <param name="ProviderStatus">Raw provider status such as AniList <c>RELEASING</c>.</param>
/// <param name="AverageScore">Provider average score on a 0-100 scale.</param>
/// <param name="AudioLanguages">Language tags, most common first.</param>
/// <param name="SubtitleLanguages">Language tags, most common first.</param>
/// <param name="GroupCount">Seasons for anime, volumes for reading media.</param>
/// <param name="Availability">
/// What the caller knows about the title's presence and open request; without it the card says nothing
/// about availability rather than guessing.
/// </param>
public sealed record MediaBannerCardData(
    MediaBannerKind Kind,
    string Title,
    string Href,
    string? BackdropUrl = null,
    string? ProviderStatus = null,
    int? Year = null,
    int? AverageScore = null,
    IReadOnlyList<string>? AudioLanguages = null,
    IReadOnlyList<string>? SubtitleLanguages = null,
    int? GroupCount = null,
    MediaBannerProgress? Progress = null,
    MediaAvailabilityFacts? Availability = null);

public sealed record MediaBannerStatusView(MediaReleaseStatus Status, string Label)
{
    public string CssModifier => Status.ToString().ToLowerInvariant();
}

/// <summary>The small availability badge: requested (with the stage of the request), in the library, or available.</summary>
public sealed record MediaBannerAvailabilityView(MediaAvailabilityState State, string Label)
{
    public string CssModifier => State.ToString().ToLowerInvariant();
}

public sealed record MediaBannerActionView(string Label, string UnitLabel, string Url);

public sealed record MediaBannerProgressView(int Percent, string Text, string AriaLabel);

public sealed record MediaBannerChipGroup(string Label, IReadOnlyList<string> Chips);

public sealed record MediaBannerFact(string Value, string Label);

/// <summary>Display model for <c>_MediaBannerCard</c>; build it with <see cref="Create"/>.</summary>
public sealed record MediaBannerCardModel(
    string Title,
    string Href,
    string? BackdropUrl,
    string Meta,
    MediaBannerStatusView? Status,
    MediaBannerAvailabilityView? Availability,
    MediaBannerActionView? Action,
    MediaBannerProgressView? Progress,
    MediaBannerChipGroup? Audio,
    MediaBannerChipGroup? Subtitles,
    MediaBannerFact? Groups,
    MediaBannerFact? Rating)
{
    public const int MaxLanguageChips = 4;

    public bool HasFacts => Audio is not null || Subtitles is not null || Groups is not null || Rating is not null;

    public static MediaBannerCardModel Create(MediaBannerCardData data, UiTextBundle ui)
    {
        var culture = ResolveCulture(ui.Locale);
        var kindLabel = ui[KindKey(data.Kind)];
        var meta = data.Year is int year and > 0
            ? $"{kindLabel} · {year.ToString(CultureInfo.InvariantCulture)}"
            : kindLabel;

        var status = MapStatus(data.ProviderStatus) is { } releaseStatus
            ? new MediaBannerStatusView(releaseStatus, ui[StatusKey(releaseStatus)])
            : null;

        var reading = data.Kind is MediaBannerKind.Manga or MediaBannerKind.LightNovel or MediaBannerKind.Book;
        MediaBannerActionView? action = null;
        MediaBannerProgressView? progress = null;
        if (data.Progress is { } unit)
        {
            action = new MediaBannerActionView(
                ui[ActionKey(unit.State, reading)],
                FormatUnit(unit, ui, culture),
                unit.NextUrl);

            if (unit.State != MediaBannerProgressState.NotStarted && unit.Percent is int percent)
            {
                var clamped = Math.Clamp(percent, 0, 100);
                progress = new MediaBannerProgressView(
                    clamped,
                    ui.Format("library.mediaCard.percent", ("percent", clamped)),
                    ui.Format(
                        reading ? "library.mediaCard.progressRead" : "library.mediaCard.progressWatched",
                        ("percent", clamped)));
            }
        }

        var groups = data.GroupCount is int count and > 0
            ? new MediaBannerFact(
                count.ToString(culture),
                ui[data.Kind is MediaBannerKind.Anime or MediaBannerKind.Series ? "library.mediaCard.seasons" : "library.mediaCard.volumes"])
            : null;

        var rating = FormatRating(data.AverageScore, culture) is { } score
            ? new MediaBannerFact(score, ui["library.mediaCard.rating"])
            : null;

        return new MediaBannerCardModel(
            data.Title,
            data.Href,
            string.IsNullOrWhiteSpace(data.BackdropUrl) ? null : data.BackdropUrl,
            meta,
            status,
            ResolveAvailability(data.Availability, hasPlayAction: action is not null, ui),
            action,
            progress,
            ChipGroup(ui["library.mediaCard.audioLanguages"], data.AudioLanguages),
            ChipGroup(ui["library.mediaCard.subtitleLanguages"], data.SubtitleLanguages),
            groups,
            rating);
    }

    /// <summary>
    /// The availability badge for a card. "Available" is not repeated when the card already offers to play or
    /// read the title (that button says it), so the badge only appears where it adds something. A requested
    /// title names the stage of its request with the same words the request lists use.
    /// </summary>
    public static MediaBannerAvailabilityView? ResolveAvailability(
        MediaAvailabilityFacts? facts,
        bool hasPlayAction,
        UiTextBundle ui)
    {
        if (facts is null || MediaAvailability.Resolve(facts) is not { } state)
        {
            return null;
        }

        return state switch
        {
            MediaAvailabilityState.Available when hasPlayAction => null,
            MediaAvailabilityState.Available => new(state, ui["library.mediaCard.availability.available"]),
            MediaAvailabilityState.Local => new(state, ui["library.mediaCard.availability.local"]),
            _ => new(state, ui[ConsumerAcquisitionLabels.StatusKey(facts.Request!.Value)])
        };
    }

    /// <summary>Maps AniList media status values; unknown or missing values yield no badge.</summary>
    public static MediaReleaseStatus? MapStatus(string? providerStatus) =>
        providerStatus?.Trim().ToUpperInvariant() switch
        {
            "RELEASING" => MediaReleaseStatus.Ongoing,
            "FINISHED" => MediaReleaseStatus.Finished,
            "NOT_YET_RELEASED" => MediaReleaseStatus.Upcoming,
            "HIATUS" => MediaReleaseStatus.Hiatus,
            "CANCELLED" => MediaReleaseStatus.Cancelled,
            _ => null
        };

    /// <summary>A 0-100 provider score as one decimal on a 10 scale (86 -> 8.6); null when unrated.</summary>
    public static string? FormatRating(int? averageScore, CultureInfo culture) =>
        averageScore is int score and > 0 and <= 100
            ? (score / 10m).ToString("0.0", culture)
            : null;

    /// <summary>Normalized, de-duplicated language codes in input order, capped with a "+N" overflow chip.</summary>
    public static IReadOnlyList<string> LanguageChips(IEnumerable<string>? languages)
    {
        var codes = (languages ?? [])
            .Select(PlaybackLanguages.Normalize)
            .Where(code => code is not null && code != PlaybackLanguages.SubtitlesOff)
            .Select(code => code!.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return codes.Length <= MaxLanguageChips
            ? codes
            : [.. codes.Take(MaxLanguageChips - 1), $"+{codes.Length - (MaxLanguageChips - 1)}"];
    }

    private static MediaBannerChipGroup? ChipGroup(string label, IEnumerable<string>? languages)
    {
        var chips = LanguageChips(languages);
        return chips.Count == 0 ? null : new MediaBannerChipGroup(label, chips);
    }

    private static string FormatUnit(MediaBannerProgress unit, UiTextBundle ui, CultureInfo culture)
    {
        var number = unit.NextNumber.ToString("0.##", culture);
        if (unit.Unit == MediaBannerUnit.Episode && unit.NextSeason is int season)
        {
            return ui.Format(
                "library.mediaCard.seasonEpisode",
                ("season", season),
                ("number", number));
        }

        var (ofKey, plainKey) = unit.Unit switch
        {
            MediaBannerUnit.Chapter => ("library.mediaCard.chapterOf", "library.mediaCard.chapter"),
            MediaBannerUnit.Volume => ("library.mediaCard.volumeOf", "library.mediaCard.volume"),
            _ => ("library.mediaCard.episodeOf", "library.mediaCard.episode")
        };

        return unit.TotalUnits is int total and > 0
            ? ui.Format(ofKey, ("number", number), ("total", total))
            : ui.Format(plainKey, ("number", number));
    }

    public static string KindKey(MediaBannerKind kind) => kind switch
    {
        MediaBannerKind.Series => "library.mediaCard.kind.series",
        MediaBannerKind.Movie => "library.mediaCard.kind.movie",
        MediaBannerKind.Manga => "library.mediaCard.kind.manga",
        MediaBannerKind.LightNovel => "library.mediaCard.kind.lightNovel",
        MediaBannerKind.Book => "library.mediaCard.kind.book",
        _ => "library.mediaCard.kind.anime"
    };

    public static string StatusKey(MediaReleaseStatus status) => status switch
    {
        MediaReleaseStatus.Ongoing => "library.mediaCard.status.ongoing",
        MediaReleaseStatus.Finished => "library.mediaCard.status.finished",
        MediaReleaseStatus.Upcoming => "library.mediaCard.status.upcoming",
        MediaReleaseStatus.Hiatus => "library.mediaCard.status.hiatus",
        _ => "library.mediaCard.status.cancelled"
    };

    private static string ActionKey(MediaBannerProgressState state, bool reading) => (state, reading) switch
    {
        (MediaBannerProgressState.NotStarted, false) => "library.mediaCard.startWatching",
        (MediaBannerProgressState.NotStarted, true) => "library.mediaCard.startReading",
        (MediaBannerProgressState.Completed, false) => "library.mediaCard.watchAgain",
        (MediaBannerProgressState.Completed, true) => "library.mediaCard.readAgain",
        (_, false) => "library.mediaCard.continueWatching",
        _ => "library.mediaCard.continueReading"
    };

    private static CultureInfo ResolveCulture(string locale)
    {
        try
        {
            return string.IsNullOrWhiteSpace(locale)
                ? CultureInfo.InvariantCulture
                : CultureInfo.GetCultureInfo(locale);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
