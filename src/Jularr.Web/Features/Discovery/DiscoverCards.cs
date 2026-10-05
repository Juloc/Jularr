using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Franchises;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Watchlist;
using Jularr.Web.Ui;

namespace Jularr.Web.Features.Discovery;

/// <summary>What one title means for the viewer, in the words of the Discover card: which language is available or requested.</summary>
public enum DiscoverStateKind
{
    /// <summary>In the library with the preferred audio or subtitle language.</summary>
    PreferredAvailable,

    /// <summary>In the library, but only in other languages.</summary>
    OtherLanguages,

    /// <summary>Requested, in the preferred language or without a language choice.</summary>
    Requested,

    /// <summary>Requested in a language other than the preferred one.</summary>
    RequestedOtherLanguage,

    /// <summary>In the library; there is no language information (reading media, or nothing probed yet).</summary>
    InLibrary,

    /// <summary>Not in the library and not requested; the profile may request it.</summary>
    NotRequested,

    /// <summary>Not in the library and not requested; the profile may not request it.</summary>
    NotAvailable
}

/// <param name="Css">The modifier of the indicator (also picks its icon).</param>
/// <param name="Label">The short text of the indicator; the icon and the text carry the state, never the colour alone.</param>
public sealed record DiscoverStateView(DiscoverStateKind Kind, string Css, string Label, string? Hint);

public sealed record DiscoverFact(string Label, string Value);

/// <summary>The languages and the play link of a title that is already in the library.</summary>
public sealed record DiscoverLocalFacts(
    IReadOnlyList<string> Audio,
    IReadOnlyList<string> Subtitles,
    string? PlayUrl,
    string? PlayLabel);

/// <summary>One title of a Discover shelf or result grid: what the card shows and what its preview offers.</summary>
public sealed record DiscoverCardView(
    string Key,
    string Category,
    string Provider,
    string ExternalId,
    string Title,
    string? NativeTitle,
    string? Author,
    string Href,
    bool HrefIsExternal,
    string? PosterUrl,
    string Initial,
    string Meta,
    int? Year,
    MediaReleaseStatus? ReleaseStatus,
    bool IsLocal,
    string? RequestStatus,
    string? RequestStatusLabel,
    Guid? RequestId,
    DiscoverStateView State,
    string? Description,
    IReadOnlyList<string> Genres,
    IReadOnlyList<DiscoverFact> Facts,
    IReadOnlyList<string> Audio,
    IReadOnlyList<string> Subtitles,
    string? PlayUrl,
    string? PlayLabel,
    string? Format,
    string? RawStatus,
    bool CanRequest,
    string? ImportMangaUrl,
    bool CanFollow,
    bool IsFollowed,
    Guid? FollowedFranchiseId,
    bool CanFollowFranchise,
    bool CanImportSource);

public sealed record DiscoverShelfView(
    string Id,
    string Heading,
    string? SeeAllUrl,
    IReadOnlyList<DiscoverCardView> Cards);

/// <summary>Everything a card needs besides the title itself. All of it is read on the server; the browser never supplies library state.</summary>
public sealed record DiscoverContext(
    UiTextBundle Ui,
    LibraryLanguagePreference Preference,
    IReadOnlyDictionary<(MediaAcquisitionKind Kind, string ExternalId), AcquisitionRequest> OpenRequests,
    IReadOnlyDictionary<string, DiscoverLocalFacts> Local,
    IReadOnlyDictionary<string, Guid?> Followed,
    IReadOnlySet<string> RequestableCategories);

public static partial class DiscoverCardFactory
{
    private const int DescriptionLimit = 320;
    private const int MaxLanguageCodes = 3;

    public static DiscoverCardView Create(DiscoveryItem item, DiscoverContext context)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(context);
        var ui = context.Ui;

        var kind = KindOf(item.Category);
        var acquisition = AcquisitionKindOf(item.Category);
        var open = !item.IsLocal
            && acquisition is { } acquisitionKind
            && context.OpenRequests.TryGetValue((acquisitionKind, item.ExternalId), out var request)
                ? request
                : null;

        var local = item.IsLocal && item.LocalUrl is { Length: > 0 } localUrl
            && context.Local.TryGetValue(localUrl, out var facts)
                ? facts
                : null;

        var canRequest = !item.IsLocal && open is null && context.RequestableCategories.Contains(item.Category);
        var state = DiscoverStates.Resolve(item, open, local, context.Preference, canRequest, ui);

        var providerUrl = ProviderUrl(item);
        var importUrl = item.DetailsUrl.StartsWith("/Discover/MangaImport", StringComparison.Ordinal)
            ? item.DetailsUrl
            : null;
        var href = item.IsLocal && item.LocalUrl is { Length: > 0 } target
            ? target
            : importUrl is not null
                ? providerUrl ?? item.DetailsUrl
                : item.DetailsUrl;

        var canFollow = WatchlistDraftInput.TryIdentity(item.Category, item.Provider, item.ExternalId, out var identity);
        var followedFranchise = canFollow && context.Followed.TryGetValue(identity.Key, out var franchiseId)
            ? franchiseId
            : null;
        var isFollowed = item.IsFollowed || (canFollow && context.Followed.ContainsKey(identity.Key));

        var release = MediaBannerCardModel.MapStatus(item.Status);
        var requestLabel = open is null
            ? null
            : ui[ConsumerAcquisitionLabels.StatusKey(open.Status)];

        return new DiscoverCardView(
            item.Id,
            item.Category,
            item.Provider,
            item.ExternalId,
            item.Title,
            string.IsNullOrWhiteSpace(item.NativeTitle) || item.NativeTitle == item.Title ? null : item.NativeTitle,
            item.Author,
            href,
            IsExternal(href),
            string.IsNullOrWhiteSpace(item.CoverImageUrl) ? null : item.CoverImageUrl,
            WatchlistLabels.Initial(item.Title),
            Meta(item.Category, kind, item.Year, ui),
            item.Year,
            release,
            item.IsLocal,
            open is null ? null : AcquisitionAccessNames.Status(open.Status),
            requestLabel,
            open?.Id,
            state,
            DiscoverText.Plain(item.Description, DescriptionLimit),
            [.. item.Genres
                .Select(DiscoverGenres.Key)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Take(4)
                .Select(key => ui[key])],
            Facts(item, kind, release, requestLabel, ui),
            local?.Audio ?? [],
            local?.Subtitles ?? [],
            local?.PlayUrl,
            local?.PlayLabel,
            item.Format,
            item.Status,
            canRequest,
            importUrl,
            canFollow,
            isFollowed,
            followedFranchise ?? item.FollowedFranchiseId,
            canFollow && !item.IsLocal && FranchiseService.CanSeed(identity),
            item.CanImportSource && !item.IsLocal);
    }

    public static MediaBannerKind KindOf(string category) => category switch
    {
        "anime" or "movie" or "tv" => MediaBannerKind.Anime,
        "manga" => MediaBannerKind.Manga,
        "light-novel" => MediaBannerKind.LightNovel,
        _ => MediaBannerKind.Book
    };

    /// <summary>The acquisition kind of a Discover category.</summary>
    public static MediaAcquisitionKind? AcquisitionKindOf(string category) => category switch
    {
        "anime" => MediaAcquisitionKind.Anime,
        "manga" => MediaAcquisitionKind.Manga,
        "light-novel" => MediaAcquisitionKind.LightNovel,
        "book" => MediaAcquisitionKind.Book,
        "movie" => MediaAcquisitionKind.Movie,
        "tv" => MediaAcquisitionKind.Tv,
        _ => null
    };

    private static string Meta(string category, MediaBannerKind kind, int? year, UiTextBundle ui)
    {
        var label = category switch
        {
            "movie" => ui["search.type.movie"],
            "tv" => ui["search.type.series"],
            _ => ui[MediaBannerCardModel.KindKey(kind)]
        };
        return year is > 0
            ? $"{label} · {year.Value.ToString(CultureInfo.InvariantCulture)}"
            : label;
    }

    private static IReadOnlyList<DiscoverFact> Facts(
        DiscoveryItem item,
        MediaBannerKind kind,
        MediaReleaseStatus? release,
        string? requestLabel,
        UiTextBundle ui)
    {
        var facts = new List<DiscoverFact>();
        if (release is { } status)
        {
            facts.Add(new(ui["library.browse.filter.status"], ui[MediaBannerCardModel.StatusKey(status)]));
        }

        if (requestLabel is not null)
        {
            facts.Add(new(ui["discover.card.request"], requestLabel));
        }

        if (item.TotalProgress is > 0 and var total)
        {
            facts.Add(new(
                ui[kind == MediaBannerKind.Anime ? "discover.fact.episodes" : "discover.fact.chapters"],
                total.ToString(CultureInfo.InvariantCulture)));
        }

        if (kind is MediaBannerKind.Manga or MediaBannerKind.LightNovel && item.VolumeCount is > 0 and var volumes)
        {
            facts.Add(new(ui["library.mediaCard.volumes"], volumes.ToString(CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrWhiteSpace(item.Author))
        {
            facts.Add(new(ui["discover.fact.author"], item.Author));
        }

        if (item.Rating is { } rating and > 0)
        {
            facts.Add(new(ui["library.mediaCard.rating"], rating.ToString("0.0", CultureInfo.InvariantCulture)));
        }

        return facts;
    }

    private static string? ProviderUrl(DiscoveryItem item) =>
        item.Provider == "anilist" && item.ExternalId.All(char.IsAsciiDigit) && item.ExternalId.Length > 0
            ? $"https://anilist.co/{(item.Category == "anime" ? "anime" : "manga")}/{item.ExternalId}"
            : null;

    private static bool IsExternal(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    internal static string CodeList(IReadOnlyList<string> codes) =>
        string.Join('/', codes.Take(MaxLanguageCodes).Select(code => code.ToUpperInvariant()));
}

/// <summary>Resolves the one language and request indicator of a card (docs/mockups/discover).</summary>
public static class DiscoverStates
{
    public static DiscoverStateView Resolve(
        DiscoveryItem item,
        AcquisitionRequest? open,
        DiscoverLocalFacts? local,
        LibraryLanguagePreference preference,
        bool canRequest,
        UiTextBundle ui)
    {
        if (item.IsLocal)
        {
            if (local is not null && preference.IsSet && (local.Audio.Count > 0 || local.Subtitles.Count > 0))
            {
                if (preference.Audio is { } audio && LibraryBrowse.Has(local.Audio, audio))
                {
                    return Preferred(audio, ui);
                }

                if (preference.Subtitle is { } subtitle && LibraryBrowse.Has(local.Subtitles, subtitle))
                {
                    return Preferred(subtitle, ui);
                }

                var others = local.Audio.Count > 0 ? local.Audio : local.Subtitles;
                return new DiscoverStateView(
                    DiscoverStateKind.OtherLanguages,
                    "other",
                    ui.Format("discover.state.onlyOther", ("codes", DiscoverCardFactory.CodeList(others))),
                    ui["library.browse.card.preferredMissing"]);
            }

            return new DiscoverStateView(DiscoverStateKind.InLibrary, "library", ui["discover.card.inLibrary"], null);
        }

        if (open is not null)
        {
            var requested = new[] { open.Options.AudioLanguage, open.Options.SubtitleLanguage }
                .Where(code => code is not null && code != PlaybackLanguages.SubtitlesOff)
                .Select(code => code!)
                .ToArray();
            var stage = ui[ConsumerAcquisitionLabels.StatusKey(open.Status)];

            if (requested.Length > 0 && preference.IsSet)
            {
                var match = requested.FirstOrDefault(code =>
                    string.Equals(code, preference.Audio, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(code, preference.Subtitle, StringComparison.OrdinalIgnoreCase));
                return match is not null
                    ? new DiscoverStateView(
                        DiscoverStateKind.Requested,
                        "requested",
                        ui.Format("discover.state.requestedIn", ("code", match.ToUpperInvariant())),
                        stage)
                    : new DiscoverStateView(
                        DiscoverStateKind.RequestedOtherLanguage,
                        "requested-other",
                        ui.Format("discover.state.requestedIn", ("code", requested[0].ToUpperInvariant())),
                        stage);
            }

            return new DiscoverStateView(DiscoverStateKind.Requested, "requested", stage, null);
        }

        return canRequest
            ? new DiscoverStateView(DiscoverStateKind.NotRequested, "none", ui["discover.state.notRequested"], null)
            : new DiscoverStateView(DiscoverStateKind.NotAvailable, "unavailable", ui["library.browse.availability.missing"], null);
    }

    private static DiscoverStateView Preferred(string code, UiTextBundle ui) =>
        new(
            DiscoverStateKind.PreferredAvailable,
            "preferred",
            ui.Format("discover.state.availableIn", ("code", code.ToUpperInvariant())),
            null);
}

public static partial class DiscoverText
{
    /// <summary>A provider description as plain text: markup removed, whitespace collapsed, cut at a word.</summary>
    public static string? Plain(string? html, int limit)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = Markup().Replace(html, " ");
        text = Whitespace().Replace(WebUtility.HtmlDecode(text), " ").Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length <= limit)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', limit);
        return text[..(cut > limit / 2 ? cut : limit)].TrimEnd(' ', ',', ';', ':', '.', '-') + "…";
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Markup();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

public static class DiscoverCanonical
{
    /// <summary>
    /// One canonical work shown once: the same media identity (provider, id) collapses, and so do several
    /// provider entries that resolve to the same library work (for example the seasons AniList lists
    /// separately for one series). The first occurrence wins. Provider entries the library cannot tie to a
    /// work stay separate, because nothing proves they are the same.
    /// </summary>
    public static IReadOnlyList<DiscoveryItem> Collapse(IEnumerable<DiscoveryItem> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DiscoveryItem>();
        foreach (var item in items)
        {
            var identity = DiscoveryShelfComposer.IdentityKey(item);
            var work = item.LocalMediaId is { } id ? $"work:{item.Category}:{id:N}" : null;
            if (seen.Contains(identity) || (work is not null && seen.Contains(work)))
            {
                continue;
            }

            seen.Add(identity);
            if (work is not null)
            {
                seen.Add(work);
            }

            result.Add(item);
        }

        return result;
    }
}

public static class DiscoverFilter
{
    /// <summary>Narrows the loaded titles by the year, release status, availability and language filters.</summary>
    public static IReadOnlyList<DiscoverCardView> Apply(IEnumerable<DiscoverCardView> cards, DiscoverBrowseQuery query) =>
    [
        .. cards.Where(card =>
            (query.Year is null || card.Year == query.Year)
            && (query.Status is null || card.ReleaseStatus == query.Status)
            && (!query.PreferredLanguage || card.State.Kind == DiscoverStateKind.PreferredAvailable)
            && query.Availability switch
            {
                DiscoverAvailabilityFilter.InLibrary => card.IsLocal,
                DiscoverAvailabilityFilter.Requested => card.RequestStatus is not null,
                DiscoverAvailabilityFilter.NotInLibrary => !card.IsLocal && card.RequestStatus is null,
                _ => true
            })
    ];
}
