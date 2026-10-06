using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Ui;

namespace Jularr.Tests;

/// <summary>
/// Discover per docs/mockups/discover: the address is the only state, cards answer "what is available for me",
/// one canonical work is shown once, and the filters narrow what was loaded.
/// </summary>
[TestClass]
public sealed class DiscoverPageTests
{
    private static readonly UiTextBundle Ui = UiTextBundle.English;

    private static DiscoverBrowseQuery Parse(string query)
    {
        var values = System.Web.HttpUtility.ParseQueryString(query);
        return DiscoverBrowseQuery.Parse(key => values[key]);
    }

    [TestMethod]
    public void TheDefaultAddressIsTheLandingWithNoFilters()
    {
        var query = Parse("");

        Assert.IsTrue(query.IsLanding);
        Assert.AreEqual(0, query.ActiveFilterCount);
        Assert.AreEqual("/", query.Href);
    }

    [TestMethod]
    public void EveryFilterRoundTripsThroughTheAddress()
    {
        var address = "/?q=frieren&category=anime&mode=top&genre=Sci-Fi,Fantasy&from=2015&to=2023&status=finished,ongoing&avail=library,requested&pref=1";

        var query = Parse(address[(address.IndexOf('?') + 1)..]);

        Assert.AreEqual("frieren", query.Text);
        Assert.AreEqual(DiscoveryCategory.Anime, query.Category);
        Assert.AreEqual(DiscoveryMode.Top, query.Mode);
        CollectionAssert.AreEqual(new[] { "Sci-Fi", "Fantasy" }, query.Genres.ToArray());
        Assert.AreEqual(2015, query.YearFrom);
        Assert.AreEqual(2023, query.YearTo);
        CollectionAssert.AreEqual(new[] { MediaReleaseStatus.Finished, MediaReleaseStatus.Ongoing }, query.Statuses.ToArray());
        CollectionAssert.AreEqual(new[] { DiscoverAvailabilityFilter.InLibrary, DiscoverAvailabilityFilter.Requested }, query.Availabilities.ToArray());
        Assert.IsTrue(query.PreferredLanguage);
        Assert.AreEqual(8, query.ActiveFilterCount, "Two genres, the year range, two statuses, two availabilities and the language are the filters.");
        Assert.AreEqual(8, query.ActiveFilters.Count, "Every active filter is one removable token.");
        Assert.AreEqual("/?q=frieren&category=anime&mode=top&genre=Sci-Fi&from=2015&to=2023&status=finished,ongoing&avail=library,requested&pref=1", query.WithoutFilter("genre:Fantasy").Href);
        Assert.AreEqual(0, query.WithoutFilters().ActiveFilterCount);
        Assert.IsFalse(query.IsLanding);
        Assert.AreEqual(address, query.Href);
    }

    [TestMethod]
    public void ASearchTextOverridesTheBrowseOrderingOfTheRequest()
    {
        var query = Parse("q=dune&mode=top");

        Assert.IsTrue(query.IsSearch);
        Assert.AreEqual(DiscoveryMode.Search, query.ToRequest().Mode);
        Assert.AreEqual(DiscoveryMode.Top, Parse("mode=top").ToRequest().Mode);
    }

    [TestMethod]
    public void ProviderBackedVideoModesAreAcceptedAndUnknownValuesAreIgnored()
    {
        Assert.AreEqual(DiscoveryMode.New, Parse("category=book&mode=new").Mode);
        Assert.AreEqual(DiscoveryMode.New, Parse("category=movie&mode=new").Mode);
        Assert.AreEqual(DiscoveryMode.New, Parse("category=tv&mode=new").Mode);
        Assert.AreEqual(DiscoveryMode.Upcoming, Parse("category=movie&mode=upcoming").Mode);
        Assert.AreEqual(DiscoveryMode.Upcoming, Parse("category=tv&mode=upcoming").Mode);
        Assert.AreEqual(DiscoveryMode.New, Parse("category=anime&mode=new").Mode);
        Assert.AreEqual(DiscoveryMode.Upcoming, Parse("category=manga&mode=upcoming").Mode);
        Assert.AreEqual(DiscoveryMode.Popular, Parse("category=light-novel&mode=popular").Mode);
        Assert.AreEqual(DiscoveryMode.TopRated, Parse("category=tv&mode=top-rated").Mode);
        Assert.AreEqual(DiscoveryMode.MyList, Parse("category=anime&mode=my-list").Mode);
        Assert.AreEqual(DiscoveryMode.Overview, Parse("category=movie&mode=my-list").Mode, "My List is an AniList view.");
        Assert.AreEqual(DiscoveryMode.Overview, Parse("category=book&mode=upcoming").Mode, "Open Library announces nothing.");
        Assert.AreEqual(DiscoveryMode.Overview, Parse("category=book&mode=top-rated").Mode, "Books have no rating feed.");
        Assert.AreEqual(DiscoveryMode.Overview, Parse("category=anime").Mode, "A scope opens its overview.");
        Assert.AreEqual(DiscoveryMode.Trending, Parse("category=anime&mode=trending").Mode);
        Assert.IsTrue(Parse("category=anime").IsLanding, "The overview of one type is its shelves one below the other.");
        Assert.IsFalse(Parse("category=anime&mode=trending").IsLanding);
        Assert.AreEqual(DiscoveryMode.Trending, Parse("mode=search").Mode, "search is derived from the text, never asked for.");

        var junk = Parse("year=abc&status=gone&avail=x&pref=0&category=games");
        Assert.IsNull(junk.YearFrom);
        Assert.IsNull(junk.YearTo);
        Assert.AreEqual(0, junk.Statuses.Count);
        Assert.AreEqual(0, junk.Availabilities.Count);
        Assert.IsFalse(junk.PreferredLanguage);
        Assert.AreEqual(DiscoveryCategory.All, junk.Category);
        Assert.IsTrue(junk.IsLanding);
    }

    [TestMethod]
    public void AMediaTypeTheProfileMayNotBrowseHasNoTabAndNoScope()
    {
        var series = new HashSet<Jularr.Web.Features.MediaCore.WorkMediaType> { Jularr.Web.Features.MediaCore.WorkMediaType.Series };
        var books = new HashSet<Jularr.Web.Features.MediaCore.WorkMediaType> { Jularr.Web.Features.MediaCore.WorkMediaType.LightNovel };

        CollectionAssert.AreEqual(
            new[] { DiscoveryCategory.All, DiscoveryCategory.Series },
            DiscoverScopes.Tabs.Select(tab => tab.Category).Where(category => DiscoverScopes.IsVisible(category, series)).ToArray());
        Assert.IsTrue(DiscoverScopes.IsVisible(DiscoveryCategory.LightNovel, books), "Light Novels and Books are separate scopes: each needs its own media type.");
        Assert.IsFalse(DiscoverScopes.IsVisible(DiscoveryCategory.Book, books));
        Assert.IsFalse(DiscoverScopes.IsVisible(DiscoveryCategory.Anime, series));
        Assert.IsTrue(DiscoverScopes.IsVisible(DiscoveryCategory.All, new HashSet<Jularr.Web.Features.MediaCore.WorkMediaType>()));
    }

    [TestMethod]
    public void TheDiscoverHandlersAreRateLimitedPerAccountBecauseEveryRequestCanStartProviderCalls()
    {
        var attribute = (Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute?)Attribute.GetCustomAttribute(
            typeof(Jularr.Web.Pages.IndexModel),
            typeof(Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute));

        Assert.IsNotNull(attribute);
        Assert.AreEqual(DiscoveryRegistration.RateLimitPolicy, attribute.PolicyName);
    }

    [TestMethod]
    public void MyAniListCountsAsAFilterOnlyWithoutASearchText()
    {
        Assert.AreEqual(0, Parse("category=anime&mode=my-list").ActiveFilterCount, "My List is a browse view, never a filter.");
        Assert.IsFalse(Parse("category=anime&mode=my-list").IsLanding);
        Assert.AreEqual(DiscoveryMode.MyList, Parse("category=anime&mode=my-list&genre=Horror").WithoutFilters().Mode);
        Assert.AreEqual(DiscoveryMode.Top, Parse("mode=top&genre=Horror").WithoutFilters().Mode, "Top is an ordering, not a filter.");
    }

    [TestMethod]
    public void GenresAreSpelledTheWayAniListSpellsThem()
    {
        Assert.AreEqual("Slice of Life", DiscoveryRequest.NormalizeGenre("slice of life"));
        Assert.AreEqual("Sci-Fi", DiscoveryRequest.NormalizeGenre("SCI-FI"));
        Assert.AreEqual("", DiscoveryRequest.NormalizeGenre("mahou shoujo"), "A genre Discover does not offer is ignored: free text would make every spelling a provider call.");
        foreach (var genre in DiscoveryRequest.KnownGenres)
        {
            Assert.IsNotNull(DiscoverGenres.Key(genre), $"{genre} needs a catalog key.");
            Assert.IsTrue(UiTranslationResources.TryGet(DiscoverGenres.Key(genre)!, out _));
        }
    }

    [TestMethod]
    public void TheMediaTypeSwitchOffersOnlyTypesWithADiscoverySource()
    {
        CollectionAssert.AreEqual(
            new[] { DiscoveryCategory.All, DiscoveryCategory.Anime, DiscoveryCategory.Series, DiscoveryCategory.Movie, DiscoveryCategory.LightNovel, DiscoveryCategory.Book, DiscoveryCategory.Manga },
            DiscoverScopes.Tabs.Select(tab => tab.Category).ToArray());

        Assert.IsTrue(DiscoverScopes.IsActive(DiscoveryCategory.Book, DiscoveryCategory.Book));
        Assert.IsFalse(DiscoverScopes.IsActive(DiscoveryCategory.Book, DiscoveryCategory.LightNovel), "Books and Light Novels are two scopes.");
        Assert.IsFalse(DiscoverScopes.IsActive(DiscoveryCategory.Manga, DiscoveryCategory.Book));
        Assert.IsTrue(DiscoverScopes.Includes(DiscoveryCategory.LightNovel, DiscoveryCategory.LightNovel));
        Assert.IsFalse(DiscoverScopes.Includes(DiscoveryCategory.Book, DiscoveryCategory.LightNovel));
        Assert.IsFalse(DiscoverScopes.Includes(DiscoveryCategory.Anime, DiscoveryCategory.Manga));
        Assert.AreEqual(DiscoveryCategory.Movie, Parse("category=movies").Category);
        Assert.AreEqual(DiscoveryCategory.Series, Parse("category=tv").Category);
        Assert.AreEqual(DiscoveryCategory.Book, Parse("category=books-light-novels").Category, "The retired combined scope of old bookmarks opens Books.");
        Assert.AreEqual("/?category=light-novel", Parse("category=light-novels").Href);
        Assert.AreEqual("/?category=book&mode=top", DiscoveryShelfLinks.ToDiscoverUrl(DiscoveryCategory.Book, DiscoveryMode.Top, ""));
    }

    // ---- The one language and request indicator ---------------------------------------------------

    private static DiscoveryItem Item(
        string category = "anime",
        string externalId = "1",
        string title = "Frieren",
        bool isLocal = false,
        string? localUrl = null,
        Guid? localMediaId = null,
        int? year = 2023,
        string? status = "FINISHED",
        string details = "https://anilist.co/anime/1",
        string? description = null,
        bool canImportSource = false,
        string provider = "anilist",
        string? workUrl = null,
        string? backdrop = null,
        string? trailerKey = null) =>
        new(
            $"{provider}:{category}:{externalId}",
            category,
            provider,
            externalId,
            title,
            null,
            description,
            "https://img.example/cover.jpg",
            "TV",
            status,
            year,
            null,
            28,
            null,
            null,
            ["Fantasy", "Adventure", "Gore"],
            isLocal,
            localUrl,
            details,
            canImportSource,
            LocalMediaId: localMediaId,
            BackdropUrl: backdrop,
            TrailerKey: trailerKey,
            WorkUrl: workUrl);

    private static AcquisitionRequest Request(
        string? audio,
        string? subtitle,
        AcquisitionRequestStatus status = AcquisitionRequestStatus.Pending,
        string externalId = "1") =>
        new(
            Guid.NewGuid(),
            MediaAcquisitionKind.Anime,
            "anilist",
            externalId,
            "Frieren",
            null,
            null,
            new AcquisitionRequestOptions { AudioLanguage = audio, SubtitleLanguage = subtitle }.ToPayloadJson(),
            "p",
            status,
            null,
            null,
            null,
            DateTime.UtcNow,
            DateTime.UtcNow,
            null,
            null);

    private static DiscoverContext Context(
        LibraryLanguagePreference? preference = null,
        IEnumerable<AcquisitionRequest>? requests = null,
        IDictionary<string, DiscoverLocalFacts>? local = null,
        params string[] requestable) =>
        new(
            Ui,
            preference ?? LibraryLanguagePreference.From("de", null),
            (requests ?? []).ToDictionary(request => (request.Kind, request.ExternalId)),
            new Dictionary<string, DiscoverLocalFacts>(local ?? new Dictionary<string, DiscoverLocalFacts>()),
            new Dictionary<string, Guid?>(),
            new HashSet<string>(requestable.Length == 0 ? ["anime"] : requestable.Where(category => category.Length > 0), StringComparer.Ordinal));

    [TestMethod]
    public void ALibraryTitleWithThePreferredLanguageSaysSo()
    {
        var local = new Dictionary<string, DiscoverLocalFacts>
        {
            ["/Library/Anime/a"] = new(["ja", "de"], ["en"], "/Library/Episode/e", "Continue watching")
        };

        var card = DiscoverCardFactory.Create(Item(isLocal: true, localUrl: "/Library/Anime/a"), Context(local: local));

        Assert.AreEqual(DiscoverStateKind.PreferredAvailable, card.State.Kind);
        Assert.AreEqual("DE available", card.State.Label);
        Assert.AreEqual("/Library/Episode/e", card.PlayUrl);
        Assert.AreEqual("Continue watching", card.PlayLabel);
        Assert.AreEqual("/Library/Anime/a", card.DetailUrl);
        Assert.IsFalse(card.ResolvesDetail);
    }

    [TestMethod]
    public void ALocalTitleOffersAPlayLinkOnlyWhereThereIsAPlayer()
    {
        var card = new MediaBannerCardData(
            MediaBannerKind.Anime,
            "Akatsuki no Sora",
            "/Library/Anime/1",
            AudioLanguages: ["ja"],
            Progress: new MediaBannerProgress(MediaBannerProgressState.NotStarted, MediaBannerUnit.Episode, 1, "/Library/Episode/e"));

        var playing = DiscoverLocalFacts.From(card, Ui, playbackEnabled: true);
        var managerOnly = DiscoverLocalFacts.From(card, Ui, playbackEnabled: false);

        Assert.AreEqual("/Library/Episode/e", playing.PlayUrl);
        Assert.IsNull(managerOnly.PlayUrl);
        Assert.IsNull(managerOnly.PlayLabel);
        CollectionAssert.AreEqual(new[] { "ja" }, managerOnly.Audio.ToArray(), "The languages stay: they are library facts, not playback.");
    }

    [TestMethod]
    public void ALibraryTitleWithOnlyOtherLanguagesSaysWhichOnes()
    {
        var local = new Dictionary<string, DiscoverLocalFacts>
        {
            ["/Library/Anime/a"] = new(["ja", "en"], [], null, null)
        };

        var card = DiscoverCardFactory.Create(Item(isLocal: true, localUrl: "/Library/Anime/a"), Context(local: local));

        Assert.AreEqual(DiscoverStateKind.OtherLanguages, card.State.Kind);
        Assert.AreEqual("Only JA/EN", card.State.Label);
        Assert.AreEqual("Not available in your preferred language", card.State.Hint);
    }

    [TestMethod]
    public void ALibraryTitleWithoutLanguageInformationIsSimplyInTheLibrary()
    {
        var reading = DiscoverCardFactory.Create(
            Item("manga", "5", isLocal: true, localUrl: "/Manga/Series/s"),
            Context());
        var unprobed = DiscoverCardFactory.Create(
            Item(isLocal: true, localUrl: "/Library/Anime/a"),
            Context(local: new Dictionary<string, DiscoverLocalFacts> { ["/Library/Anime/a"] = new([], [], null, null) }));
        var noPreference = DiscoverCardFactory.Create(
            Item(isLocal: true, localUrl: "/Library/Anime/a"),
            Context(
                LibraryLanguagePreference.None,
                local: new Dictionary<string, DiscoverLocalFacts> { ["/Library/Anime/a"] = new(["ja"], [], null, null) }));

        foreach (var card in new[] { reading, unprobed, noPreference })
        {
            Assert.AreEqual(DiscoverStateKind.InLibrary, card.State.Kind);
            Assert.AreEqual("In library", card.State.Label);
        }
    }

    [TestMethod]
    public void ARequestedTitleNamesTheLanguageItWasRequestedIn()
    {
        var preferred = DiscoverCardFactory.Create(Item(), Context(requests: [Request("de", null)]));
        var other = DiscoverCardFactory.Create(Item(), Context(requests: [Request("en", "en")]));
        var unspecified = DiscoverCardFactory.Create(
            Item(),
            Context(requests: [Request(null, null, AcquisitionRequestStatus.Downloading)]));

        Assert.AreEqual(DiscoverStateKind.Requested, preferred.State.Kind);
        Assert.AreEqual("DE requested", preferred.State.Label);
        Assert.AreEqual(DiscoverStateKind.RequestedOtherLanguage, other.State.Kind);
        Assert.AreEqual("EN requested", other.State.Label);
        Assert.AreEqual(DiscoverStateKind.Requested, unspecified.State.Kind);
        Assert.AreEqual("Getting media", unspecified.State.Label, "Without a language choice the stage of the request is the state.");
        Assert.AreEqual("downloading", unspecified.RequestStatus);
        Assert.IsNotNull(unspecified.RequestId);
        Assert.IsFalse(unspecified.CanRequest, "A requested title offers no second request.");
    }

    [TestMethod]
    public void BooksUseTheSameDiscoverAcquisitionFlow()
    {
        Assert.AreEqual(
            MediaAcquisitionKind.Book,
            DiscoverCardFactory.AcquisitionKindOf("book"));

        var context = Context(requestable: ["anime", "manga", "light-novel", "book"]);
        var book = DiscoverCardFactory.Create(
            Item("book", "ol-dune", title: "Dune", details: "/Books/ol-dune"),
            context);

        Assert.IsTrue(book.CanRequest);
        Assert.AreEqual(DiscoverStateKind.NotRequested, book.State.Kind);
    }

    [TestMethod]
    public void ATitleNobodyRequestedIsNotRequestedOrNotAvailableByPermission()
    {
        var requestable = DiscoverCardFactory.Create(Item(), Context(requestable: "anime"));
        var locked = DiscoverCardFactory.Create(Item(), Context(requestable: ""));

        Assert.AreEqual(DiscoverStateKind.NotRequested, requestable.State.Kind);
        Assert.AreEqual("Not requested", requestable.State.Label);
        Assert.IsTrue(requestable.CanRequest);
        Assert.AreEqual(DiscoverStateKind.NotAvailable, locked.State.Kind);
        Assert.AreEqual("Not available", locked.State.Label);
        Assert.IsFalse(locked.CanRequest);
    }

    // ---- Card content ----------------------------------------------------------------------------------

    [TestMethod]
    public void TheCardCarriesTypeYearAndOnlyGenresTheCatalogKnows()
    {
        var card = DiscoverCardFactory.Create(
            Item(description: "<i>A</i> mage&#39;s journey.<br><br>Then   more"),
            Context());

        Assert.AreEqual("Anime · 2023", card.Meta);
        Assert.AreEqual("A mage's journey. Then more", card.Description);
        CollectionAssert.AreEqual(new[] { "Fantasy", "Adventure" }, card.Genres.ToArray(), "Genres outside the catalog are not shown.");
        Assert.AreEqual("F", card.Initial);
        Assert.IsTrue(card.CanFollow);
        Assert.IsTrue(card.CanFollowFranchise);
        Assert.IsNull(card.DetailUrl, "An anime that only exists at AniList has no page here, and the provider page is never the destination.");
        Assert.IsFalse(card.ResolvesDetail, "AniList identities get no Work from opening a card.");
    }

    [TestMethod]
    public void AMovieOrSeriesWithoutAWorkResolvesItsDetailOnlyForAProfileThatMayRequestIt()
    {
        var tmdbMovie = Item("movie", "603", "The Matrix", provider: "tmdb", details: "https://www.themoviedb.org/movie/603");

        var requestable = DiscoverCardFactory.Create(tmdbMovie, Context(null, null, null, "movie"));
        var notRequestable = DiscoverCardFactory.Create(tmdbMovie, Context(null, null, null, "anime"));

        Assert.IsNull(requestable.DetailUrl);
        Assert.IsTrue(requestable.ResolvesDetail);
        Assert.IsFalse(notRequestable.ResolvesDetail, "Opening creates the canonical Work, so it needs the capability to request.");
        Assert.IsNull(notRequestable.DetailUrl);
    }

    [TestMethod]
    public void ATitleThatAlreadyHasACanonicalWorkOpensItsDetailWithoutBeingInTheLibrary()
    {
        var item = Item("tv", "1399", "Severance", provider: "tmdb", details: "https://www.themoviedb.org/tv/1399", workUrl: "/Library/Series/5d0f6a38-0000-0000-0000-000000000001");

        var card = DiscoverCardFactory.Create(item, Context(null, null, null, "tv"));

        Assert.IsFalse(card.IsLocal);
        Assert.AreEqual("/Library/Series/5d0f6a38-0000-0000-0000-000000000001", card.DetailUrl);
        Assert.IsFalse(card.ResolvesDetail);
    }

    [TestMethod]
    public void ABookOpensItsCatalogPageAndAProviderPageOrAnUnsafeAddressNeverBecomesTheDestination()
    {
        var book = DiscoverCardFactory.Create(Item("book", "ol-1", "Dune", provider: "openlibrary", details: "/Books/ol-1"), Context(null, null, null, "book"));
        var foreign = DiscoverCardFactory.Create(Item("book", "x", "X", provider: "openlibrary", details: "https://example.com/Books/x", workUrl: "//evil.example/x"), Context(null, null, null, "book"));

        Assert.AreEqual("/Books/ol-1", book.DetailUrl);
        Assert.IsNull(foreign.DetailUrl);
    }

    [TestMethod]
    public void ThePreviewKeepsOnlyASafeBackdropAndAYouTubeShapedTrailerKey()
    {
        var good = DiscoverCardFactory.Create(Item(backdrop: "https://img.example/b.jpg", trailerKey: "dQw4w9WgXcQ"), Context());
        var bad = DiscoverCardFactory.Create(Item(backdrop: "javascript:alert(1)", trailerKey: "x\" onload=\"1"), Context());

        Assert.AreEqual("https://img.example/b.jpg", good.BackdropUrl);
        Assert.AreEqual("dQw4w9WgXcQ", good.TrailerKey);
        Assert.IsNull(bad.BackdropUrl);
        Assert.IsNull(bad.TrailerKey);
    }

    [TestMethod]
    public void ALongDescriptionIsCutAtAWord()
    {
        var plain = DiscoverText.Plain(string.Join(' ', Enumerable.Repeat("wandering", 80)), 60);

        Assert.IsNotNull(plain);
        Assert.IsTrue(plain.Length <= 61, plain);
        Assert.IsTrue(plain.EndsWith('…'));
        Assert.IsFalse(plain.Contains("wanderi…", StringComparison.Ordinal));
        Assert.IsNull(DiscoverText.Plain(" <br> ", 60));
    }

    [TestMethod]
    public void TheOwnersMangaImportStaysAnActionAndTheTitleHasNoProviderLink()
    {
        var card = DiscoverCardFactory.Create(
            Item("manga", "7", details: DiscoveryCoordinator.BuildMangaImportUrl("7", "Berserk")),
            Context());

        Assert.AreEqual(DiscoveryCoordinator.BuildMangaImportUrl("7", "Berserk"), card.ImportMangaUrl);
        Assert.IsNull(card.DetailUrl);
        Assert.IsFalse(card.ResolvesDetail);
    }

    [TestMethod]
    public void ARecommendationWithoutAProviderIdentityCannotBeFollowedOrRequested()
    {
        var item = DiscoveryItem_FromCandidate();

        var card = DiscoverCardFactory.Create(item, Context());

        Assert.IsFalse(card.CanFollow);
        Assert.IsFalse(card.CanFollowFranchise);
        Assert.AreEqual("/Library/Anime/x", card.DetailUrl);
        Assert.IsTrue(card.IsLocal);
    }

    private static DiscoveryItem DiscoveryItem_FromCandidate() =>
        DiscoverRecommendations.ToItem(new Jularr.Web.Features.Recommendations.MediaRecommendationCandidate(
            "local-1",
            Jularr.Web.Features.MediaCore.WorkMediaType.Anime,
            null,
            "Local Show",
            null,
            [],
            null,
            "/Library/Anime/x",
            2020,
            IsLocal: true));

    // ---- One canonical work, shown once ---------------------------------------------------------------------

    [TestMethod]
    public void ProviderEntriesOfOneLibraryWorkCollapseToTheFirst()
    {
        var work = Guid.NewGuid();
        var items = DiscoverCanonical.Collapse(
        [
            Item(externalId: "10", title: "Series", isLocal: true, localUrl: "/Library/Anime/a", localMediaId: work),
            Item(externalId: "11", title: "Series Season 2", isLocal: true, localUrl: "/Library/Anime/a", localMediaId: work),
            Item(externalId: "10", title: "Series again"),
            Item(externalId: "20", title: "Unrelated"),
            Item("manga", "10", title: "Other type, same id")
        ]);

        CollectionAssert.AreEqual(
            new[] { "Series", "Unrelated", "Other type, same id" },
            items.Select(item => item.Title).ToArray());
    }

    // ---- Filters narrow what was loaded ------------------------------------------------------------------------

    [TestMethod]
    public void TheFiltersNarrowYearStatusAvailabilityAndLanguage()
    {
        var local = new Dictionary<string, DiscoverLocalFacts>
        {
            ["/Library/Anime/a"] = new(["de"], [], null, null)
        };
        var context = Context(local: local, requests: [Request("de", null, externalId: "3")]);
        DiscoverCardView[] cards =
        [
            DiscoverCardFactory.Create(Item(externalId: "1", title: "In library", isLocal: true, localUrl: "/Library/Anime/a", year: 2020), context),
            DiscoverCardFactory.Create(Item(externalId: "2", title: "Newcomer", year: 2024, status: "RELEASING"), context),
            DiscoverCardFactory.Create(Item(externalId: "3", title: "Wished", year: 2024), context)
        ];

        Assert.AreEqual("In library", DiscoverFilter.Apply(cards, Parse("year=2020")).Single().Title);
        Assert.AreEqual("Newcomer", DiscoverFilter.Apply(cards, Parse("status=ongoing")).Single().Title);
        Assert.AreEqual("In library", DiscoverFilter.Apply(cards, Parse("avail=library")).Single().Title);
        Assert.AreEqual("Wished", DiscoverFilter.Apply(cards, Parse("avail=requested")).Single().Title);
        Assert.AreEqual("Newcomer", DiscoverFilter.Apply(cards, Parse("avail=new")).Single().Title);
        Assert.AreEqual("In library", DiscoverFilter.Apply(cards, Parse("pref=1")).Single().Title);
        Assert.AreEqual(3, DiscoverFilter.Apply(cards, Parse("")).Count);
        Assert.AreEqual(0, DiscoverFilter.Apply(cards, Parse("year=2020&status=ongoing")).Count);
    }
}
