using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;

namespace Jularr.Tests;

/// <summary>
/// The sections of the Discover body (docs/mockups/discover/SPEC.md): reserved places for sources that wait, one sentence for rows that failed
/// together, media groups for an all-types search with a deterministic order, one canonical title once, and a signature that only changes when the
/// viewer would notice a change.
/// </summary>
[TestClass]
public sealed class DiscoverSectionComposerTests
{
    private static readonly UiTextBundle Ui = UiTextBundle.English;

    private static DiscoveryItem Item(string category, string provider, string id, string title, int? year = 2024, string? cover = "https://img.example/c.jpg") =>
        new($"{provider}:{category}:{id}", category, provider, id, title, null, null, cover, null, null, year, null, null, null, null, [], false, null, "https://example.test/d", false);

    private static DiscoverContext Context(params string[] requestable) =>
        new(
            Ui,
            LibraryLanguagePreference.None,
            new Dictionary<(Jularr.Web.Features.Acquisition.Access.MediaAcquisitionKind Kind, string ExternalId), Jularr.Web.Features.Acquisition.Access.AcquisitionRequest>(),
            new Dictionary<string, DiscoverLocalFacts>(),
            new Dictionary<string, Guid?>(),
            new HashSet<string>(requestable.Length == 0 ? ["anime", "movie", "tv", "book", "manga", "light-novel"] : requestable, StringComparer.Ordinal));

    private static DiscoverySourceResult Source(DiscoverySource source, DiscoverySourceState state, params DiscoveryItem[] items) => new(source, state, items);

    private static DiscoverLandingRow Row(string id, string label, params DiscoverySourceResult[] sources) =>
        new(id, label + " row", "/?category=" + id, [.. sources.SelectMany(source => source.Items)], sources, label);

    [TestMethod]
    public void ARowThatWaitsKeepsItsPlaceAsGhostCardsAndNoRowIsInventedForAnEmptyOne()
    {
        var sections = DiscoverSectionComposer.Landing(
            [
                Row("trending-anime", "Anime", Source(DiscoverySource.Anime, DiscoverySourceState.Ready, Item("anime", "anilist", "1", "Frieren"))),
                Row("trending-movie", "Movie", Source(DiscoverySource.Movies, DiscoverySourceState.Pending)),
                Row("trending-series", "Series", Source(DiscoverySource.Series, DiscoverySourceState.Ready))
            ],
            Context());

        CollectionAssert.AreEqual(new[] { "trending-anime", "trending-movie" }, sections.Select(section => section.Id).ToArray());
        Assert.AreEqual(DiscoverySectionState.Ready, sections[0].State);
        Assert.AreEqual(DiscoverySectionState.Pending, sections[1].State);
        Assert.AreEqual(0, sections[1].Cards.Count);
        Assert.AreEqual(DiscoverSectionView.TrackGhosts, sections[1].Ghosts);
        Assert.AreEqual("Movie row", sections[1].Heading, "A waiting row keeps its heading, so the layout and the labels do not change when it fills.");
    }

    [TestMethod]
    public void RowsThatFailedTogetherBecomeOneSentenceAtTheFirstOfThemWithOneRetry()
    {
        var failed = Source(DiscoverySource.Movies, DiscoverySourceState.Unavailable);
        var sections = DiscoverSectionComposer.Landing(
            [
                Row("trending-anime", "Anime", Source(DiscoverySource.Anime, DiscoverySourceState.Ready, Item("anime", "anilist", "1", "Frieren"))),
                Row("trending-movie", "Movie", failed),
                Row("top-anime", "Anime", Source(DiscoverySource.Anime, DiscoverySourceState.Ready, Item("anime", "anilist", "2", "Dandadan"))),
                Row("top-movie", "Movie", failed),
                Row("trending-series", "Series", Source(DiscoverySource.Series, DiscoverySourceState.Busy))
            ],
            Context());

        CollectionAssert.AreEqual(new[] { "trending-anime", "notice-tmdb-unavailable", "top-anime" }, sections.Select(section => section.Id).ToArray());
        Assert.IsNull(sections[1].Message, "A failed TMDB row names its provider cause, not the generic sentence.");
        Assert.AreEqual("Movie & Series discovery is temporarily unavailable", sections[1].Notice!.Title);
        CollectionAssert.AreEqual(new[] { DiscoverySource.Movies, DiscoverySource.Series }, sections[1].Retry.ToArray());
        Assert.IsNull(sections[1].Heading);
    }

    [TestMethod]
    public void ARowWithTitlesAndAFailedSourceKeepsTheTitlesAndNamesTheGapOnce()
    {
        var sections = DiscoverSectionComposer.Landing(
            [
                Row(
                    "trending-books-light-novels",
                    "Books",
                    Source(DiscoverySource.Reading, DiscoverySourceState.Ready, Item("light-novel", "anilist", "7", "Overlord")),
                    Source(DiscoverySource.Books, DiscoverySourceState.Unavailable))
            ],
            Context());

        Assert.AreEqual(DiscoverySectionState.Ready, sections.Single().State);
        Assert.AreEqual(1, sections.Single().Cards.Count);
        Assert.AreEqual("Some titles are missing.", sections.Single().Message);
        CollectionAssert.AreEqual(new[] { DiscoverySource.Books }, sections.Single().Retry.ToArray());
    }

    [TestMethod]
    public void AnAllTypesSearchGroupsByMediaTypeInAFixedOrderWhateverTheSourcesReturnFirst()
    {
        var batch = new DiscoveryBatch(
            new DiscoveryRequest("solo", DiscoveryCategory.All, DiscoveryMode.Search),
            false,
            [
                Source(DiscoverySource.Books, DiscoverySourceState.Ready, Item("book", "openlibrary", "b1", "Solo Book")),
                Source(DiscoverySource.Series, DiscoverySourceState.Ready, Item("tv", "tmdb", "s1", "Solo Series")),
                Source(DiscoverySource.Anime, DiscoverySourceState.Ready, Item("anime", "anilist", "a1", "Solo Leveling")),
                Source(DiscoverySource.Reading, DiscoverySourceState.Ready, Item("manga", "anilist", "m1", "Solo Manga"), Item("light-novel", "anilist", "l1", "Solo Novel")),
                Source(DiscoverySource.Movies, DiscoverySourceState.Ready, Item("movie", "tmdb", "mo1", "Solo Movie"))
            ]);
        var reversed = batch with { Sources = [.. batch.Sources.Reverse()] };

        var (sections, total) = DiscoverSectionComposer.Results(batch, new DiscoverBrowseQuery { Text = "solo" }, Context());
        var (again, _) = DiscoverSectionComposer.Results(reversed, new DiscoverBrowseQuery { Text = "solo" }, Context());

        CollectionAssert.AreEqual(new[] { "group-anime", "group-movie", "group-tv", "group-light-novel", "group-book", "group-manga" }, sections.Select(section => section.Id).ToArray());
        Assert.AreEqual(5 + 1, total);
        Assert.IsTrue(sections.All(section => section.Collapsible && section.SeeAllUrl is not null));
        Assert.AreEqual("/?q=solo&category=anime", sections[0].SeeAllUrl, "See all keeps the search text.");
        CollectionAssert.AreEqual(new[] { "Solo Novel" }, sections[3].Cards.Select(card => card.Title).ToArray(), "Light novels are their own group.");
        CollectionAssert.AreEqual(new[] { "Solo Book" }, sections[4].Cards.Select(card => card.Title).ToArray(), "Books are their own group.");
        CollectionAssert.AreEqual(sections.Select(section => section.Signature).ToArray(), again.Select(section => section.Signature).ToArray(), "Identical data renders identically.");
    }

    [TestMethod]
    public void OneMediaTypeIsOneGridWithItsCountAndTheFilteredCountWhenFiltersNarrowIt()
    {
        var batch = new DiscoveryBatch(
            new DiscoveryRequest("", DiscoveryCategory.Anime, DiscoveryMode.Trending),
            false,
            [Source(DiscoverySource.Anime, DiscoverySourceState.Ready, Item("anime", "anilist", "1", "A", 2023), Item("anime", "anilist", "2", "B", 2024), Item("anime", "anilist", "3", "C", 2024))]);

        var (all, total) = DiscoverSectionComposer.Results(batch, new DiscoverBrowseQuery { Category = DiscoveryCategory.Anime }, Context());
        var (narrowed, _) = DiscoverSectionComposer.Results(batch, new DiscoverBrowseQuery { Category = DiscoveryCategory.Anime, YearFrom = 2024, YearTo = 2024 }, Context());
        var (none, noneTotal) = DiscoverSectionComposer.Results(batch, new DiscoverBrowseQuery { Category = DiscoveryCategory.Anime, YearFrom = 1999, YearTo = 1999 }, Context());

        Assert.AreEqual(DiscoverSectionLayout.Grid, all.Single().Layout);
        Assert.IsNull(all.Single().Count, "A paged view has no count: the titles loaded so far are not the results.");
        Assert.AreEqual(2, narrowed.Single().Cards.Count);
        Assert.AreEqual(3, total);
        Assert.AreEqual(0, none.Count, "Nothing left after the filters is the no-results state of the body, not an empty grid.");
        Assert.AreEqual(3, noneTotal);
    }

    [TestMethod]
    public void AnUnavailableSourceOfAGroupSaysSoUnderTheGroupHeadingAndAnEmptyAnswerIsDropped()
    {
        var batch = new DiscoveryBatch(
            new DiscoveryRequest("zzz", DiscoveryCategory.All, DiscoveryMode.Search),
            false,
            [
                Source(DiscoverySource.Anime, DiscoverySourceState.Unavailable),
                Source(DiscoverySource.Movies, DiscoverySourceState.Ready),
                Source(DiscoverySource.Series, DiscoverySourceState.Pending)
            ]);

        var (sections, total) = DiscoverSectionComposer.Results(batch, new DiscoverBrowseQuery { Text = "zzz" }, Context());

        CollectionAssert.AreEqual(new[] { "group-anime", "group-tv" }, sections.Select(section => section.Id).ToArray());
        Assert.AreEqual(DiscoverySectionState.Unavailable, sections[0].State);
        Assert.AreEqual("Anime", sections[0].Heading);
        Assert.AreEqual("Couldn't load this section right now.", sections[0].Message);
        Assert.AreEqual(DiscoverySectionState.Pending, sections[1].State);
        Assert.AreEqual(0, total);
    }

    [TestMethod]
    public void TheSameTitleFromTwoSourcesAppearsOnceAndHostileProviderTextIsOnlyData()
    {
        var hostile = Item("anime", "anilist", "1", "<img src=x onerror=alert(1)>", cover: "javascript:alert(1)");
        var batch = new DiscoveryBatch(
            new DiscoveryRequest("", DiscoveryCategory.Anime, DiscoveryMode.Trending),
            false,
            [Source(DiscoverySource.Anime, DiscoverySourceState.Ready, hostile, hostile with { Title = "Duplicate of the same identity" })]);

        var (sections, _) = DiscoverSectionComposer.Results(batch, new DiscoverBrowseQuery { Category = DiscoveryCategory.Anime }, Context());

        var card = sections.Single().Cards.Single();
        Assert.AreEqual("<img src=x onerror=alert(1)>", card.Title, "The text is kept as it is; Razor encodes it where it is shown.");
        Assert.IsNull(card.PosterUrl, "An address that is not a web address is never used as an image.");
    }

    [TestMethod]
    public void OnlyWebAddressesAndPathsOfThisApplicationAreUsedAsImagesOrLinks()
    {
        Assert.AreEqual("https://image.tmdb.org/t/p/w500/a.jpg", DiscoverUrls.Safe("https://image.tmdb.org/t/p/w500/a.jpg"));
        Assert.AreEqual("http://127.0.0.1:5000/x.png", DiscoverUrls.Safe(" http://127.0.0.1:5000/x.png "));
        Assert.AreEqual("/works/1/artwork/2?v=abc", DiscoverUrls.Safe("/works/1/artwork/2?v=abc"));
        foreach (var hostile in new[] { "javascript:alert(1)", "data:image/svg+xml;base64,AAAA", "vbscript:x", "//evil.example/x.png", "/\\evil.example", "file:///etc/passwd", "", "   ", "relative/path.png" })
        {
            Assert.IsNull(DiscoverUrls.Safe(hostile), hostile);
        }

        Assert.IsNull(DiscoverUrls.Safe(null));
    }

    [TestMethod]
    public void EveryPathOfThisApplicationSurvivesWhateverTheOperatingSystemMakesOfItAsAFileAddress()
    {
        // On Unix "/Library/Movie/1" parses as an absolute file address, on Windows it does not: the rule must not depend on the parser.
        foreach (var path in new[] { "/Library/Movie/6f1a", "/Library/Anime/6f1a", "/Books/ol-OL45804W", "/Discover/MangaImport?anilistId=7&title=Berserk", "/works/6f1a/artwork/3?v=0123456789ab", "/Novels/Work/6f1a" })
        {
            Assert.AreEqual(path, DiscoverUrls.Safe(path), path);
        }

        foreach (var hostile in new[] { "/x\ny", "/x\r\ny", "/x\u0000y", "https://example.test/\nx", "https://example.test/x\u0007y", "\t//evil.example", "/\\evil.example", "///evil.example" })
        {
            Assert.IsNull(DiscoverUrls.Safe(hostile), hostile.Replace("\n", "\\n"));
        }
    }

    [TestMethod]
    public void ACoverPlaceholderNeverShowsMarkupOrPunctuationAsTheInitial()
    {
        Assert.AreEqual("F", Jularr.Web.Features.Watchlist.WatchlistLabels.Initial("  Frieren"));
        Assert.AreEqual("i", Jularr.Web.Features.Watchlist.WatchlistLabels.Initial("<img src=x onerror=alert(1)>"));
        Assert.AreEqual("狂", Jularr.Web.Features.Watchlist.WatchlistLabels.Initial("「狂」の物語"));
        Assert.AreEqual("·", Jularr.Web.Features.Watchlist.WatchlistLabels.Initial("<<<>>>"));
        Assert.AreEqual("", Jularr.Web.Features.Watchlist.WatchlistLabels.Initial("   "));
    }

    [TestMethod]
    public void TheSignatureChangesWhenTheViewerWouldNoticeAndOnlyThen()
    {
        var one = Item("anime", "anilist", "1", "A");
        var two = Item("anime", "anilist", "2", "B");
        DiscoverSectionView Compose(params DiscoveryItem[] items) =>
            DiscoverSectionComposer.Landing([Row("trending-anime", "Anime", Source(DiscoverySource.Anime, DiscoverySourceState.Ready, items))], Context()).Single();

        Assert.AreEqual(Compose(one, two).Signature, Compose(one, two).Signature);
        Assert.AreNotEqual(Compose(one, two).Signature, Compose(two, one).Signature, "A different order is a change.");
        Assert.AreNotEqual(Compose(one).Signature, Compose(one, two).Signature);
        Assert.AreEqual(Compose(one).Signature, Compose(one with { Description = "a new synopsis" }).Signature, "A change nobody sees on the card is no change.");
    }
}
