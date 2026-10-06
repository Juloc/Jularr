using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Ui;
using Jularr.Web.Pages;

namespace Jularr.Tests;

/// <summary>The Discover completion contracts: provider paging and filters, browse views, ranking scope and the Hero pool.</summary>
[TestClass]
public sealed class DiscoverCompletionTests
{
    private static AniListDiscoveryOptions Options(AniListDiscoveryKind kind, DiscoveryMode mode, DiscoveryFilter? filter = null, string search = "", int page = 1) =>
        new(page, 24, search, mode, filter ?? DiscoveryFilter.None, kind);

    [TestMethod]
    public void MangaExcludesTheNovelFormatAndLightNovelsRequireItOnTheProviderSide()
    {
        var manga = Options(AniListDiscoveryKind.Manga, DiscoveryMode.Trending).Variables();
        var novels = Options(AniListDiscoveryKind.LightNovel, DiscoveryMode.Trending).Variables();
        var both = Options(AniListDiscoveryKind.Reading, DiscoveryMode.Search, search: "overlord").Variables();

        CollectionAssert.AreEqual(new[] { "NOVEL" }, (string[])manga["formatNot"]!);
        Assert.IsFalse(manga.ContainsKey("format"));
        Assert.AreEqual("NOVEL", novels["format"]);
        Assert.IsFalse(novels.ContainsKey("formatNot"));
        Assert.IsFalse(both.ContainsKey("format") || both.ContainsKey("formatNot"), "The all-types search asks for both reading formats.");
    }

    [TestMethod]
    public void EveryBrowseViewMapsToItsOwnAniListRankingAndAFilterThatIsOffIsLeftOut()
    {
        CollectionAssert.AreEqual(new[] { "TRENDING_DESC", "POPULARITY_DESC" }, (string[])Options(AniListDiscoveryKind.Anime, DiscoveryMode.Trending).Variables()["sort"]!);
        CollectionAssert.AreEqual(new[] { "POPULARITY_DESC" }, (string[])Options(AniListDiscoveryKind.Anime, DiscoveryMode.Popular).Variables()["sort"]!);
        CollectionAssert.AreEqual(new[] { "START_DATE_DESC" }, (string[])Options(AniListDiscoveryKind.Anime, DiscoveryMode.New).Variables()["sort"]!);

        var upcoming = Options(AniListDiscoveryKind.Manga, DiscoveryMode.Upcoming).Variables();
        CollectionAssert.AreEqual(new[] { "NOT_YET_RELEASED" }, (string[])upcoming["status"]!);

        var recent = Options(AniListDiscoveryKind.Anime, DiscoveryMode.New).Variables();
        CollectionAssert.AreEqual(new[] { "RELEASING", "FINISHED" }, (string[])recent["status"]!);
        Assert.IsTrue((int)recent["startTo"]! > 20200101, "New only holds titles that have already started.");

        var rated = Options(AniListDiscoveryKind.LightNovel, DiscoveryMode.TopRated).Variables();
        Assert.AreEqual(3000, rated["popularityMin"], "Top rated needs an audience.");

        var plain = Options(AniListDiscoveryKind.Anime, DiscoveryMode.Top).Variables();
        Assert.IsFalse(plain.Keys.Any(key => key is "genre" or "status" or "startFrom" or "startTo" or "format" or "formatNot" or "search" or "popularityMin"), "A filter that is off is not sent; AniList rejects an explicit null for some of them.");
    }

    [TestMethod]
    public void TheViewersFiltersAreAskedOfTheProviderSoPagingStaysTruthful()
    {
        var filter = new DiscoveryFilter(["Fantasy", "Romance"], 2015, 2023, [MediaReleaseStatus.Ongoing, MediaReleaseStatus.Hiatus]);

        var variables = Options(AniListDiscoveryKind.Manga, DiscoveryMode.TopRated, filter, page: 4).Variables();

        Assert.AreEqual(4, variables["page"]);
        CollectionAssert.AreEqual(new[] { "Fantasy", "Romance" }, (string[])variables["genre"]!);
        CollectionAssert.AreEqual(new[] { "RELEASING", "HIATUS" }, (string[])variables["status"]!);
        Assert.AreEqual(20141231, variables["startFrom"], "From 2015 means after the end of 2014.");
        Assert.AreEqual(20240101, variables["startTo"], "To 2023 means before the start of 2024.");
    }

    [TestMethod]
    public void ASearchIsNotRankedAndNotNarrowedByTheBrowseView()
    {
        var search = Options(AniListDiscoveryKind.Anime, DiscoveryMode.Search, search: "frieren").Variables();

        Assert.AreEqual("frieren", search["search"]);
        Assert.IsFalse(search.ContainsKey("sort"));
        Assert.IsFalse(search.ContainsKey("status"));
    }

    [TestMethod]
    public void TheBrowseViewsAreOnlyThoseAProviderCanAnswerAndTheOverviewComesFirst()
    {
        Assert.AreEqual(DiscoveryMode.Overview, DiscoverBrowseModes.For(DiscoveryCategory.Anime)[0]);
        CollectionAssert.AreEqual(
            new[] { DiscoveryMode.Overview, DiscoveryMode.Trending, DiscoveryMode.Top, DiscoveryMode.New, DiscoveryMode.Upcoming, DiscoveryMode.Popular, DiscoveryMode.TopRated, DiscoveryMode.MyList },
            DiscoverBrowseModes.For(DiscoveryCategory.Manga).ToArray());
        CollectionAssert.DoesNotContain(DiscoverBrowseModes.For(DiscoveryCategory.Series).ToArray(), DiscoveryMode.MyList);
        CollectionAssert.AreEqual(new[] { DiscoveryMode.Overview, DiscoveryMode.Trending, DiscoveryMode.Popular, DiscoveryMode.New }, DiscoverBrowseModes.For(DiscoveryCategory.Book).ToArray());
        Assert.AreEqual(0, DiscoverBrowseModes.StatusesFor(DiscoveryCategory.Book).Count, "Open Library has no release status to filter on.");
        CollectionAssert.AreEqual(new[] { MediaReleaseStatus.Upcoming, MediaReleaseStatus.Finished }, DiscoverBrowseModes.StatusesFor(DiscoveryCategory.Movie).ToArray());
    }

    [TestMethod]
    public void NoRankingIsLabelledRegionalBecauseNoSourceSuppliesARegionalSignal()
    {
        foreach (var category in Enum.GetValues<DiscoveryCategory>())
        {
            foreach (var mode in DiscoverBrowseModes.For(category))
            {
                Assert.AreNotEqual(DiscoveryRankingScope.Regional, DiscoverBrowseModes.ScopeOf(category, mode), $"{category} {mode}");
            }
        }

        Assert.AreEqual(DiscoveryRankingScope.Global, DiscoverBrowseModes.ScopeOf(DiscoveryCategory.Book, DiscoveryMode.Trending), "Open Library trending is global.");
    }

    [TestMethod]
    public void TheFilterCacheKeyDoesNotDependOnTheOrderOfTheSelectionAndRequestsMergeTheShelfGenre()
    {
        Assert.AreEqual(new DiscoveryFilter(["Drama", "Fantasy"]).CacheKey, new DiscoveryFilter(["Fantasy", "Drama"]).CacheKey);
        Assert.IsTrue(DiscoveryFilter.None.IsEmpty);

        var request = new DiscoveryRequest("", DiscoveryCategory.Anime, DiscoveryMode.Top, "Horror") { Filter = new DiscoveryFilter(["Fantasy", "Horror"]) };
        CollectionAssert.AreEqual(new[] { "Horror", "Fantasy" }, request.EffectiveGenres.ToArray());
        Assert.AreEqual(1, request.Page);
    }

    private static DiscoverCardView Card(string key, int? year, string? status, bool local)
    {
        var item = new DiscoveryItem($"anime:anilist:{key}", "anime", "anilist", key, $"Title {key}", null, null, null, null, status, year, null, null, null, null, [], local, local ? $"/Library/Anime/{key}" : null, "/x", false);
        var context = new DiscoverContext(
            UiTextBundle.English,
            LibraryLanguagePreference.None,
            new Dictionary<(MediaAcquisitionKind, string), AcquisitionRequest>(),
            new Dictionary<string, DiscoverLocalFacts>(),
            new Dictionary<string, Guid?>(),
            new HashSet<string>());
        return DiscoverCardFactory.Create(item, context);
    }

    [TestMethod]
    public void MultiSelectFiltersAreAnyOfPerGroupAndAllOfAcrossGroupsAndATitleWithoutTheFieldDoesNotPassARange()
    {
        var cards = new[]
        {
            Card("a", 2016, "RELEASING", local: true),
            Card("b", 2020, "FINISHED", local: false),
            Card("c", 2022, "HIATUS", local: false),
            Card("d", null, "RELEASING", local: true)
        };
        var years = new DiscoverBrowseQuery { YearFrom = 2015, YearTo = 2021, Statuses = [MediaReleaseStatus.Ongoing, MediaReleaseStatus.Finished] };

        CollectionAssert.AreEqual(new[] { "anime:anilist:a", "anime:anilist:b" }, DiscoverFilter.Apply(cards, years).Select(card => card.Key).ToArray());
        CollectionAssert.AreEqual(
            new[] { "anime:anilist:a" },
            DiscoverFilter.Apply(cards, years with { Availabilities = [DiscoverAvailabilityFilter.InLibrary] }).Select(card => card.Key).ToArray());
        CollectionAssert.AreEqual(
            new[] { "anime:anilist:a", "anime:anilist:b" },
            DiscoverFilter.Apply(cards, years with { Availabilities = [DiscoverAvailabilityFilter.InLibrary, DiscoverAvailabilityFilter.NotInLibrary] }).Select(card => card.Key).ToArray());
    }

    [TestMethod]
    public void AViewMayContinueOnlyWhileAReadySourceReturnsAWholePage()
    {
        DiscoveryItem Item(int index) => new($"anime:anilist:{index}", "anime", "anilist", index.ToString(), $"Title {index}", null, null, null, null, null, null, null, null, null, null, [], false, null, "/x", false);
        DiscoveryBatch Batch(DiscoverySourceState state, int count) =>
            new(new DiscoveryRequest("", DiscoveryCategory.Anime, DiscoveryMode.Top), false, [new DiscoverySourceResult(DiscoverySource.Anime, state, [.. Enumerable.Range(1, count).Select(Item)])]);

        Assert.IsTrue(DiscoverPaging.MayContinue(Batch(DiscoverySourceState.Ready, DiscoverySources.PageSize)));
        Assert.IsFalse(DiscoverPaging.MayContinue(Batch(DiscoverySourceState.Ready, DiscoverySources.PageSize - 1)), "A short page is the end of the source.");
        Assert.IsFalse(DiscoverPaging.MayContinue(Batch(DiscoverySourceState.Unavailable, DiscoverySources.PageSize)));
        Assert.AreEqual(20, DiscoverySources.FullPageSize(DiscoverySource.Movies), "TMDB pages hold twenty titles.");
    }

    [TestMethod]
    public void TheHeroRotatesThroughTheClassesInPriorityOrderAndShowsATitleOnce()
    {
        IndexModel.HomeHeroSlide Slide(string label, string title) => new(label, title, null, null, null, null, null, null, false, "/x", "Open", false, "/x", "Details", IndexModel.HomeHeroSecondary.Details);

        var hero = IndexModel.RotateClasses(
            [
                [Slide("Continue", "A"), Slide("Continue", "B"), Slide("Continue", "C")],
                [Slide("New", "A"), Slide("New", "D")],
                [Slide("For you", "E")],
                [Slide("Release", "F")]
            ],
            6);

        CollectionAssert.AreEqual(new[] { "A", "E", "F", "B", "D", "C" }, hero.Select(slide => slide.Title).ToArray());
        Assert.AreEqual("Continue", hero[0].Label, "Continue leads without monopolizing the Hero.");
        Assert.AreEqual(3, IndexModel.RotateClasses([[Slide("Continue", "A"), Slide("Continue", "B"), Slide("Continue", "C")]], 3).Count);
        Assert.AreEqual(0, IndexModel.RotateClasses([], 6).Count);
    }

    private static string ReadSource(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine([directory!.FullName, "src", "Jularr.Web", .. parts]));
    }

    [TestMethod]
    public void TheBrowserPagesByScrollingAndNeverLetsAnOlderResponseOverwriteANewerQuery()
    {
        var script = ReadSource("wwwroot", "js", "discover.js");

        // Paging: a sentinel asks for the next page, titles already shown are skipped, a failed page offers a retry and the end stops it.
        StringAssert.Contains(script, "new IntersectionObserver(");
        StringAssert.Contains(script, "params.set(\"pg\", String(pager.page + 1));");
        StringAssert.Contains(script, "known.has(key)");
        StringAssert.Contains(script, "pager.empty >= 5");
        StringAssert.Contains(script, "data-dc-has-more".Replace("data-dc-has-more", "dcHasMore"));

        // Live search: one debounce, the previous request is aborted and a response of an older version is dropped.
        StringAssert.Contains(script, "SEARCH_DELAY");
        StringAssert.Contains(script, "abortController");
        StringAssert.Contains(script, "version !== requestVersion");
        StringAssert.Contains(script, "address !== pager.address", "A page of an older address is never appended.");
    }

    [TestMethod]
    public void TheFilterIsAMultiSelectWithAYearRangeAndNoBrowseViewsAndTheBrowseNavigationStartsWithAll()
    {
        var filter = ReadSource("Pages", "Shared", "_DiscoverFilter.cshtml");
        var page = ReadSource("Pages", "Index.cshtml");

        StringAssert.Contains(filter, "type=\"checkbox\" form=\"dc-form\" name=\"genre\"");
        StringAssert.Contains(filter, "name=\"from\"");
        StringAssert.Contains(filter, "name=\"to\"");
        Assert.IsFalse(filter.Contains("<select", StringComparison.Ordinal), "No dropdowns: genres, statuses and availabilities are checkbox lists and the year is a range.");
        Assert.IsFalse(filter.Contains("name=\"mode\"", StringComparison.Ordinal), "The browse views are not filters.");
        StringAssert.Contains(page, "class=\"dc-browse\"");
        StringAssert.Contains(page, "class=\"dc-tokens\"");
        StringAssert.Contains(page, "discover.filter.clearAll");
    }
}

