namespace Jularr.Tests;

[TestClass]
public sealed class AnimeMonitoringTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "jularr-monitoring-" + Guid.NewGuid());

    [TestMethod]
    public async Task SettingsSurviveARestartAndFollowASeriesFolderRename()
    {
        var root = NewRoot();
        try
        {
            var store = new AnimeMonitoringStore(root);
            await store.UpdateAsync(state => state with { Anime = new Dictionary<string, AnimeMonitorSettings>(StringComparer.OrdinalIgnoreCase) { ["anime"] = new("anime", true, [3, 5], Guid.Parse("00000000-0000-0000-0000-0000000000a0")) } });

            var reloaded = await new AnimeMonitoringStore(root).LoadAsync();
            Assert.IsTrue(reloaded.Anime["anime"].SearchOnAdd);
            CollectionAssert.AreEqual(new[] { 3, 5 }, reloaded.Anime["anime"].IndexerIds);

            Assert.IsTrue(await store.RekeyAnimeAsync("anime", "anime (2023)"));
            var renamed = await store.LoadAsync();
            Assert.IsFalse(renamed.Anime.ContainsKey("anime"));
            Assert.AreEqual("anime (2023)", renamed.Anime["anime (2023)"].AnimeKey);
            Assert.IsFalse(await store.RekeyAnimeAsync("unknown", "other"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task AnOlderFileWithAttemptsWantedEpisodesHistoryAndScheduleStillLoadsItsSettings()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "acquisition"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "acquisition", "monitoring.json"),
                """
                {
                  "version": 1,
                  "anime": { "anime": { "animeKey": "anime", "searchOnAdd": true } },
                  "wanted": { "anime:S01E01": { "key": { "animeKey": "anime", "seasonNumber": 1, "episodeNumber": 1 }, "reason": 0, "becameWantedAtUtc": "2026-01-01T00:00:00+00:00" } },
                  "attempts": { "anime:S01E01": { "key": { "animeKey": "anime", "seasonNumber": 1, "episodeNumber": 1 }, "status": 3, "failureCount": 1 } },
                  "history": [],
                  "schedule": { "enabled": true, "intervalMinutes": 30 }
                }
                """);

            var state = await new AnimeMonitoringStore(root).LoadAsync();

            Assert.IsTrue(state.Anime["anime"].SearchOnAdd);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
