using System.Net;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Metadata;

namespace Jularr.Tests;

/// <summary>
/// The Anime/Series detail page rendered end to end (docs/mockups/anime-series-detail/SPEC.md): the hero
/// carries no second poster, seasons/episodes/related sit in three regions, episode cards show runtime,
/// languages and availability, and owner-only tools stay out of the consumer markup.
/// </summary>
[TestClass]
public sealed class AnimeDetailPageRenderTests
{
    private const string Cover = "https://img.example/cover.jpg";
    private const string Banner = "https://img.example/banner.jpg";

    private static async Task<(ManageSheetPageTestHost Host, Anime Anime, Episode First)> SeedAsync(bool withBanner = true)
    {
        var host = await ManageSheetPageTestHost.CreateAsync();
        var anime = await host.AddAnimeAsync("Starfall Chronicle");
        host.Db.Add(new AnimeMetadata
        {
            AnimeId = anime.Id,
            Provider = "anilist",
            ExternalId = "9001",
            PreferredTitle = "Starfall Chronicle",
            NativeTitle = "星降りの記",
            Description = "A young archivist inherits a failing observatory.",
            CoverImageUrl = Cover,
            BannerImageUrl = withBanner ? Banner : null,
            Format = "TV",
            Status = "FINISHED",
            SeasonYear = 2017,
            EpisodeCount = 3,
            EpisodeDurationMinutes = 24,
            AverageScore = 82
        });
        await host.Db.SaveChangesAsync();

        var first = await host.AddEpisodeAsync(anime, season: 1, number: 1);
        await host.AddEpisodeAsync(anime, season: 1, number: 2);
        await host.AddEpisodeAsync(anime, season: 0, number: 1);

        var root = new LibraryRoot { Name = "Media", Path = $"/media/{Guid.NewGuid():N}" };
        host.Db.Add(root);
        var file = new MediaFile { LibraryRootId = root.Id, EpisodeId = first.Id, Path = $"{root.Path}/e1.mkv", SizeBytes = 1 };
        host.Db.Add(file);
        host.Db.Add(new MediaAnalysis
        {
            MediaFileId = file.Id,
            Status = MediaAnalysisStatus.Succeeded,
            DurationSeconds = 1445,
            SourceLastWriteTimeUtc = DateTime.UtcNow
        });
        host.Db.Add(new MediaAnalysisStream { MediaFileId = file.Id, StreamIndex = 1, Kind = MediaStreamKind.Audio, Language = "jpn" });
        host.Db.Add(new MediaAnalysisStream { MediaFileId = file.Id, StreamIndex = 2, Kind = MediaStreamKind.Subtitle, Language = "eng" });
        await host.Db.SaveChangesAsync();
        return (host, anime, first);
    }

    [TestMethod]
    public async Task HeroHasOneBackdropNoSecondPosterAndItsFactsInsideIt()
    {
        var (host, anime, _) = await SeedAsync();
        await using var _host = host;

        var html = WebUtility.HtmlDecode(await host.GetHtmlAsync($"/Library/Anime/{anime.Id}", asOwner: false));

        StringAssert.Contains(html, $"src=\"{Banner}\"");
        Assert.AreEqual(0, CountOf(html, "anime-detail-poster"), "The hero must not show a second poster card.");
        // Cover is only ever a season thumbnail/fallback, never a hero poster element.
        var hero = Between(html, "<section class=\"ad-hero", "</section>");
        Assert.IsFalse(hero.Contains(Cover, StringComparison.Ordinal), "With a backdrop the hero uses only the backdrop.");
        StringAssert.Contains(hero, "星降りの記");
        StringAssert.Contains(hero, "ad-hero-strip");
        StringAssert.Contains(hero, "8.2");
        StringAssert.Contains(hero, "24 min");
        StringAssert.Contains(hero, "2017");
        StringAssert.Contains(hero, "Specials");
        StringAssert.Contains(hero, "Start watching");
    }

    [TestMethod]
    public async Task ManagerOnlyAnimeDetailLinksNowhereIntoThePlayer()
    {
        var (host, anime, _) = await SeedAsync();
        await using var _host = host;
        var playing = WebUtility.HtmlDecode(await host.GetHtmlAsync($"/Library/Anime/{anime.Id}", asOwner: false));
        StringAssert.Contains(playing, "/Library/Episode/");

        await host.Modules.SetAsync(Jularr.Web.Features.Instance.InstanceModule.Playback, false);
        var html = WebUtility.HtmlDecode(await host.GetHtmlAsync($"/Library/Anime/{anime.Id}", asOwner: false));

        Assert.IsFalse(html.Contains("/Library/Episode/", StringComparison.Ordinal), "Episode cards and the hero must not link into the player.");
        Assert.IsFalse(Between(html, "<section class=\"ad-hero", "</section>").Contains("Start watching", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task WithoutABackdropTheCoverBecomesABlurredBackgroundNotAPoster()
    {
        var (host, anime, _) = await SeedAsync(withBanner: false);
        await using var _host = host;

        var html = WebUtility.HtmlDecode(await host.GetHtmlAsync($"/Library/Anime/{anime.Id}", asOwner: false));

        StringAssert.Contains(html, "ad-hero ad-hero-derived");
        var hero = Between(html, "<section class=\"ad-hero", "</section>");
        Assert.AreEqual(1, CountOf(hero, Cover), "The cover appears once, as the blurred background.");
    }

    [TestMethod]
    public async Task SeasonRailSelectsTheSeasonAndShowsItsEpisodesWithRuntimeLanguagesAndAvailability()
    {
        var (host, anime, first) = await SeedAsync();
        await using var _host = host;

        var html = WebUtility.HtmlDecode(await host.GetHtmlAsync($"/Library/Anime/{anime.Id}", asOwner: false));

        var rail = Between(html, "<nav class=\"ad-seasons\"", "</nav>");
        StringAssert.Contains(rail, "Season 1");
        StringAssert.Contains(rail, "2 episodes");
        StringAssert.Contains(rail, "Specials");
        StringAssert.Contains(rail, "1 episode");
        StringAssert.Contains(rail, "ad-season is-selected");
        StringAssert.Contains(html, "part=s0");

        var episodes = Between(html, "<section class=\"ad-episodes\"", "</section>");
        StringAssert.Contains(episodes, "S01 E01");
        StringAssert.Contains(episodes, "S01 E02");
        Assert.IsFalse(episodes.Contains("S00 E01", StringComparison.Ordinal), "Only the selected season is listed.");
        StringAssert.Contains(episodes, $"/Library/Episode/{first.Id}");
        StringAssert.Contains(episodes, "24 min");
        StringAssert.Contains(episodes, ">JA<");
        StringAssert.Contains(episodes, ">EN<");
        StringAssert.Contains(episodes, "ad-state-available");
        StringAssert.Contains(episodes, "ad-state-unavailable");
        StringAssert.Contains(episodes, "Not available");
    }

    [TestMethod]
    public async Task ChoosingTheSpecialsShowsThemAndListLayoutAndSortAreHonoured()
    {
        var (host, anime, _) = await SeedAsync();
        await using var _host = host;

        var specials = WebUtility.HtmlDecode(await host.GetHtmlAsync($"/Library/Anime/{anime.Id}?part=s0", asOwner: false));
        var episodes = Between(specials, "<section class=\"ad-episodes\"", "</section>");
        StringAssert.Contains(episodes, "S00 E01");
        Assert.IsFalse(episodes.Contains("S01 E01", StringComparison.Ordinal));

        var desc = WebUtility.HtmlDecode(await host.GetHtmlAsync($"/Library/Anime/{anime.Id}?part=s1&sort=desc&view=list", asOwner: false));
        var list = Between(desc, "<section class=\"ad-episodes\"", "</section>");
        StringAssert.Contains(list, "ad-episode-list is-list");
        Assert.IsTrue(
            list.IndexOf("S01 E02", StringComparison.Ordinal) < list.IndexOf("S01 E01", StringComparison.Ordinal),
            "Newest first puts episode 2 before episode 1.");
    }

    [TestMethod]
    public async Task ConsumerPageHasNoReleaseOrFileInternalsAndNoOwnerToolsButOwnersGetTheManageSheet()
    {
        var (host, anime, _) = await SeedAsync();
        await using var _host = host;

        var user = WebUtility.HtmlDecode(await host.GetHtmlAsync($"/Library/Anime/{anime.Id}", asOwner: false));
        Assert.IsFalse(user.Contains("data-manage-sheet-open", StringComparison.Ordinal));
        Assert.IsFalse(user.Contains(".mkv", StringComparison.Ordinal), "File paths never appear on the consumer page.");
        Assert.IsFalse(user.Contains("release group", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(user.Contains("Map local episodes", StringComparison.Ordinal));

        var owner = WebUtility.HtmlDecode(await host.GetHtmlAsync($"/Library/Anime/{anime.Id}", asOwner: true));
        StringAssert.Contains(owner, "data-manage-sheet-open=\"anime-manage\"");
        StringAssert.Contains(Between(owner, "<section class=\"ad-hero", "</section>"), "anime-manage");
    }

    [TestMethod]
    public async Task AMatchedAnimeOffersTheWatchlistAndTheOverflowMenu()
    {
        var (host, anime, _) = await SeedAsync();
        await using var _host = host;

        var html = WebUtility.HtmlDecode(await host.GetHtmlAsync($"/Library/Anime/{anime.Id}", asOwner: false));

        StringAssert.Contains(html, "handler=Follow");
        StringAssert.Contains(html, "Add to watchlist");
        StringAssert.Contains(html, "More actions");
        StringAssert.Contains(html, "Language tools & Learning");
    }

    [TestMethod]
    public async Task AnAnimeWithoutEpisodesShowsAnEmptyStateInsteadOfARail()
    {
        await using var host = await ManageSheetPageTestHost.CreateAsync();
        var anime = await host.AddAnimeAsync("Empty Show");

        var html = WebUtility.HtmlDecode(await host.GetHtmlAsync($"/Library/Anime/{anime.Id}", asOwner: false));

        StringAssert.Contains(html, "No episodes in the library yet.");
        Assert.IsFalse(html.Contains("<nav class=\"ad-seasons\"", StringComparison.Ordinal));
        StringAssert.Contains(html, "ad-main no-rail");
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(from >= 0, $"Markup '{start}' was not rendered.");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        return to < 0 ? text[from..] : text[from..(to + end.Length)];
    }
}
