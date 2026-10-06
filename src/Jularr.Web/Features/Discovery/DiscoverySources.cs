using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Discovery;

/// <summary>
/// The provider pipelines behind Discover. Each is fetched, cached and failed on its own, so one slow or broken source
/// never holds back the others and never turns the whole page into an error.
/// </summary>
public enum DiscoverySource
{
    Anime,
    Movies,
    Series,

    /// <summary>AniList manga and light novels: one request answers both media types.</summary>
    Reading,
    Books
}

public enum DiscoverySourceState
{
    /// <summary>The source has not answered yet.</summary>
    Pending,

    Ready,

    /// <summary>The source failed or ran out of time.</summary>
    Unavailable,

    /// <summary>The source refused the call because it is rate limited or its circuit is open.</summary>
    Busy
}

public sealed record DiscoverySourceResult(DiscoverySource Source, DiscoverySourceState State, IReadOnlyList<DiscoveryItem> Items)
{
    public bool IsSettled => State != DiscoverySourceState.Pending;
}

/// <summary>
/// How long a load may wait for sources that have not answered. A first paint waits briefly and shows what is there; a follow-up
/// load that already holds <paramref name="SettledBefore"/> settled sources returns as soon as one more has settled.
/// </summary>
/// <param name="Budget">The longest the call waits for pending sources; zero returns the current state at once.</param>
/// <param name="SettledBefore">The number of settled sources the caller has already seen; null waits for every source.</param>
/// <param name="Refresh">Sources that are fetched again even when a fresh or failed answer is remembered: the explicit retry of a viewer.</param>
public sealed record DiscoveryWait(TimeSpan Budget, int? SettledBefore = null, IReadOnlySet<DiscoverySource>? Refresh = null)
{
    public static DiscoveryWait None { get; } = new(TimeSpan.Zero);
}

/// <summary>The viewer a discovery load is for: the profile (private lists), the owner flag and the media types the profile may browse at all.</summary>
public sealed record DiscoveryAudience(string ProfileId, bool IsOwner, IReadOnlySet<WorkMediaType> VisibleMediaTypes);

/// <summary>What one request of a load resolved to: its titles from the sources that answered and the state of every source behind it.</summary>
public sealed record DiscoveryBatch(DiscoveryRequest Request, bool AniListConnected, IReadOnlyList<DiscoverySourceResult> Sources)
{
    /// <summary>The titles of the ready sources in the fixed order of <see cref="DiscoverySource"/>, never the order in which the sources answered.</summary>
    public IReadOnlyList<DiscoveryItem> Items => [.. Sources.Where(source => source.State == DiscoverySourceState.Ready).OrderBy(source => source.Source).SelectMany(source => source.Items)];

    public bool HasPending => Sources.Any(source => source.State == DiscoverySourceState.Pending);

    /// <summary>The same batch with every title replaced, for overlaying library state.</summary>
    public DiscoveryBatch Select(Func<DiscoveryItem, DiscoveryItem> map) =>
        this with { Sources = [.. Sources.Select(source => source with { Items = [.. source.Items.Select(map)] })] };
}

/// <param name="Settled">The sources of this load that have answered or failed; the follow-up load passes it back as <see cref="DiscoveryWait.SettledBefore"/>.</param>
public sealed record DiscoveryLoad(IReadOnlyList<DiscoveryBatch> Batches, int Settled, int Pending);

/// <summary>Which sources answer which scope, and which result section a source feeds.</summary>
public static class DiscoverySources
{
    /// <summary>The sources of a scope in their fixed order, which is also the order of the result sections.</summary>
    public static IReadOnlyList<DiscoverySource> For(DiscoveryCategory category) => category switch
    {
        DiscoveryCategory.Anime => [DiscoverySource.Anime],
        DiscoveryCategory.Movie => [DiscoverySource.Movies],
        DiscoveryCategory.Series => [DiscoverySource.Series],
        DiscoveryCategory.Manga => [DiscoverySource.Reading],
        DiscoveryCategory.LightNovel => [DiscoverySource.Reading],
        DiscoveryCategory.Book => [DiscoverySource.Books],
        DiscoveryCategory.BooksAndLightNovels => [DiscoverySource.Reading, DiscoverySource.Books],
        _ => [DiscoverySource.Anime, DiscoverySource.Movies, DiscoverySource.Series, DiscoverySource.Reading, DiscoverySource.Books]
    };

    public static string Name(DiscoverySource source) => source switch
    {
        DiscoverySource.Anime => "anime",
        DiscoverySource.Movies => "movies",
        DiscoverySource.Series => "series",
        DiscoverySource.Reading => "reading",
        _ => "books"
    };

    public static bool TryParse(string? name, out DiscoverySource source)
    {
        foreach (var candidate in Enum.GetValues<DiscoverySource>())
        {
            if (string.Equals(Name(candidate), name?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                source = candidate;
                return true;
            }
        }

        source = default;
        return false;
    }

    /// <summary>The source that yields a media type the profile may see.</summary>
    public static bool IsVisible(DiscoverySource source, IReadOnlySet<WorkMediaType> visible) => source switch
    {
        DiscoverySource.Anime => visible.Contains(WorkMediaType.Anime),
        DiscoverySource.Movies => visible.Contains(WorkMediaType.Movie),
        DiscoverySource.Series => visible.Contains(WorkMediaType.Series),
        DiscoverySource.Reading => visible.Contains(WorkMediaType.Manga) || visible.Contains(WorkMediaType.LightNovel),
        _ => visible.Contains(WorkMediaType.Book)
    };
}

public enum DiscoverySectionState
{
    /// <summary>The section has titles, though a source behind it may still be pending or may have failed.</summary>
    Ready,

    /// <summary>No title yet and a source has not answered.</summary>
    Pending,

    /// <summary>Every source answered and none had a title.</summary>
    Empty,

    Unavailable,
    Busy
}

public static class DiscoverySections
{
    /// <summary>
    /// A section with titles is ready whatever else happens to its sources. Without titles it waits while any source is pending, and a
    /// failed source makes it unavailable rather than empty: nothing may claim "no results" when a source did not answer.
    /// </summary>
    public static DiscoverySectionState StateOf(int itemCount, IReadOnlyList<DiscoverySourceResult> sources)
    {
        if (itemCount > 0)
        {
            return DiscoverySectionState.Ready;
        }

        if (sources.Any(source => source.State == DiscoverySourceState.Pending))
        {
            return DiscoverySectionState.Pending;
        }

        if (sources.Any(source => source.State == DiscoverySourceState.Unavailable))
        {
            return DiscoverySectionState.Unavailable;
        }

        return sources.Any(source => source.State == DiscoverySourceState.Busy) ? DiscoverySectionState.Busy : DiscoverySectionState.Empty;
    }
}
