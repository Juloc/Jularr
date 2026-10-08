using System.Text.Json;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;

namespace Jularr.Tests;

/// <summary>
/// The media-type-agnostic monitoring engine: whole-item (movie/audiobook) and season (series)
/// granularity beyond the anime episode granularity covered by <see cref="AnimeMonitoringTests"/>,
/// plus per-kind store isolation, registry granularity and legacy-state (no granularity field)
/// parity.
/// </summary>
[TestClass]
public sealed class GenericMonitoringTests
{
    private static readonly QualityProfile MovieProfile = VideoQualityProfiles.CreateDefaultMovie1080p();
    private static readonly QualityProfile TvProfile = VideoQualityProfiles.CreateDefaultTv1080p();

    // ---- Whole-item granularity (movies, audiobooks, books) ------------------------------------

    [TestMethod]
    public void MissingWholeItemBecomesWantedAndAcceptedReleaseAutoGrabs()
    {
        var now = DateTimeOffset.Parse("2026-09-25T18:00:00Z");
        var key = MonitoredUnitKey.ForItem("movie");
        var state = State(new MonitorSettings("movie", SearchOnAdd: true));

        var wanted = MonitoringEngine.GetWanted(
            _ => true,
            new[] { new MonitoredUnitInventory(key, null, HasFile: false, null) },
            MovieProfile,
            now);

        Assert.AreEqual(1, wanted.Count);
        Assert.AreEqual(WantedReason.Missing, wanted[0].Reason);
        Assert.AreEqual(MonitoringGranularity.Item, wanted[0].Key.Granularity);

        // A whole-item unit has no season/episode numbering to match: any accepted release grabs.
        var candidate = Score(MovieProfile, "The Movie 2021 1080p WEB-DL x264-GRP", 8_000_000_000);
        var decision = MonitoringEngine.EvaluateCandidate(MovieProfile, wanted[0], candidate, null, state);

        Assert.IsTrue(decision.Grab);
    }

    // ---- Season granularity (series season packs) ----------------------------------------------

    [TestMethod]
    public void SeasonPackGrabsButSingleEpisodeAndWrongSeasonDoNot()
    {
        var now = DateTimeOffset.UtcNow;
        var key = MonitoredUnitKey.ForSeason("series", 2);
        var state = State(new MonitorSettings("series", SearchOnAdd: true));
        var wanted = new WantedUnit(key, WantedReason.Missing, now);

        var seasonPack = Score(TvProfile, "Series Title S02 1080p WEB-DL x264-GRP", 20_000_000_000);
        Assert.IsTrue(MonitoringEngine.EvaluateCandidate(TvProfile, wanted, seasonPack, null, state).Grab);

        var singleEpisode = Score(TvProfile, "Series Title S02E01 1080p WEB-DL x264-GRP", 2_000_000_000);
        Assert.IsFalse(MonitoringEngine.EvaluateCandidate(TvProfile, wanted, singleEpisode, null, state).Grab);

        var wrongSeasonPack = Score(TvProfile, "Series Title S03 1080p WEB-DL x264-GRP", 20_000_000_000);
        Assert.IsFalse(MonitoringEngine.EvaluateCandidate(TvProfile, wanted, wrongSeasonPack, null, state).Grab);
    }

    // ---- Registry: each media type declares its monitoring granularity -------------------------

    [TestMethod]
    public void RegistryReportsMonitoringGranularityPerKind()
    {
        var registry = new MediaAcquisitionRegistry(new IMediaAcquisitionRegistration[]
        {
            new AnimeAcquisitionRegistration(),
            new MovieAcquisitionRegistration(),
            new TvAcquisitionRegistration(),
            new AudiobookAcquisitionRegistration(),
            new BookAcquisitionRegistration()
        });

        Assert.AreEqual(MonitoringGranularity.Episode, registry.MonitoringGranularityFor(MediaAcquisitionKind.Anime));
        Assert.AreEqual(MonitoringGranularity.Episode, registry.MonitoringGranularityFor(MediaAcquisitionKind.Tv));
        Assert.AreEqual(MonitoringGranularity.Item, registry.MonitoringGranularityFor(MediaAcquisitionKind.Movie));
        Assert.AreEqual(MonitoringGranularity.Item, registry.MonitoringGranularityFor(MediaAcquisitionKind.Audiobook));
        Assert.AreEqual(MonitoringGranularity.Item, registry.MonitoringGranularityFor(MediaAcquisitionKind.Book));
    }

    // ---- Parity: legacy state written before granularity existed loads as episode granularity ---

    [TestMethod]
    public async Task LegacyStateWithoutGranularityLoadsAsEpisodeGranularity()
    {
        var root = Path.Combine(Path.GetTempPath(), "jularr-monitoring-legacy-" + Guid.NewGuid());
        var directory = Path.Combine(root, "acquisition");
        Directory.CreateDirectory(directory);
        try
        {
            // The exact on-disk shape from before monitoring was generalized: no "granularity" field
            // anywhere. It must load with the anime episode behaviour unchanged.
            const string legacy = """
                {
                  "version": 1,
                  "anime": {
                    "anime": {
                      "animeKey": "anime",
                      "monitored": true,
                      "searchOnAdd": true,
                      "seasonOverrides": {},
                      "episodeOverrides": {}
                    }
                  },
                  "wanted": {},
                  "attempts": {
                    "anime:S01E01": {
                      "key": {
                        "animeKey": "anime",
                        "seasonNumber": 1,
                        "episodeNumber": 1,
                        "absoluteEpisodeNumber": null
                      },
                      "status": 3,
                      "releaseKey": "release",
                      "failureCount": 1,
                      "lastAttemptAtUtc": "2026-01-01T00:00:00+00:00",
                      "nextRetryAtUtc": "2026-01-01T00:05:00+00:00"
                    }
                  },
                  "history": [],
                  "schedule": { "enabled": true, "intervalMinutes": 30 }
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(directory, "monitoring.json"), legacy);

            var state = await new MonitoringStore(root).LoadAsync();

            var attempt = state.Attempts["anime:S01E01"];
            Assert.AreEqual(MonitoringGranularity.Episode, attempt.Key.Granularity);
            Assert.AreEqual(AcquisitionAttemptStatus.Failed, attempt.Status);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ReleaseScoreResult Score(QualityProfile profile, string title, long size) =>
        ReleaseScorer.Score(profile, new ReleaseCandidate(ReleaseParser.Parse(title), size));

    private static MonitoringState State(MonitorSettings settings)
    {
        var state = MonitoringState.Empty();
        state.Anime[settings.AnimeKey] = settings;
        return state;
    }
}
