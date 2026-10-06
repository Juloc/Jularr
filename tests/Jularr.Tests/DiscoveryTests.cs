using System.Text.Json.Nodes;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Tracking;

namespace Jularr.Tests;

[TestClass]
public sealed class DiscoveryTests
{
    [TestMethod]
    public void SearchInputIsNormalizedAndForcesSearchMode()
    {
        var request = DiscoveryRequest.Parse(
            "  Mushoku   Tensei  ",
            "light-novel",
            "top");

        Assert.AreEqual("Mushoku Tensei", request.Query);
        Assert.AreEqual(DiscoveryCategory.LightNovel, request.Category);
        Assert.AreEqual(DiscoveryMode.Search, request.Mode);
    }

    [TestMethod]
    public void PublicDiscoveryDoesNotRequirePersonalAniListAccount()
    {
        var trending = DiscoveryRequest.Parse(null, "anime", "trending");
        var top = DiscoveryRequest.Parse(null, "manga", "top");
        var search = DiscoveryRequest.Parse("Frieren", "anime", "my-list");

        Assert.IsFalse(trending.RequiresPersonalAniListAccount);
        Assert.IsFalse(top.RequiresPersonalAniListAccount);
        Assert.IsFalse(search.RequiresPersonalAniListAccount);
        Assert.AreEqual(DiscoveryMode.Search, search.Mode);
    }

    [TestMethod]
    public void MyAniListRequiresPersonalAniListAccount()
    {
        var request = DiscoveryRequest.Parse(null, "all", "my-list");

        Assert.IsTrue(request.RequiresPersonalAniListAccount);
    }

    [TestMethod]
    public void GenreIsTrimmedAndTitleCasedForAniListGenreIn()
    {
        var request = DiscoveryRequest.Parse(null, "anime", "trending", "  sci-fi  ");

        Assert.AreEqual("Sci-Fi", request.Genre);
    }

    [TestMethod]
    public void MissingOrBlankGenreNormalizesToEmpty()
    {
        Assert.AreEqual("", DiscoveryRequest.Parse(null, "anime", "trending").Genre);
        Assert.AreEqual("", DiscoveryRequest.Parse(null, "anime", "trending", "   ").Genre);
        Assert.AreEqual("", DiscoveryRequest.Parse(null, "anime", "trending", null).Genre);
    }

    [TestMethod]
    public void AniListReadingDiscoverySeparatesNovelsAndManga()
    {
        const string json = """
        {
          "data": {
            "Page": {
              "media": [
                {
                  "id": 1,
                  "title": { "english": "Novel A", "romaji": "Novel A", "native": "小説A" },
                  "description": "Novel",
                  "coverImage": { "large": "https://example.invalid/novel.jpg" },
                  "bannerImage": null,
                  "format": "NOVEL",
                  "status": "FINISHED",
                  "chapters": 12,
                  "volumes": 2,
                  "startDate": { "year": 2020 },
                  "genres": ["Fantasy"],
                  "isAdult": false
                },
                {
                  "id": 2,
                  "title": { "english": "Manga B", "romaji": "Manga B", "native": "漫画B" },
                  "description": "Manga",
                  "coverImage": { "large": "https://example.invalid/manga.jpg" },
                  "bannerImage": null,
                  "format": "MANGA",
                  "status": "RELEASING",
                  "chapters": 50,
                  "volumes": 6,
                  "startDate": { "year": 2021 },
                  "genres": ["Action"],
                  "isAdult": false
                }
              ]
            }
          }
        }
        """;

        var novels = NovelAniListProvider.ParseReadingMediaResponse(
            json,
            includeNovels: true,
            includeManga: false);
        var manga = NovelAniListProvider.ParseReadingMediaResponse(
            json,
            includeNovels: false,
            includeManga: true);

        Assert.AreEqual(1, novels.Count);
        Assert.IsTrue(novels[0].IsNovel);
        Assert.AreEqual("Novel A", novels[0].PreferredTitle);

        Assert.AreEqual(1, manga.Count);
        Assert.IsFalse(manga[0].IsNovel);
        Assert.AreEqual("Manga B", manga[0].PreferredTitle);
    }

    [TestMethod]
    public void AiringAnimeWithNullCountsStillParses()
    {
        // AniList sends null (not a missing field) for unknown episode counts, years and durations
        // of airing shows; trending lists are mostly airing shows.
        const string json = """
        {
          "data": {
            "Page": {
              "media": [
                {
                  "id": 7,
                  "title": { "english": null, "romaji": "Airing Show", "native": null },
                  "description": null,
                  "coverImage": { "extraLarge": null, "large": "https://example.invalid/a.jpg" },
                  "bannerImage": null,
                  "format": "TV",
                  "status": "RELEASING",
                  "season": null,
                  "seasonYear": null,
                  "episodes": null,
                  "duration": null,
                  "isAdult": false
                }
              ]
            }
          }
        }
        """;

        var rows = AniListMetadataProvider.ParseSearchResponse(json);

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("Airing Show", rows[0].PreferredTitle);
        Assert.IsNull(rows[0].EpisodeCount);
        Assert.IsNull(rows[0].SeasonYear);
    }

    [TestMethod]
    public void TheBannerAndOnlyAYouTubeTrailerOfAnAnimeReachThePreview()
    {
        const string json = """
        {
          "data": {
            "Page": {
              "media": [
                { "id": 1, "title": { "romaji": "Has Both" }, "bannerImage": "https://s4.anilist.co/banner/1.jpg", "trailer": { "id": "dQw4w9WgXcQ", "site": "youtube" } },
                { "id": 2, "title": { "romaji": "Other Site" }, "bannerImage": null, "trailer": { "id": "x9", "site": "dailymotion" } },
                { "id": 3, "title": { "romaji": "No Trailer" }, "trailer": null }
              ]
            }
          }
        }
        """;

        var rows = AniListMetadataProvider.ParseSearchResponse(json);

        Assert.AreEqual("https://s4.anilist.co/banner/1.jpg", rows[0].BannerImageUrl);
        Assert.AreEqual("dQw4w9WgXcQ", rows[0].TrailerKey);
        Assert.IsNull(rows[1].TrailerKey, "Only a YouTube trailer can be embedded.");
        Assert.IsNull(rows[2].TrailerKey);
    }

    [TestMethod]
    public void OngoingReadingMediaWithNullCountsStillParses()
    {
        const string json = """
        {
          "data": {
            "Page": {
              "media": [
                {
                  "id": 3,
                  "title": { "english": null, "romaji": "Ongoing Novel", "native": null },
                  "description": null,
                  "coverImage": { "large": null },
                  "bannerImage": null,
                  "format": "NOVEL",
                  "status": "RELEASING",
                  "chapters": null,
                  "volumes": null,
                  "startDate": { "year": null },
                  "genres": [],
                  "isAdult": false
                }
              ]
            }
          }
        }
        """;

        var rows = NovelAniListProvider.ParseReadingMediaResponse(json, includeNovels: true, includeManga: false);

        Assert.AreEqual(1, rows.Count);
        Assert.IsNull(rows[0].ChapterCount);
        Assert.IsNull(rows[0].VolumeCount);
    }

    [TestMethod]
    public void ParsesPersonalAniListLibraryAcrossAnimeAndNovels()
    {
        const string json = """
        {
          "data": {
            "MediaListCollection": {
              "lists": [
                {
                  "status": "CURRENT",
                  "entries": [
                    {
                      "id": 10,
                      "status": "CURRENT",
                      "progress": 5,
                      "repeat": 0,
                      "updatedAt": 200,
                      "media": {
                        "id": 100,
                        "type": "MANGA",
                        "format": "NOVEL",
                        "title": {
                          "english": "Light Novel",
                          "romaji": "Light Novel",
                          "native": "ライトノベル"
                        },
                        "coverImage": { "large": "https://example.invalid/ln.jpg" },
                        "status": "RELEASING",
                        "episodes": null,
                        "chapters": 20,
                        "volumes": 4,
                        "seasonYear": null,
                        "startDate": { "year": 2024 },
                        "genres": ["Fantasy"],
                        "isAdult": false
                      }
                    }
                  ]
                }
              ]
            }
          }
        }
        """;

        var items = AniListAccountService.ParseLibraryResponse(json);

        Assert.AreEqual(1, items.Count);
        Assert.IsTrue(items[0].IsNovel);
        Assert.AreEqual(5, items[0].Progress);
        Assert.AreEqual(20, items[0].TotalProgress);
        Assert.AreEqual("CURRENT", items[0].ListStatus);
        Assert.AreEqual(2024, items[0].Year);
    }

    [TestMethod]
    public void NovelProgressSyncNeverMovesBackward()
    {
        var preview = AniListAccountService.EvaluateRemoteChapterProgressSafety(
            Remote(progress: 8, status: "CURRENT"),
            requestedProgress: 7,
            aniListChapterCount: 20,
            mediaTitle: "Novel");

        Assert.IsFalse(preview.CanSync);
        Assert.IsTrue(preview.IsNoOp);
        Assert.AreEqual(8, preview.RemoteProgress);
    }

    [TestMethod]
    public void NovelProgressSyncBlocksUnknownAniListChapterCount()
    {
        var preview = AniListAccountService.EvaluateRemoteChapterProgressSafety(
            Remote(progress: 3, status: "CURRENT"),
            requestedProgress: 4,
            aniListChapterCount: null,
            mediaTitle: "Novel");

        Assert.IsFalse(preview.CanSync);
        Assert.IsFalse(preview.IsNoOp);
        StringAssert.Contains(preview.Message, "chapter count");
    }

    [TestMethod]
    public void NovelProgressSyncRequiresCurrentAndAvoidsFinalChapter()
    {
        var paused = AniListAccountService.EvaluateRemoteChapterProgressSafety(
            Remote(progress: 3, status: "PAUSED"),
            requestedProgress: 4,
            aniListChapterCount: 20,
            mediaTitle: "Novel");

        Assert.IsFalse(paused.CanSync);
        Assert.IsFalse(paused.IsNoOp);

        var final = AniListAccountService.EvaluateRemoteChapterProgressSafety(
            Remote(progress: 19, status: "CURRENT"),
            requestedProgress: 20,
            aniListChapterCount: 20,
            mediaTitle: "Novel");

        Assert.IsFalse(final.CanSync);
        Assert.IsFalse(final.IsNoOp);
        StringAssert.Contains(final.Message, "final");
    }

    [TestMethod]
    public void DiscoveryClientUsesDebounceAndCancelsStaleRequests()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Jularr.Web",
            "wwwroot",
            "js",
            "discover.js"));

        StringAssert.Contains(script, "const SEARCH_DELAY = 250");
        StringAssert.Contains(script, "AbortController");
        StringAssert.Contains(script, "requestVersion");
        StringAssert.Contains(script, "history.replaceState");
    }

    [TestMethod]
    public void NewModeStringsParseToNewDiscoveryMode()
    {
        // #371: "New" (recently published) is a real Books-only signal; other spellings the
        // client/URL might send must resolve to the same mode.
        Assert.AreEqual(DiscoveryMode.New, DiscoveryRequest.Parse(null, "book", "new").Mode);
        Assert.AreEqual(DiscoveryMode.New, DiscoveryRequest.Parse(null, "book", "recent").Mode);
        Assert.AreEqual(DiscoveryMode.New, DiscoveryRequest.Parse(null, "book", "recently-published").Mode);
    }

    [TestMethod]
    public void DiscoveryClientOffersNewAndUpcomingOnlyWhereProvidersSupportThem()
    {
        // Books have a true recent-publication feed. TMDB exposes new/upcoming Movie/TV feeds.
        // AniList-backed categories still stay on Trending/Top here.
        // outside the Book category instead of silently aliasing to Top under a wrong label.
        var page = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Jularr.Web",
            "Pages",
            "Discover",
            "Index.cshtml"));

        StringAssert.Contains(page, "DiscoveryCategory.Book or DiscoveryCategory.Movie or DiscoveryCategory.Series");
        StringAssert.Contains(page, "DiscoveryMode.New, \"discover.tabs.new\"");
        StringAssert.Contains(page, "DiscoveryMode.Upcoming, \"discover.tabs.upcoming\"");
        Assert.AreEqual(
            DiscoveryMode.Trending,
            DiscoverBrowseQuery.Parse(key => key == "mode" ? "new" : key == "category" ? "anime" : null).Mode);
        Assert.AreEqual(
            DiscoveryMode.Upcoming,
            DiscoverBrowseQuery.Parse(key => key == "mode" ? "upcoming" : key == "category" ? "movie" : null).Mode);
    }

    private static AniListRemoteListEntry Remote(
        int progress,
        string status) =>
        new(
            Id: 123,
            UserId: 42,
            MediaId: 999,
            Status: status,
            Progress: progress,
            Score: 7,
            Repeat: 0,
            Priority: 0,
            Private: false,
            Notes: null,
            HiddenFromStatusLists: false,
            CustomLists: JsonNode.Parse("""{"Favorites":true}"""),
            AdvancedScores: null,
            StartedAt: null,
            CompletedAt: null,
            UpdatedAt: 123456789);

    private static string FindRepositoryRoot()
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

        throw new DirectoryNotFoundException(
            "Could not locate Jularr repository root.");
    }
}
