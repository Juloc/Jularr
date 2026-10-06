namespace Jularr.Web.Features.Discovery;

/// <summary>One row of the landing before it becomes a section: titles from the sources that answered and the state of every source behind it.</summary>
/// <param name="MediaLabel">The row's media type in the viewer's words, used when several failed rows are named in one sentence.</param>
public sealed record DiscoverLandingRow(
    string Id,
    string Heading,
    string? SeeAllUrl,
    IReadOnlyList<DiscoveryItem> Items,
    IReadOnlyList<DiscoverySourceResult> Sources,
    string? MediaLabel);

/// <summary>
/// Turns what the sources returned into the sections of the Discover body. The order of sections and of titles inside them depends only on the
/// data and the fixed source order, never on which source answered first, so two renderings of the same data are identical and a late source only
/// fills the section that was reserved for it.
/// </summary>
public static class DiscoverSectionComposer
{
    private const string TmdbSettingsPage = "/Admin/Providers";

    /// <summary>The media groups of an all-types search, in the order they are shown.</summary>
    private static readonly DiscoveryCategory[] Groups =
    [
        DiscoveryCategory.Anime,
        DiscoveryCategory.Movie,
        DiscoveryCategory.Series,
        DiscoveryCategory.BooksAndLightNovels,
        DiscoveryCategory.Manga
    ];

    /// <summary>
    /// The landing rows as sections. A row that waits keeps its place as a ghost row. Rows that failed are not repeated: all failed rows that
    /// share the same cause become one sentence at the place of the first of them, with one retry.
    /// </summary>
    public static IReadOnlyList<DiscoverSectionView> Landing(IReadOnlyList<DiscoverLandingRow> rows, DiscoverContext context)
    {
        var ui = context.Ui;
        var failedRows = rows
            .Where(row => FailureKey(row) is not null && row.Items.Count == 0)
            .GroupBy(row => FailureKey(row)!)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var announced = new HashSet<string>(StringComparer.Ordinal);
        var sections = new List<DiscoverSectionView>(rows.Count);

        foreach (var row in rows)
        {
            var cards = Cards(row.Items, context);
            var state = DiscoverySections.StateOf(cards.Count, row.Sources);
            var failure = FailureKey(row);
            switch (state)
            {
                case DiscoverySectionState.Ready:
                    sections.Add(Filled(row.Id, row.Heading, row.SeeAllUrl, DiscoverSectionLayout.Track, cards, null, row.Sources, ui));
                    break;
                case DiscoverySectionState.Pending:
                    sections.Add(Pending(row.Id, row.Heading, row.SeeAllUrl, DiscoverSectionLayout.Track));
                    break;
                case not (DiscoverySectionState.Ready or DiscoverySectionState.Pending or DiscoverySectionState.Empty) when failure is not null && announced.Add(failure):
                    var sharing = failedRows[failure];
                    var retry = FailedSources([.. sharing.SelectMany(shared => shared.Sources)]);
                    if (TmdbNotice(state, row.Sources, context) is { } notice)
                    {
                        sections.Add(new DiscoverSectionView($"notice-{failure}", null, null, DiscoverSectionLayout.Track, state, [], null, null, retry) { Notice = notice });
                        break;
                    }

                    var media = string.Join(", ", sharing.Select(shared => shared.MediaLabel).OfType<string>().Distinct(StringComparer.Ordinal));
                    var message = ui.Format(state == DiscoverySectionState.Busy ? "discover.section.busyFor" : "discover.section.unavailableFor", ("media", media));
                    sections.Add(new DiscoverSectionView($"notice-{failure}", null, null, DiscoverSectionLayout.Track, state, [], null, message, retry));
                    break;
            }
        }

        return sections;
    }

    /// <summary>
    /// The sections of a search or drill-down. One media type is one grid; every type together is one row per media group, each with a link
    /// to the grid of that group. Returns the number of titles found before the filters narrowed them.
    /// </summary>
    public static (IReadOnlyList<DiscoverSectionView> Sections, int Total) Results(DiscoveryBatch batch, DiscoverBrowseQuery query, DiscoverContext context)
    {
        var ui = context.Ui;
        if (query.Category != DiscoveryCategory.All)
        {
            var cards = Cards(batch.Items, context);
            var shown = DiscoverFilter.Apply(cards, query);
            var count = shown.Count == 1
                ? ui["discover.results.countOne"]
                : query.HasPostFilters
                    ? ui.Format("discover.results.countFiltered", ("count", shown.Count), ("total", cards.Count))
                    : ui.Format("discover.results.count", ("count", shown.Count));
            var state = DiscoverySections.StateOf(cards.Count, batch.Sources);
            var section = state switch
            {
                DiscoverySectionState.Ready when shown.Count > 0 => Filled("results", null, null, DiscoverSectionLayout.Grid, shown, count, batch.Sources, ui),
                DiscoverySectionState.Pending => Pending("results", null, null, DiscoverSectionLayout.Grid),
                _ when DiscoverySections.HasFailed(state) => Failed("results", null, DiscoverSectionLayout.Grid, state, batch.Sources, context),
                _ => null
            };

            return (section is null ? [] : [section], cards.Count);
        }

        var sections = new List<DiscoverSectionView>(Groups.Length);
        var total = 0;
        foreach (var group in Groups)
        {
            var sources = batch.Sources.Where(source => DiscoverySources.For(group).Contains(source.Source)).ToArray();
            if (sources.Length == 0)
            {
                continue;
            }

            var cards = Cards(batch.Items.Where(item => Belongs(group, item.Category)), context);
            var shown = DiscoverFilter.Apply(cards, query);
            total += cards.Count;
            var heading = ui[DiscoverScopes.Tabs.First(tab => tab.Category == group).LabelKey];
            var seeAll = (query with { Category = group }).Href;
            var state = DiscoverySections.StateOf(cards.Count, sources);
            var section = state switch
            {
                DiscoverySectionState.Ready when shown.Count > 0 => Filled(SectionId(group), heading, seeAll, DiscoverSectionLayout.Track, shown, null, sources, ui) with { Collapsible = true },
                DiscoverySectionState.Pending => Pending(SectionId(group), heading, seeAll, DiscoverSectionLayout.Track),
                _ when DiscoverySections.HasFailed(state) => Failed(SectionId(group), heading, DiscoverSectionLayout.Track, state, sources, context),
                _ => null
            };

            if (section is not null)
            {
                sections.Add(section);
            }
        }

        return (sections, total);
    }

    private static string SectionId(DiscoveryCategory group) => "group-" + DiscoverBrowseQuery.CategoryName(group);

    private static bool Belongs(DiscoveryCategory group, string itemCategory) => group switch
    {
        DiscoveryCategory.Anime => itemCategory == "anime",
        DiscoveryCategory.Movie => itemCategory == "movie",
        DiscoveryCategory.Series => itemCategory == "tv",
        DiscoveryCategory.Manga => itemCategory == "manga",
        _ => itemCategory is "book" or "light-novel"
    };

    private static IReadOnlyList<DiscoverCardView> Cards(IEnumerable<DiscoveryItem> items, DiscoverContext context) =>
        [.. DiscoverCanonical.Collapse(items).Select(item => DiscoverCardFactory.Create(item, context))];

    /// <summary>A section with titles. A source behind it that failed is named once and can be retried; one that is pending only marks the section as having more coming.</summary>
    private static DiscoverSectionView Filled(
        string id,
        string? heading,
        string? seeAllUrl,
        DiscoverSectionLayout layout,
        IReadOnlyList<DiscoverCardView> cards,
        string? count,
        IReadOnlyList<DiscoverySourceResult> sources,
        Localization.UiTextBundle ui)
    {
        var failed = FailedSources(sources);
        return new DiscoverSectionView(
            id,
            heading,
            seeAllUrl,
            layout,
            DiscoverySectionState.Ready,
            cards,
            count,
            failed.Count > 0 ? ui["discover.section.partial"] : null,
            failed);
    }

    /// <summary>The reserved place of a section that waits for its first titles; ghost cards the size of real ones keep the layout still when they arrive.</summary>
    private static DiscoverSectionView Pending(string id, string? heading, string? seeAllUrl, DiscoverSectionLayout layout) =>
        new(id, heading, seeAllUrl, layout, DiscoverySectionState.Pending, [], null, null, []);

    private static DiscoverSectionView Failed(
        string id,
        string? heading,
        DiscoverSectionLayout layout,
        DiscoverySectionState state,
        IReadOnlyList<DiscoverySourceResult> sources,
        DiscoverContext context)
    {
        var notice = TmdbNotice(state, sources, context);
        var message = notice is null ? context.Ui[state == DiscoverySectionState.Busy ? "discover.section.busy" : "discover.section.unavailable"] : null;
        return new DiscoverSectionView(id, heading, null, layout, state, [], null, message, FailedSources(sources)) { Notice = notice };
    }

    private static IReadOnlyList<DiscoverySource> FailedSources(IReadOnlyList<DiscoverySourceResult> sources) =>
        [.. sources.Where(source => source.State is DiscoverySourceState.Unavailable or DiscoverySourceState.Busy or DiscoverySourceState.AuthFailed).Select(source => source.Source).Distinct()];

    /// <summary>
    /// What a section says when TMDB, the provider behind Movies and Series, cannot answer: the owner sees the cause and the way to fix it, everybody
    /// else only that an administrator has to act (not configured, disabled, refused) or that it is temporary. Null for any other provider or cause,
    /// which keep their generic sentence.
    /// </summary>
    private static DiscoverProviderNotice? TmdbNotice(DiscoverySectionState state, IReadOnlyList<DiscoverySourceResult> sources, DiscoverContext context)
    {
        var failed = sources.Where(source => DiscoverySections.HasFailed(source.State)).ToArray();
        if (failed.Length == 0 || !failed.All(source => source.Source is DiscoverySource.Movies or DiscoverySource.Series))
        {
            return null;
        }

        var ui = context.Ui;
        var admin = context.CanConfigureProviders;
        var configure = admin ? ui["discover.tmdb.configure"] : null;
        var url = admin ? TmdbSettingsPage : null;
        return state switch
        {
            DiscoverySectionState.NotConfigured => admin
                ? new DiscoverProviderNotice(ui["discover.tmdb.notConfigured.title"], ui["discover.tmdb.notConfigured.body"], configure, url)
                : new DiscoverProviderNotice(ui["discover.tmdb.setupPending.title"], ui["discover.tmdb.setupPending.body"], null, null),
            DiscoverySectionState.Disabled => admin
                ? new DiscoverProviderNotice(ui["discover.tmdb.disabled.title"], ui["discover.tmdb.disabled.body"], configure, url)
                : new DiscoverProviderNotice(ui["discover.tmdb.setupPending.title"], ui["discover.tmdb.setupPending.body"], null, null),
            DiscoverySectionState.AuthFailed => admin
                ? new DiscoverProviderNotice(ui["discover.tmdb.authFailed.title"], ui["discover.tmdb.authFailed.body"], configure, url)
                : new DiscoverProviderNotice(ui["discover.tmdb.temporary.title"], ui["discover.tmdb.temporary.body"], null, null),
            DiscoverySectionState.Unavailable or DiscoverySectionState.Busy => admin
                ? new DiscoverProviderNotice(ui["discover.tmdb.unavailable.title"], ui["discover.tmdb.unavailable.body"], null, null)
                : new DiscoverProviderNotice(ui["discover.tmdb.temporary.title"], ui["discover.tmdb.temporary.body"], null, null),
            _ => null
        };
    }

    /// <summary>The cause shared by rows that failed together, or null for a row that did not fail. Rows of the TMDB-backed Movies and Series share one cause per kind.</summary>
    private static string? FailureKey(DiscoverLandingRow row)
    {
        var failed = row.Sources.Where(source => DiscoverySections.HasFailed(source.State)).ToArray();
        if (failed.Length == 0)
        {
            return null;
        }

        // A paused provider (circuit open, rate limited) is as temporary as a failing one: both are one cause for TMDB.
        var state = DiscoverySections.StateOf(0, failed);
        if (failed.All(source => source.Source is DiscoverySource.Movies or DiscoverySource.Series))
        {
            return "tmdb-" + (state == DiscoverySectionState.Busy ? DiscoverySectionState.Unavailable : state).ToString().ToLowerInvariant();
        }

        return (failed.Any(source => source.State == DiscoverySourceState.Unavailable) ? "unavailable-" : "busy-") + string.Join('-', failed.Select(source => DiscoverySources.Name(source.Source)));
    }
}
