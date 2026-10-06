using System.Net;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Collections;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Progress;

namespace Jularr.Tests;

/// <summary>
/// The Library page rendered end to end (docs/mockups/library): the Library | Collections switch, one
/// toolbar with the filter count, sort and layout, poster cards with a language line, and every body state.
/// </summary>
[TestClass]
public sealed class LibraryPageRenderTests
{
    private const string Profile = "test-profile";
    private const string Cover = "https://img.example/starfall.jpg";

    private static async Task<(ManageSheetPageTestHost Host, SeededAnime Starfall, SeededAnime Amber)> SeedAsync()
    {
        var host = await ManageSheetPageTestHost.CreateAsync();
        var seed = new LibraryCanonicalSeed(host.Db);
        var starfall = await seed.AddAnimeAsync(
            "Starfall Chronicle",
            [(1, 1, true), (1, 2, true), (1, 3, false)],
            audio: ["jpn", "ger"],
            subtitles: ["eng"]);
        host.Db.Add(new AnimeMetadata
        {
            AnimeId = starfall.Anime.Id,
            Provider = "anilist",
            ExternalId = "9001",
            PreferredTitle = "Starfall Chronicle",
            CoverImageUrl = Cover,
            Format = "TV",
            Status = "RELEASING",
            SeasonYear = 2024,
            EpisodeCount = 12,
            AverageScore = 82
        });
        await seed.SetProgressAsync(Profile, starfall.Work, starfall.Episodes[0].Canonical, 0, null, completed: true, DateTime.UtcNow);

        var amber = await seed.AddAnimeAsync("Amber Nights", [(1, 1, false)]);
        await host.Db.SaveChangesAsync();
        return (host, starfall, amber);
    }

    [TestMethod]
    public async Task ALibraryCardOffersNoPlayActionOnAManagerOnlyInstance()
    {
        var (host, _, _) = await SeedAsync();
        await using var _host = host;

        StringAssert.Contains(WebUtility.HtmlDecode(await host.GetHtmlAsync("/Library", asOwner: false)), "Continue watching");

        await host.Modules.SetAsync(Jularr.Web.Features.Instance.InstanceModule.Playback, false);

        var managerOnly = WebUtility.HtmlDecode(await host.GetHtmlAsync("/Library", asOwner: false));
        StringAssert.Contains(managerOnly, "Starfall Chronicle");
        foreach (var word in new[] { "Continue watching", "Start watching", "Watch again", "/Library/Watch" })
        {
            Assert.IsFalse(managerOnly.Contains(word, StringComparison.Ordinal), $"The Library must not show '{word}' without Playback.");
        }
    }

    [TestMethod]
    public async Task LibraryShowsTheSwitchToolbarAndPosterCardsWithLanguagesAndAvailability()
    {
        var (host, starfall, _) = await SeedAsync();
        await using var _host = host;
        host.Db.Add(new ProfilePlaybackPreferences { ProfileId = Profile, PreferredAudioLanguage = "de" });
        await host.Db.SaveChangesAsync();

        var html = WebUtility.HtmlDecode(await host.GetHtmlAsync("/Library", asOwner: false));

        var head = Between(html, "<header class=\"lib-head\"", "</header>");
        StringAssert.Contains(head, "Library");
        StringAssert.Contains(head, "Collections");
        StringAssert.Contains(head, "lib-switch-item is-active\" href=\"/Library\"");
        StringAssert.Contains(head, "href=\"/Library?section=collections\"");
        Assert.IsFalse(head.Contains("Import from Sonarr", StringComparison.Ordinal), "Admin tools do not belong to the consumer Library.");
        StringAssert.Contains(html, "library-type-tabs");
        StringAssert.Contains(html, "2 items");

        var toolbar = Between(html, "<div class=\"lib-toolbar\"", "</nav>");
        StringAssert.Contains(toolbar, "Filters");
        StringAssert.Contains(toolbar, "Sort: Recently added");
        StringAssert.Contains(toolbar, "href=\"/Library?view=list\"");
        Assert.IsFalse(toolbar.Contains("lib-badge", StringComparison.Ordinal), "No filter is active.");

        var starfallCard = Between(html, "<article class=\"lib-card lib-card-partial\"", "</article>");
        StringAssert.Contains(starfallCard, Cover);
        StringAssert.Contains(starfallCard, $"/Library/Anime/{starfall.Anime.Id}");
        StringAssert.Contains(starfallCard, "Starfall Chronicle");
        StringAssert.Contains(starfallCard, "Episode 2");
        StringAssert.Contains(starfallCard, "lib-card-bar");
        StringAssert.Contains(starfallCard, "Continue watching");
        StringAssert.Contains(starfallCard, "lib-lang is-preferred\">DE<");
        StringAssert.Contains(starfallCard, ">JA<");
        StringAssert.Contains(starfallCard, ">EN<");
        StringAssert.Matches(starfallCard, new Regex(@"\d+ / \d+ available"));

        var amberCard = Between(html, "<article class=\"lib-card lib-card-missing\"", "</article>");
        StringAssert.Contains(amberCard, "Amber Nights");
        StringAssert.Contains(amberCard, "lib-card-initial");
        StringAssert.Contains(amberCard, "Not available");
    }

    [TestMethod]
    public async Task ActiveFiltersAreCountedOnTheButtonKeptInTheAddressAndNarrowTheGrid()
    {
        var (host, _, _) = await SeedAsync();
        await using var _host = host;

        var html = WebUtility.HtmlDecode(await host.GetHtmlAsync("/Library?progress=inprogress&year=2024&sort=title", asOwner: false));

        StringAssert.Contains(html, "1 of 2 items");
        StringAssert.Contains(html, "<span class=\"lib-badge\">2</span>");
        StringAssert.Contains(html, "Sort: Title (A-Z)");
        StringAssert.Contains(html, "Starfall Chronicle");
        Assert.IsFalse(html.Contains("Amber Nights", StringComparison.Ordinal));

        var panel = Between(html, "<div class=\"lib-pop-panel lib-filter-panel\"", "</form>");
        StringAssert.Contains(panel, "name=\"sort\" value=\"title\"");
        StringAssert.Contains(panel, "name=\"progress\" value=\"inprogress\" checked");
        StringAssert.Contains(panel, "Reset all");
        StringAssert.Contains(panel, "href=\"/Library?sort=title\"");
        StringAssert.Contains(panel, "<option value=\"2024\" selected");
        StringAssert.Contains(panel, "Audio language");
        StringAssert.Contains(panel, "Partially available");
        Assert.IsFalse(panel.Contains("Fully available", StringComparison.Ordinal), "An option nothing matches is not offered.");
        Assert.IsFalse(panel.Contains("My preferred language", StringComparison.Ordinal), "Without a preference the option is not offered.");
    }

    [TestMethod]
    public async Task FiltersThatMatchNothingOfferAResetAndTheListLayoutIsTheSameCardAsARow()
    {
        var (host, _, _) = await SeedAsync();
        await using var _host = host;

        var none = WebUtility.HtmlDecode(await host.GetHtmlAsync("/Library?progress=completed&view=list", asOwner: false));
        StringAssert.Contains(none, "No titles match these filters");
        StringAssert.Contains(none, "Reset filters");
        StringAssert.Contains(none, "href=\"/Library?view=list\"");
        Assert.IsFalse(none.Contains("<article class=\"lib-card", StringComparison.Ordinal));

        var list = WebUtility.HtmlDecode(await host.GetHtmlAsync("/Library?view=list", asOwner: false));
        StringAssert.Contains(list, "class=\"lib-grid lib-list\"");
        StringAssert.Contains(list, "lib-icon-btn is-active\" href=\"/Library?view=list\"");
    }

    [TestMethod]
    public async Task EmptyLibraryPointsEveryoneToDiscoverInsteadOfRootSetup()
    {
        await using var host = await ManageSheetPageTestHost.CreateAsync();

        var owner = WebUtility.HtmlDecode(await host.GetHtmlAsync("/Library", asOwner: true));
        StringAssert.Contains(owner, "Library is empty");
        StringAssert.Contains(owner, "Find something in Discover and request it.");
        StringAssert.Contains(owner, "href=\"/Discover\"");
        Assert.IsFalse(owner.Contains("Add a root", StringComparison.Ordinal), "Roots are an Admin concern.");
        Assert.IsFalse(owner.Contains("Import from Sonarr", StringComparison.Ordinal), "Importing is an Admin task, not a Library header action.");
        Assert.IsFalse(owner.Contains("Manage roots", StringComparison.Ordinal));
        Assert.IsFalse(owner.Contains("lib-toolbar", StringComparison.Ordinal), "Nothing to filter yet.");

        var member = WebUtility.HtmlDecode(await host.GetHtmlAsync("/Library", asOwner: false));
        StringAssert.Contains(member, "Find something in Discover and request it.");
        Assert.IsFalse(member.Contains("Add a root", StringComparison.Ordinal));
        Assert.IsFalse(member.Contains("Import from Sonarr", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CollectionsViewShowsMosaicTilesWithCountAndKindInsteadOfTheMediaTabs()
    {
        var (host, starfall, _) = await SeedAsync();
        await using var _host = host;
        var work = starfall.Work;
        var manual = new Collection { ProfileId = Profile, Kind = CollectionKind.Manual, Name = "Weekend Picks", SortOrder = 1 };
        var smart = new Collection { ProfileId = Profile, Kind = CollectionKind.Smart, Name = "Airing now", SortOrder = 2 };
        var others = new Collection { ProfileId = "someone-else", Kind = CollectionKind.Manual, Name = "Not mine", SortOrder = 1 };
        host.Db.AddRange(manual, smart, others);
        host.Db.Add(new CollectionItem { CollectionId = manual.Id, WorkId = work.Id, Source = CollectionItemSource.Manual });
        await host.Db.SaveChangesAsync();

        var html = WebUtility.HtmlDecode(await host.GetHtmlAsync("/Library?section=collections", asOwner: false));

        StringAssert.Matches(
            Between(html, "<header class=\"lib-head\"", "</header>"),
            new Regex(@"lib-switch-item is-active""\s+href=""/Library\?section=collections"""));
        Assert.IsFalse(html.Contains("library-type-tabs", StringComparison.Ordinal), "Collections are not a media type.");
        Assert.IsFalse(html.Contains("lib-filter", StringComparison.Ordinal), "Filters belong to the Library view.");
        StringAssert.Contains(html, "2 collections");
        StringAssert.Contains(html, "Manage collections");
        Assert.IsFalse(html.Contains("Not mine", StringComparison.Ordinal), "Another profile's collections stay private.");

        var tiles = html.Split("<a class=\"lib-tile\"");
        Assert.AreEqual(3, tiles.Length, "Two tiles: the profile's own collections.");
        var first = tiles[1][..tiles[1].IndexOf("</a>", StringComparison.Ordinal)];
        StringAssert.Contains(first, "Weekend Picks");
        StringAssert.Contains(first, "1 item");
        StringAssert.Contains(first, "Manual");
        StringAssert.Contains(first, Cover);
        StringAssert.Contains(first, $"/Collections/{manual.Id}");

        var second = tiles[2][..tiles[2].IndexOf("</a>", StringComparison.Ordinal)];
        StringAssert.Contains(second, "Airing now");
        StringAssert.Contains(second, "0 items");
        StringAssert.Contains(second, "Smart");
        StringAssert.Contains(second, "lib-card-initial");
    }

    [TestMethod]
    public async Task EmptyCollectionsViewLinksToWhereCollectionsAreCreated()
    {
        await using var host = await ManageSheetPageTestHost.CreateAsync();

        var html = WebUtility.HtmlDecode(await host.GetHtmlAsync("/Library?section=collections", asOwner: false));

        StringAssert.Contains(html, "No collections yet.");
        StringAssert.Contains(html, "Create a collection");
        StringAssert.Contains(html, "href=\"/Collections\"");
    }

    [TestMethod]
    public async Task EntriesCarryPlayableAndMissingUnitsFormatAndWhenTheTitleWasLastWatched()
    {
        var (host, starfall, amber) = await SeedAsync();
        await using var _host = host;

        var read = await new LibraryMediaCardQuery(host.Db).GetEntriesAsync(Profile, [WorkMediaType.Anime], CancellationToken.None);

        Assert.IsFalse(read.Degraded);
        var starfallEntry = read.Entries.Single(x => x.Card.Href.EndsWith(starfall.Anime.Id.ToString(), StringComparison.Ordinal));
        Assert.AreEqual(2, starfallEntry.PlayableUnits);
        Assert.AreEqual(1, starfallEntry.MissingUnits, "Episode 3 is known but has no file.");
        Assert.AreEqual("TV", starfallEntry.Format);
        Assert.AreEqual(Cover, starfallEntry.PosterUrl);
        Assert.IsNotNull(starfallEntry.LastWatchedAt);

        var amberEntry = read.Entries.Single(x => x.Card.Href.EndsWith(amber.Anime.Id.ToString(), StringComparison.Ordinal));
        Assert.AreEqual(0, amberEntry.PlayableUnits);
        Assert.AreEqual(1, amberEntry.MissingUnits);
        Assert.IsNull(amberEntry.LastWatchedAt);
    }

    [TestMethod]
    public async Task MoviesAndSeriesShareTheLibraryWithMediaTypeTabsBoundToTheSameRead()
    {
        var (host, _, _) = await SeedAsync();
        await using var _host = host;
        var seed = new LibraryCanonicalSeed(host.Db);
        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Moon Empire", 2024);
        await seed.AddVideoAsync(movie, null, audio: ["ger"], durationSeconds: 7440);
        var series = await seed.AddWorkAsync(WorkMediaType.Series, "Dark Harbor", 2021);
        await seed.AddVideoAsync(series, await seed.AddEpisodeAsync(series, 1, 1), audio: ["ger"]);
        await seed.AddEpisodeAsync(series, 1, 2);

        var all = WebUtility.HtmlDecode(await host.GetHtmlAsync("/Library", asOwner: false));
        var tabs = Between(all, "<nav class=\"library-type-tabs\"", "</nav>");
        StringAssert.Contains(tabs, "All");
        StringAssert.Contains(tabs, "href=\"/Library?type=movie\"");
        StringAssert.Contains(tabs, "href=\"/Library?type=series\"");
        StringAssert.Contains(tabs, "href=\"/Reading\"");
        StringAssert.Contains(all, "4 items");

        var movies = WebUtility.HtmlDecode(await host.GetHtmlAsync("/Library?type=movie", asOwner: false));
        StringAssert.Contains(Between(movies, "<nav class=\"library-type-tabs\"", "</nav>"), "library-type-tab active\" href=\"/Library?type=movie\"");
        StringAssert.Contains(movies, "1 item");
        var movieCard = Between(movies, "<article class=\"lib-card", "</article>");
        StringAssert.Contains(movieCard, "Moon Empire");
        StringAssert.Contains(movieCard, $"href=\"/Library/Movie/{movie.Id}\"");
        StringAssert.Contains(movies, "2024 · 2h 04m");
        StringAssert.Contains(movies, "name=\"type\" value=\"movie\"");
        Assert.IsFalse(movies.Contains("Starfall Chronicle", StringComparison.Ordinal));

        var seriesPage = WebUtility.HtmlDecode(await host.GetHtmlAsync("/Library?type=series", asOwner: false));
        StringAssert.Contains(seriesPage, "Dark Harbor");
        StringAssert.Contains(seriesPage, $"href=\"/Library/Series/{series.Id}\"");
        StringAssert.Matches(seriesPage, new Regex(@"\d+ / \d+ available"));
        Assert.IsFalse(seriesPage.Contains("Moon Empire", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MovieAndSeriesCardsShowTheLocalPosterSizedAndLazyWithAFallbackAndNoRatingBadge()
    {
        var (host, _, _) = await SeedAsync();
        await using var _host = host;
        var seed = new LibraryCanonicalSeed(host.Db);
        var movie = await seed.AddWorkAsync(WorkMediaType.Movie, "Moon Empire", 2024);
        await seed.AddVideoAsync(movie, null, audio: ["ger"], durationSeconds: 7440);
        var plain = await seed.AddWorkAsync(WorkMediaType.Movie, "Plain Film", 2023);
        await seed.AddVideoAsync(plain, null, audio: ["ger"]);
        var store = new WorkMetadataStore(host.Db);
        var key = Jularr.Web.Features.Artwork.WorkArtworkCache.CacheKey(movie.Id, WorkArtworkSlot.Poster, "", "tmdb", "/p.jpg");
        var poster = new WorkArtworkCandidate(WorkArtworkSlot.Poster, "", "/p.jpg", new Uri("https://image.tmdb.org/t/p/w780/p.jpg"), 500, 750, 5, 1);
        await store.UpsertArtworkAsync(movie.Id, poster, "tmdb", key, DateTime.UtcNow, CancellationToken.None);
        await store.UpsertFactsAsync(new WorkMetadataFacts { WorkId = movie.Id, Rating = 7.7, RatingCount = 10, UpdatedAt = DateTime.UtcNow }, CancellationToken.None);

        var html = WebUtility.HtmlDecode(await host.GetHtmlAsync("/Library", asOwner: false));

        var cards = html.Split("<article class=\"lib-card", StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        var movieCard = cards.Single(x => x.Contains("Moon Empire", StringComparison.Ordinal));
        var posterAttributes = @"alt="""" width=""300"" height=""450"" loading=""lazy"" decoding=""async"" referrerpolicy=""no-referrer"" data-lib-poster data-initial=""M""";
        StringAssert.Matches(movieCard, new Regex($@"<img src=""/works/{movie.Id:D}/artwork/[0-9]+\?v=[0-9a-f]{{12}}"" {posterAttributes}"));
        Assert.IsFalse(movieCard.Contains("lib-score", StringComparison.Ordinal), "The MediaCard grammar of the Library SPEC has no rating badge.");
        Assert.IsFalse(movieCard.Contains("lib-card-initial", StringComparison.Ordinal), "A title with a poster has no placeholder.");
        Assert.IsFalse(movieCard.Contains("image.tmdb.org", StringComparison.Ordinal), "The CDN is never hot-linked.");

        var plainCard = cards.Single(x => x.Contains("Plain Film", StringComparison.Ordinal));
        StringAssert.Contains(plainCard, "lib-card-initial");
        Assert.IsFalse(plainCard.Contains("<img", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AScopeWithoutTitlesSaysSoWhileTheLibraryHasOthers()
    {
        var (host, _, _) = await SeedAsync();
        await using var _host = host;

        var html = WebUtility.HtmlDecode(await host.GetHtmlAsync("/Library?type=movie", asOwner: false));

        StringAssert.Contains(html, "Nothing in this section yet.");
        Assert.IsFalse(html.Contains("Library is empty", StringComparison.Ordinal));
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(from >= 0, $"Markup '{start}' was not rendered.");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        return to < 0 ? text[from..] : text[from..(to + end.Length)];
    }
}
