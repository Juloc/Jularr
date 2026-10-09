using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Providers;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>A Series that is still airing keeps its seasons and episodes current: a new episode reaches the library structure without anybody opening the show.</summary>
[TestClass]
public sealed class SeriesStructureRefreshTests
{
    private static string SeriesJson(string status, int specials, int seasonOne) =>
        $$"""
        {
          "id": 1396, "name": "Harbor", "original_name": "Harbor", "original_language": "en", "overview": "A harbor town.",
          "first_air_date": "2020-01-10", "status": "{{status}}", "genres": [],
          "seasons": [
            { "season_number": 0, "name": "Specials", "episode_count": {{specials}} },
            { "season_number": 1, "name": "Season 1", "episode_count": {{seasonOne}} } ]
        }
        """;

    private static string SeasonJson(params (int Number, string Title, string AirDate)[] episodes) =>
        "{ \"episodes\": [" + string.Join(',', episodes.Select(episode => $$"""{ "episode_number": {{episode.Number}}, "name": "{{episode.Title}}", "air_date": "{{episode.AirDate}}" }""")) + "] }";

    private static HttpResponseMessage Answer(string path, string status, int seasonOne, int[] seasonOneEpisodes) => WorkMetadataFixture.Json(path switch
    {
        "/3/tv/1396" => SeriesJson(status, 1, seasonOne),
        "/3/tv/1396/season/0" => SeasonJson((1, "Behind the scenes", "2020-01-01")),
        "/3/tv/1396/season/1" => SeasonJson([.. seasonOneEpisodes.Select(number => (number, $"Episode {number}", $"2020-01-{10 + number}"))]),
        _ => "{}"
    });

    [TestMethod]
    public async Task ANewEpisodeOfAnAiringSeriesAppearsOnTheNextSpoolPassAndOnlyChangedSeasonsAreFetched()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var series = await new WorkService(fixture.Db).EnsureWorkByExternalIdentityAsync(WorkMediaType.Series, ProviderKeys.Tmdb, "1396", "Harbor", 2020, CancellationToken.None);
        var seasonOne = new[] { 1, 2 };
        fixture.Tmdb = request => Answer(request.RequestUri!.AbsolutePath, "Returning Series", seasonOne.Length, seasonOne);
        await fixture.Queue.RequestMetadataRefreshAsync(series.Id, interactive: false, CancellationToken.None);

        await fixture.RunSpoolAsync();

        Assert.AreEqual(3, await fixture.Db.WorkEpisodes.CountAsync(episode => episode.WorkId == series.Id), "Season 1 and the specials are materialized.");
        Assert.IsTrue(await fixture.Db.WorkEpisodes.AnyAsync(episode => episode.WorkId == series.Id && episode.SeasonNumber == 0 && episode.IsSpecial));
        var entry = await fixture.RefreshEntryAsync(series.Id);
        Assert.AreEqual(fixture.Clock.GetUtcNow().UtcDateTime.Add(WorkMetadataRefresher.AiringRefreshInterval), entry.NextAttemptAt, "An airing series is looked at again within the day.");

        seasonOne = [1, 2, 3];
        fixture.TmdbRequests.Clear();
        fixture.Clock.Advance(WorkMetadataRefresher.AiringRefreshInterval + TimeSpan.FromMinutes(1));
        await fixture.RunSpoolAsync();

        Assert.IsTrue(await fixture.Db.WorkEpisodes.AnyAsync(episode => episode.WorkId == series.Id && episode.SeasonNumber == 1 && episode.EpisodeNumber == 3), "The announced third episode is now part of the structure.");
        Assert.AreEqual(4, await fixture.Db.WorkEpisodes.CountAsync(episode => episode.WorkId == series.Id));
        Assert.IsFalse(fixture.TmdbRequests.Any(path => path.Contains("/season/0", StringComparison.Ordinal)), "A season whose episode count did not change is not fetched again.");
    }

    [TestMethod]
    public async Task AnEndedSeriesIsNotLookedAtAgainBeforeTheStaleInterval()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var series = await new WorkService(fixture.Db).EnsureWorkByExternalIdentityAsync(WorkMediaType.Series, ProviderKeys.Tmdb, "1396", "Harbor", 2020, CancellationToken.None);
        fixture.Tmdb = request => Answer(request.RequestUri!.AbsolutePath, "Ended", 2, [1, 2]);
        await fixture.Queue.RequestMetadataRefreshAsync(series.Id, interactive: false, CancellationToken.None);

        await fixture.RunSpoolAsync();

        var entry = await fixture.RefreshEntryAsync(series.Id);
        Assert.AreEqual(fixture.Clock.GetUtcNow().UtcDateTime.Add(WorkMetadataRefresher.StaleAfter), entry.NextAttemptAt);
    }
}
