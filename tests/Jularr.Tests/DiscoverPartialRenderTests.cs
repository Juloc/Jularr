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
        IReadOnlyList<DiscoverShelfView>? shelves = null,
        IReadOnlyList<DiscoverCardView>? cards = null,
        int total = 0,
        bool degraded = false) =>
        new(Ui, query ?? new DiscoverBrowseQuery(), state, shelves ?? [], cards ?? [], total, degraded);

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
        var shelves = new[]
        {
            new DiscoverShelfView("trending-anime", "Trending", "/Discover?category=anime", [Card(), Card("Dune")]),
            new DiscoverShelfView("because", "Because you watched Solo", null, [Card("Mushoku")])
        };

        var html = await RenderAsync(BodyView, Body(DiscoverBodyState.Shelves, shelves: shelves));

        StringAssert.Contains(html, "<h2 id=\"dc-shelf-trending-anime\">Trending</h2>");
        StringAssert.Contains(html, "<a class=\"dc-shelf-more\" href=\"/Discover?category=anime\">See all</a>");
        Assert.AreEqual(1, Regex.Matches(html, "dc-shelf-more").Count, "A personalized row has no see-all link.");
        Assert.AreEqual(3, Regex.Matches(html, "<article class=\"dc-card").Count);
        Assert.IsFalse(html.Contains("dc-notice", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ResultsShowTheCountAndAGridAndTheFilteredCountWhenFiltersNarrowThem()
    {
        var query = new DiscoverBrowseQuery { Text = "frieren" };
        var plain = await RenderAsync(BodyView, Body(DiscoverBodyState.Results, query, cards: [Card(), Card("B")], total: 2));
        var narrowed = await RenderAsync(
            BodyView,
            Body(DiscoverBodyState.Results, query with { Year = 2023 }, cards: [Card()], total: 5));

        StringAssert.Contains(plain, "2 results");
        StringAssert.Contains(plain, "<ul class=\"dc-grid\">");
        StringAssert.Contains(narrowed, "1 of 5 results");
    }

    [TestMethod]
    public async Task APartialProviderFailureKeepsTheTitlesAndOffersARetry()
    {
        var html = await RenderAsync(
            BodyView,
            Body(DiscoverBodyState.Results, cards: [Card()], total: 1, degraded: true));

        StringAssert.Contains(html, "Some sources did not respond. Results may be incomplete.");
        StringAssert.Contains(html, "data-dc-retry");
        StringAssert.Contains(html, "<article class=\"dc-card");
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
        StringAssert.Contains(page, "DiscoverScopes.Tabs");
        StringAssert.Contains(page, "<details class=\"dc-pop\" data-dc-pop>");
        StringAssert.Contains(page, "library.browse.filtersActiveAria");
        StringAssert.Contains(page, "data-dc-offline");
        StringAssert.Contains(page, "data-dc-error");
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
