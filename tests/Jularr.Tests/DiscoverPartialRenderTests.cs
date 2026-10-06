using System.Net;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Localization;

namespace Jularr.Tests;

/// <summary>The Discover partials rendered as a browser receives them: card, rows, results and every body state.</summary>
[TestClass]
public sealed class DiscoverPartialRenderTests
{
    private const string CardView = "/Pages/Discover/_DiscoverCard.cshtml";
    private const string BodyView = "/Pages/Discover/_DiscoverBody.cshtml";

    private static readonly UiTextBundle Ui = UiTextBundle.English;

    private static DiscoverCardView Card(
        string title = "Frieren",
        string state = "preferred",
        DiscoverStateKind kind = DiscoverStateKind.PreferredAvailable,
        string label = "DE available",
        string href = "https://anilist.co/anime/1",
        bool isLocal = false,
        bool canRequest = true,
        string? poster = "https://img.example/cover.jpg",
        string? playUrl = null) =>
        new(
            "anilist:anime:1",
            "anime",
            "anilist",
            "1",
            title,
            null,
            null,
            href,
            href.StartsWith("http", StringComparison.Ordinal),
            poster,
            "F",
            "Anime · 2023",
            2023,
            null,
            isLocal,
            null,
            null,
            null,
            new DiscoverStateView(kind, state, label, null),
            "A mage looks back on her journey.",
            ["Fantasy"],
            [new DiscoverFact("Status", "Finished")],
            [],
            [],
            playUrl,
            playUrl is null ? null : "Continue watching",
            "TV",
            "FINISHED",
            canRequest,
            null,
            true,
            false,
            null,
            true,
            false);

    private static DiscoverBodyView Body(
        DiscoverBodyState state,
        DiscoverBrowseQuery? query = null,
        IReadOnlyList<DiscoverSectionView>? sections = null,
        int total = 0,
        int settled = 0,
        int pending = 0) =>
        new(Ui, query ?? new DiscoverBrowseQuery(), state, sections ?? [], total, settled, pending);

    private static DiscoverSectionView Section(
        string id,
        string? heading,
        IReadOnlyList<DiscoverCardView> cards,
        DiscoverSectionLayout layout = DiscoverSectionLayout.Track,
        DiscoverySectionState state = DiscoverySectionState.Ready,
        string? seeAll = null,
        string? count = null,
        string? message = null,
        IReadOnlyList<DiscoverySource>? retry = null) =>
        new(id, heading, seeAll, layout, state, cards, count, message, retry ?? []);

    private static async Task<string> RenderAsync(string view, object model, bool decode = true)
    {
        await using var renderer = await DiscoverPartialRenderer.CreateAsync();
        var html = await renderer.RenderAsync(view, model);
        return decode ? WebUtility.HtmlDecode(html) : html;
    }

    [TestMethod]
    public async Task ACardShowsArtworkTitleTypeYearAndOneStateIndicatorAndNothingLibraryLike()
    {
        var html = await RenderAsync(CardView, (Card(), Ui));

        var visible = html[..html.IndexOf("<template", StringComparison.Ordinal)];
        StringAssert.Contains(visible, "class=\"dc-art\"");
        StringAssert.Contains(visible, "src=\"https://img.example/cover.jpg\"");
        StringAssert.Contains(visible, ">Frieren</a></h3>");
        StringAssert.Contains(visible, "Anime · 2023");
        Assert.AreEqual(1, Regex.Matches(visible, "class=\"dc-state ").Count, "One language/request indicator.");
        StringAssert.Contains(visible, "dc-state-preferred");
        StringAssert.Contains(visible, "<span>DE available</span>");
        Assert.IsFalse(visible.Contains("progress", StringComparison.OrdinalIgnoreCase), "No Library-style progress on a Discover card.");
        Assert.IsFalse(visible.Contains("lib-", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ATitleOutsideTheLibraryOpensTheProviderInANewTabAndAPosterlessOneShowsItsInitial()
    {
        var html = await RenderAsync(CardView, (Card(poster: null), Ui));

        StringAssert.Contains(html, "href=\"https://anilist.co/anime/1\" target=\"_blank\" rel=\"noopener noreferrer\"");
        StringAssert.Contains(html, "<span class=\"dc-initial\">F</span>");
    }

    [TestMethod]
    public async Task ARequestableTitleOffersTheActionDirectlyOnTheStableCard()
    {
        var html = await RenderAsync(CardView, (Card(), Ui));
        var visible = html[..html.IndexOf("<template", StringComparison.Ordinal)];

        StringAssert.Contains(visible, "data-dc-card-request>");
        Assert.IsTrue(Regex.IsMatch(visible, @">\s*Request\s*</button>"));
        StringAssert.Contains(visible, "data-dc-provider=\"anilist\"");
    }

    [TestMethod]
    public async Task ARequestedTitleHasNoRequestButtonAndIsWatchedSoItsIndicatorShowsTheProgress()
    {
        var requested = Card(canRequest: false) with { RequestId = Guid.Parse("11111111-1111-1111-1111-111111111111"), RequestStatus = "downloading" };

        var html = await RenderAsync(CardView, (requested, Ui));
        var visible = html[..html.IndexOf("<template", StringComparison.Ordinal)];

        Assert.IsFalse(visible.Contains("data-dc-card-request", StringComparison.Ordinal));
        StringAssert.Contains(visible, "data-dc-watch-request=\"11111111-1111-1111-1111-111111111111\"");
    }

    [TestMethod]
    public async Task APreviewOffersRequestFollowAndDetailsWithoutAnyLibraryStateSuppliedByTheBrowser()
    {
        var html = await RenderAsync(CardView, (Card(), Ui));

        var preview = html[html.IndexOf("<template", StringComparison.Ordinal)..];
        StringAssert.Contains(preview, "data-external-id=\"1\"");
        StringAssert.Contains(preview, "A mage looks back on her journey.");
        StringAssert.Contains(preview, "<dt>Status</dt><dd>Finished</dd>");
        StringAssert.Contains(preview, "data-dc-request>");
        Assert.IsTrue(Regex.IsMatch(preview, @">\s*Request\s*</button>"));
        StringAssert.Contains(preview, "data-dc-follow ");
        StringAssert.Contains(preview, "Follow franchise");
        StringAssert.Contains(preview, "<span>Details</span>");
        Assert.IsFalse(preview.Contains("data-local", StringComparison.Ordinal));
        Assert.IsFalse(preview.Contains("Continue watching", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ALibraryTitleOffersPlayInsteadOfRequestAndKeepsItsOwnPage()
    {
        var card = Card(
            kind: DiscoverStateKind.InLibrary,
            state: "library",
            label: "In library",
            href: "/Library/Anime/a",
            isLocal: true,
            canRequest: false,
            playUrl: "/Library/Episode/e");

        var html = await RenderAsync(CardView, (card, Ui));

        StringAssert.Contains(html, "class=\"dc-card is-local\"");
        StringAssert.Contains(html, "href=\"/Library/Episode/e\"");
        StringAssert.Contains(html, "Continue watching");
        Assert.IsFalse(html.Contains("data-dc-request>", StringComparison.Ordinal) || html.Contains("data-dc-card-request>", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("target=\"_blank\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ATitleIsEncodedNotInterpretedAsMarkup()
    {
        var html = await RenderAsync(CardView, (Card(title: "<img src=x onerror=alert(1)>"), Ui), decode: false);

        Assert.IsFalse(html.Contains("<img src=x", StringComparison.Ordinal));
        StringAssert.Contains(html, "&lt;img src=x onerror=alert(1)&gt;");
    }

    [TestMethod]
    public async Task TheLandingShowsRowsWithASeeAllLinkAndTheirCards()
    {
        var sections = new[]
        {
            Section("trending-anime", "Trending", [Card(), Card("Dune")], seeAll: "/Discover?category=anime"),
            Section("because", "Because you watched Solo", [Card("Mushoku")])
        };

        var html = await RenderAsync(BodyView, Body(DiscoverBodyState.Sections, sections: sections, settled: 2));

        StringAssert.Contains(html, "<h2 id=\"dc-section-trending-anime\">Trending</h2>");
        StringAssert.Contains(html, "<a class=\"dc-shelf-more\" href=\"/Discover?category=anime\">See all</a>");
        Assert.AreEqual(1, Regex.Matches(html, "dc-shelf-more").Count, "A personalized row has no see-all link.");
        Assert.AreEqual(3, Regex.Matches(html, "<article class=\"dc-card").Count);
        StringAssert.Contains(html, "data-dc-settled=\"2\"");
    }

    [TestMethod]
    public async Task AGridShowsItsCountAndTheFilteredCountWhenFiltersNarrowThem()
    {
        var plain = await RenderAsync(BodyView, Body(DiscoverBodyState.Sections, sections: [Section("results", null, [Card(), Card("B")], DiscoverSectionLayout.Grid, count: "2 results")]));
        var narrowed = await RenderAsync(BodyView, Body(DiscoverBodyState.Sections, sections: [Section("results", null, [Card()], DiscoverSectionLayout.Grid, count: "1 of 5 results")]));

        StringAssert.Contains(plain, "2 results");
        StringAssert.Contains(plain, "<ul class=\"dc-grid\">");
        StringAssert.Contains(narrowed, "1 of 5 results");
    }

    [TestMethod]
    public async Task ASectionThatWaitsKeepsItsPlaceWithGhostCardsThatAreNotAnnouncedAndHaveNoActions()
    {
        var waiting = Section("trending-movie", "Trending · Movie", [], state: DiscoverySectionState.Pending, seeAll: "/Discover?category=movie");
        var grid = Section("results", null, [], DiscoverSectionLayout.Grid, DiscoverySectionState.Pending);

        var row = await RenderAsync(BodyView, Body(DiscoverBodyState.Sections, sections: [waiting], pending: 1));
        var wholeGrid = await RenderAsync(BodyView, Body(DiscoverBodyState.Sections, sections: [grid], pending: 1));

        StringAssert.Contains(row, "data-dc-section-state=\"pending\"");
        StringAssert.Contains(row, "data-dc-layout=\"track\"");
        StringAssert.Contains(wholeGrid, "data-dc-layout=\"grid\"");
        StringAssert.Contains(row, "aria-busy=\"true\"");
        StringAssert.Contains(row, "data-dc-pending=\"1\"");
        Assert.AreEqual(DiscoverSectionView.TrackGhosts, Regex.Matches(row, "class=\"dc-ghost\"").Count);
        Assert.AreEqual(DiscoverSectionView.GridGhosts, Regex.Matches(wholeGrid, "class=\"dc-ghost\"").Count);
        StringAssert.Contains(row, "aria-hidden=\"true\"");
        Assert.IsFalse(row.Contains("<article", StringComparison.Ordinal) || row.Contains("<button", StringComparison.Ordinal) || row.Contains("<a class=\"dc-shelf-more\"", StringComparison.Ordinal),
            "A ghost is not a card: nothing focusable or clickable.");
        StringAssert.Contains(row, "Loading…");
    }

    [TestMethod]
    public async Task ASectionWithTitlesAndAFailedSourceNamesItInOneSentenceWithItsOwnRetry()
    {
        var partial = Section("group-books-light-novels", "Books & Light Novels", [Card()], message: "Some titles are missing.", retry: [DiscoverySource.Books, DiscoverySource.Reading]);
        var failed = Section("results", null, [], DiscoverSectionLayout.Grid, DiscoverySectionState.Unavailable, message: "Couldn't load this section right now.", retry: [DiscoverySource.Anime]);

        var withTitles = await RenderAsync(BodyView, Body(DiscoverBodyState.Sections, sections: [partial]));
        var withoutTitles = await RenderAsync(BodyView, Body(DiscoverBodyState.Sections, sections: [failed]));

        StringAssert.Contains(withTitles, "Some titles are missing.");
        StringAssert.Contains(withTitles, "data-dc-note=\"true\"");
        StringAssert.Contains(withTitles, "data-dc-retry-sources=\"books,reading\"");
        StringAssert.Contains(withTitles, "<article class=\"dc-card");
        StringAssert.Contains(withoutTitles, "Couldn't load this section right now.");
        StringAssert.Contains(withoutTitles, "data-dc-retry-sources=\"anime\"");
        Assert.IsFalse(withoutTitles.Contains("anilist", StringComparison.OrdinalIgnoreCase), "The viewer is never shown a provider's name or error.");
    }

    [TestMethod]
    public async Task AMediaGroupOfASearchCanBeCollapsedAndEveryChangeOfItsTitlesChangesItsSignature()
    {
        var group = Section("group-anime", "Anime", [Card()]) with { Collapsible = true };
        var more = Section("group-anime", "Anime", [Card(), Card("Dune")]) with { Collapsible = true };

        var html = await RenderAsync(BodyView, Body(DiscoverBodyState.Sections, sections: [group]));

        StringAssert.Contains(html, "<details class=\"dc-collapse\" open>");
        StringAssert.Contains(html, "<summary class=\"dc-shelf-head\">");
        Assert.AreNotEqual(group.Signature, more.Signature);
        Assert.AreEqual(group.Signature, (group with { SeeAllUrl = "/elsewhere" }).Signature, "Only what the viewer would notice changing is part of the signature.");
        StringAssert.Contains(html, $"data-dc-sig=\"{group.Signature}\"");
    }

    [TestMethod]
    public async Task EveryEmptyOrFailedStateExplainsItselfWithOneAction()
    {
        var filtered = new DiscoverBrowseQuery { Text = "x", Genre = "Horror", Year = 2001 };

        var noResults = await RenderAsync(BodyView, Body(DiscoverBodyState.NoResults, filtered, total: 4));
        StringAssert.Contains(noResults, "No titles match these filters");
        StringAssert.Contains(noResults, "href=\"/Discover?q=x\"");

        var empty = await RenderAsync(BodyView, Body(DiscoverBodyState.Empty));
        StringAssert.Contains(empty, "No results");
        StringAssert.Contains(empty, "Try another title or category.");

        var unavailable = await RenderAsync(BodyView, Body(DiscoverBodyState.Unavailable));
        StringAssert.Contains(unavailable, "role=\"alert\"");
        StringAssert.Contains(unavailable, "Discover is unavailable");
        StringAssert.Contains(unavailable, "data-dc-retry");

        var notConnected = await RenderAsync(BodyView, Body(DiscoverBodyState.NotConnected));
        StringAssert.Contains(notConnected, "AniList is not connected.");
        StringAssert.Contains(notConnected, "href=\"/Settings/AniList\"");

        var books = await RenderAsync(BodyView, Body(DiscoverBodyState.BooksNotInList, new DiscoverBrowseQuery { Mode = DiscoveryMode.MyList }));
        StringAssert.Contains(books, "AniList lists have no books.");
        StringAssert.Contains(books, "href=\"/Discover\"");
    }

    [TestMethod]
    public void ThePageShellHasTheSearchFilterAndMediaTypeSwitchAndNoGenreChipWall()
    {
        var page = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Jularr.Web", "Pages", "Discover", "Index.cshtml"));

        StringAssert.Contains(page, "role=\"search\"");
        StringAssert.Contains(page, "name=\"q\"");
        StringAssert.Contains(page, "Model.VisibleTabs");
        StringAssert.Contains(page, "<details class=\"dc-pop\" data-dc-pop>");
        StringAssert.Contains(page, "library.browse.filtersActiveAria");
        StringAssert.Contains(page, "data-dc-offline");
        StringAssert.Contains(page, "data-dc-error");
        StringAssert.Contains(page, "data-dc-sync", "A failed follow-up is reported without touching the titles that are shown.");
        StringAssert.Contains(page, "<noscript>", "Without scripts the placeholders never fill, so a message stands instead.");
        StringAssert.Contains(page, "<dialog class=\"dc-sheet\" data-dc-sheet>");
        Assert.IsFalse(page.Contains("discover-genre-row", StringComparison.Ordinal), "Genres live in the Filters panel, not in a second chip row.");
        Assert.IsFalse(page.Contains("discover-collections-link", StringComparison.Ordinal));
        Assert.AreEqual(1, Regex.Matches(page, "<h1>").Count);
    }

    [TestMethod]
    public void TheStylesheetIsPageScopedAndHasNoLeftAccentBar()
    {
        var css = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "css", "discover-browse.css"));

        Assert.IsFalse(css.Contains("border-left", StringComparison.Ordinal));
        Assert.IsFalse(css.Contains("border-inline-start", StringComparison.Ordinal));
        Assert.IsFalse(css.Contains(".dc-card:hover .dc-art img", StringComparison.Ordinal), "Hover must not grow Discover artwork.");
        StringAssert.Contains(css, "@media (max-width: 760px)");
        StringAssert.Contains(css, "(hover: none), (pointer: coarse)");

        var js = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "discover.js"));
        Assert.IsFalse(js.Contains("pointerover", StringComparison.Ordinal), "Discover preview is explicit, never an automatic hover popover.");
        StringAssert.Contains(js, "data-dc-live-request");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }
}
