using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>#597: whole series / seasons / episodes, language preferences and the quality profile of a request.</summary>
[TestClass]
public sealed class RequestOptionsTests
{
    private const string FrierenId = "154587";

    [TestMethod]
    public void SeasonsAreParsedFromNumbersAndRanges()
    {
        Assert.IsTrue(RequestSelectionText.TryParseSeasons("1, 3-4 s6;3", out var seasons));
        CollectionAssert.AreEqual(new[] { 1, 3, 4, 6 }, seasons.ToArray());

        foreach (var invalid in new[] { "0", "two", "4-2", "1-", "100" })
        {
            Assert.IsFalse(RequestSelectionText.TryParseSeasons(invalid, out _), invalid);
        }

        Assert.IsTrue(RequestSelectionText.TryParseSeasons("", out var none));
        Assert.AreEqual(0, none.Count);
        Assert.AreEqual("1, 3-4, 6", RequestSelectionText.FormatSeasons([6, 1, 4, 3, 3]));
    }

    [TestMethod]
    public void EpisodesAreParsedFromNumbersRangesAndSeasonCodes()
    {
        Assert.IsTrue(RequestSelectionText.TryParseEpisodes("1-3, 9 S02E05 s03e01-02", defaultSeason: 1, out var episodes));

        CollectionAssert.AreEqual(
            new[]
            {
                new RequestEpisode(1, 1), new RequestEpisode(1, 2), new RequestEpisode(1, 3), new RequestEpisode(1, 9),
                new RequestEpisode(2, 5), new RequestEpisode(3, 1), new RequestEpisode(3, 2)
            },
            episodes.ToArray());
        Assert.AreEqual("S01E01-03, S01E09, S02E05, S03E01-02", RequestSelectionText.FormatEpisodes(episodes));

        Assert.IsTrue(RequestSelectionText.TryParseEpisodes("4", defaultSeason: 2, out var second));
        CollectionAssert.AreEqual(new[] { new RequestEpisode(2, 4) }, second.ToArray());

        foreach (var invalid in new[] { "x", "0", "5-2", "S00E01", "S1E", "1-99999" })
        {
            Assert.IsFalse(RequestSelectionText.TryParseEpisodes(invalid, 1, out _), invalid);
        }
    }

    [TestMethod]
    public void ValidationCleansTheSelectionToTheScopeAndNormalizesLanguages()
    {
        var seasons = new AcquisitionRequestOptions
        {
            Scope = RequestScope.Seasons,
            Seasons = [3, 1, 3],
            Episodes = [new RequestEpisode(1, 1)],
            AudioLanguage = " JPN ",
            SubtitleLanguage = "off",
            QualityProfileId = " anime-720p "
        }.Validate();

        CollectionAssert.AreEqual(new[] { 1, 3 }, seasons.Seasons.ToArray());
        Assert.AreEqual(0, seasons.Episodes.Count, "Only the selection of the chosen scope is kept.");
        Assert.AreEqual("ja", seasons.AudioLanguage);
        Assert.AreEqual("off", seasons.SubtitleLanguage);
        Assert.AreEqual("anime-720p", seasons.QualityProfileId);

        var whole = new AcquisitionRequestOptions { Seasons = [2] }.Validate();
        Assert.AreEqual(0, whole.Seasons.Count);
        Assert.IsTrue(whole.IsDefault);
        Assert.IsNull(whole.ToPayloadJson(), "Default options are not stored at all.");

        Assert.ThrowsExactly<ArgumentException>(() => new AcquisitionRequestOptions { Scope = RequestScope.Seasons }.Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new AcquisitionRequestOptions { Scope = RequestScope.Episodes }.Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new AcquisitionRequestOptions { Scope = RequestScope.Seasons, Seasons = [0] }.Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new AcquisitionRequestOptions { AudioLanguage = "klingon-fluent" }.Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new AcquisitionRequestOptions { AudioLanguage = "off" }.Validate());
    }

    [TestMethod]
    public void OptionsSurviveTheRoundTripThroughTheRequestPayload()
    {
        var options = new AcquisitionRequestOptions
        {
            Scope = RequestScope.Episodes,
            Episodes = [new RequestEpisode(1, 2), new RequestEpisode(2, 1)],
            AudioLanguage = "ja",
            SubtitleLanguage = "de",
            QualityProfileId = "anime-1080p"
        }.Validate();

        var restored = AcquisitionRequestOptions.FromPayload(options.ToPayloadJson());

        Assert.AreEqual(RequestScope.Episodes, restored.Scope);
        CollectionAssert.AreEqual(options.Episodes.ToArray(), restored.Episodes.ToArray());
        Assert.AreEqual("ja", restored.AudioLanguage);
        Assert.AreEqual("de", restored.SubtitleLanguage);
        Assert.AreEqual("anime-1080p", restored.QualityProfileId);
        Assert.IsTrue(AcquisitionRequestOptions.FromPayload(null).IsDefault);
        Assert.IsTrue(AcquisitionRequestOptions.FromPayload("not json").IsDefault);
    }

    [TestMethod]
    public void SummaryListsOnlyWhatDiffersFromTheDefaults()
    {
        var ui = UiTextBundle.English;
        Assert.AreEqual(0, RequestOptionsSummary.Describe(AcquisitionRequestOptions.Default, ui).Count);

        var lines = RequestOptionsSummary.Describe(
            new AcquisitionRequestOptions
            {
                Scope = RequestScope.Seasons,
                Seasons = [1, 2, 4],
                AudioLanguage = "ja",
                SubtitleLanguage = "off",
                QualityProfileId = "hd"
            },
            ui,
            new Dictionary<string, string> { ["hd"] = "Anime HD" });

        CollectionAssert.AreEqual(
            new[] { "Seasons 1-2, 4", "Audio: 日本語", "No subtitles", "Quality: Anime HD" },
            lines.ToArray());
        Assert.AreEqual(
            "Subtitles: Deutsch",
            RequestOptionsSummary.Describe(new AcquisitionRequestOptions { SubtitleLanguage = "de" }, ui).Single());
        Assert.AreEqual(
            "Quality: unknown-id",
            RequestOptionsSummary.Describe(new AcquisitionRequestOptions { QualityProfileId = "unknown-id" }, ui).Single());
    }

    [TestMethod]
    public async Task OptionsAreValidatedAndKeptInTheRequestPayloadForAnimeOnly()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var alice = fixture.Service("alice", AccountRole.User);

        var request = await alice.SubmitAsync(
            AnimeDraft(new AcquisitionRequestOptions
            {
                Scope = RequestScope.Seasons,
                Seasons = [2, 1],
                AudioLanguage = "jpn",
                SubtitleLanguage = "de"
            }),
            CancellationToken.None);

        Assert.AreEqual(AcquisitionRequestStatus.Pending, request.Status);
        var stored = (await fixture.Store.GetAsync(request.Id, CancellationToken.None))!.Options;
        Assert.AreEqual(RequestScope.Seasons, stored.Scope);
        CollectionAssert.AreEqual(new[] { 1, 2 }, stored.Seasons.ToArray());
        Assert.AreEqual("ja", stored.AudioLanguage);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => alice.SubmitAsync(
            AnimeDraft(new AcquisitionRequestOptions { Scope = RequestScope.Seasons }, "other"),
            CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => alice.SubmitAsync(
            new AcquisitionRequestDraft(
                MediaAcquisitionKind.Book, "test", "dune", "Dune", null, null,
                Options: new AcquisitionRequestOptions { AudioLanguage = "en" }),
            CancellationToken.None));
        Assert.AreEqual(1, (await fixture.Store.ListAsync(null, null, openOnly: false, 10, CancellationToken.None)).Count, "Refused requests leave nothing behind.");
    }

    [TestMethod]
    public async Task TvUsesTheSharedFutureAndCustomRequestOptions()
    {
        var future = new AcquisitionRequestOptions
        {
            Scope = RequestScope.FutureOnly,
            Seasons = [1],
            Episodes = [new RequestEpisode(1, 1)]
        }.Validate();
        Assert.AreEqual(RequestScope.FutureOnly, future.Scope);
        Assert.AreEqual(0, future.Seasons.Count);
        Assert.AreEqual(0, future.Episodes.Count);
        Assert.IsTrue(future.MonitorFuture);

        var custom = new AcquisitionRequestOptions
        {
            Scope = RequestScope.Custom,
            Seasons = [2],
            Episodes = [new RequestEpisode(1, 3)],
            MonitorFuture = true,
            SubtitleLanguage = "de"
        }.Validate();
        CollectionAssert.AreEqual(new[] { 2 }, custom.Seasons.ToArray());
        CollectionAssert.AreEqual(new[] { new RequestEpisode(1, 3) }, custom.Episodes.ToArray());
        Assert.IsTrue(custom.MonitorFuture);

        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var tvExecutor = new RecordingExecutor(MediaAcquisitionKind.Tv);
        var service = fixture.Service("owner", AccountRole.Owner, tvExecutor);
        var request = await service.SubmitAsync(
            new AcquisitionRequestDraft(
                MediaAcquisitionKind.Tv,
                "tmdb",
                "1396",
                "Breaking Bad",
                null,
                null,
                Options: custom),
            CancellationToken.None);

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);
        Assert.AreEqual(RequestScope.Custom, request.Options.Scope);
        CollectionAssert.AreEqual(new[] { 2 }, request.Options.Seasons.ToArray());
        CollectionAssert.AreEqual(new[] { new RequestEpisode(1, 3) }, request.Options.Episodes.ToArray());
        Assert.IsTrue(request.Options.MonitorFuture);
        Assert.AreEqual("de", request.Options.SubtitleLanguage);
        Assert.AreEqual(1, tvExecutor.Runs);
    }

    [TestMethod]
    public void EmptyCustomScopeIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new AcquisitionRequestOptions { Scope = RequestScope.Custom }.Validate());
        Assert.IsTrue(
            new AcquisitionRequestOptions { Scope = RequestScope.Custom, MonitorFuture = true }
                .Validate()
                .MonitorFuture);
    }

    [TestMethod]
    public async Task RequestersMayOnlyPickQualityProfilesTheOwnerOpenedToRequests()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var alice = fixture.Service("alice", AccountRole.User);
        var manager = fixture.Service("mia", AccountRole.MediaManager);
        var options = new AcquisitionRequestOptions { QualityProfileId = "anime-720p" };

        await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(
            () => alice.SubmitAsync(AnimeDraft(options), CancellationToken.None));

        await fixture.Settings.SetRequesterQualityProfilesAsync(["anime-720p"]);
        var allowed = await alice.SubmitAsync(AnimeDraft(options), CancellationToken.None);
        Assert.AreEqual("anime-720p", allowed.Options.QualityProfileId);

        // A media manager decides on quality profiles anyway and needs no opening.
        var byManager = await manager.SubmitAsync(
            AnimeDraft(new AcquisitionRequestOptions { QualityProfileId = "anything" }, "second"),
            CancellationToken.None);
        Assert.AreEqual("anything", byManager.Options.QualityProfileId);
    }

    [TestMethod]
    public async Task ARequestForSelectedEpisodesMonitorsOnlyThoseAndAppliesTheChosenProfile()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        environment.AniListMetadata.Add(FrierenId, "Frieren", episodeCount: 4);
        var profiles = environment.QualityProfiles;
        await profiles.UpsertAsync(AnimeQualityProfiles.CreateDefaultAnime1080p() with { Id = "anime-720p", Name = "720p" });

        var execution = await environment.ExecuteAnimeRequestAsync(
            FrierenId,
            new AcquisitionRequestOptions
            {
                Scope = RequestScope.Episodes,
                Episodes = [new RequestEpisode(1, 2), new RequestEpisode(1, 3)],
                QualityProfileId = "anime-720p"
            }.Validate().ToPayloadJson());

        Assert.AreEqual(AcquisitionRequestStatus.Completed, execution.Status, execution.Message);
        var anime = await environment.Db.Anime.AsNoTracking().SingleAsync();
        var settings = (await environment.MonitoringStateAsync()).Anime[anime.Key];
        Assert.IsTrue(settings.Monitored);
        Assert.AreEqual(true, settings.EpisodeOverrides["S01E02"]);
        Assert.AreEqual(true, settings.EpisodeOverrides["S01E03"]);
        Assert.AreEqual(false, settings.SeasonOverrides[1], "The rest of the season is switched off, so episodes that air later are not picked up unasked.");
        Assert.AreEqual("anime-720p", (await profiles.LoadAsync()).WorkAssignments[anime.Id.ToString("D")]);
        Assert.AreEqual(1, environment.Scheduler.QueuedRequests);
    }

    [TestMethod]
    public async Task ARequestForSeasonsSwitchesOffTheOtherKnownSeasons()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        environment.AniListMetadata.Add(FrierenId, "Frieren", episodeCount: 2);

        var execution = await environment.ExecuteAnimeRequestAsync(
            FrierenId,
            new AcquisitionRequestOptions { Scope = RequestScope.Seasons, Seasons = [2] }.Validate().ToPayloadJson());

        Assert.AreEqual(AcquisitionRequestStatus.Completed, execution.Status, execution.Message);
        var anime = await environment.Db.Anime.AsNoTracking().SingleAsync();
        var settings = (await environment.MonitoringStateAsync()).Anime[anime.Key];
        Assert.AreEqual(false, settings.SeasonOverrides[1]);
        Assert.AreEqual(true, settings.SeasonOverrides[2]);
    }

    [TestMethod]
    public async Task AWholeSeriesRequestAndALaterRequestForAMonitoredTitleLeaveTheExistingScopeAlone()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        environment.AniListMetadata.Add(FrierenId, "Frieren", episodeCount: 2);

        await environment.ExecuteAnimeRequestAsync(FrierenId);
        var anime = await environment.Db.Anime.AsNoTracking().SingleAsync();
        var whole = (await environment.MonitoringStateAsync()).Anime[anime.Key];
        Assert.AreEqual(0, whole.SeasonOverrides.Count);
        Assert.AreEqual(0, whole.EpisodeOverrides.Count);

        // The title is monitored as a whole; asking for one more episode only adds an "on" override.
        await environment.ExecuteAnimeRequestAsync(
            FrierenId,
            new AcquisitionRequestOptions { Scope = RequestScope.Episodes, Episodes = [new RequestEpisode(1, 2)] }.Validate().ToPayloadJson());

        var after = (await environment.MonitoringStateAsync()).Anime[anime.Key];
        Assert.AreEqual(true, after.EpisodeOverrides["S01E02"]);
        Assert.AreEqual(0, after.SeasonOverrides.Count, "Nothing already monitored is switched off by a later request.");
    }

    private static AcquisitionRequestDraft AnimeDraft(AcquisitionRequestOptions options, string id = FrierenId) =>
        new(MediaAcquisitionKind.Anime, AniListMetadataProvider.ProviderKey, id, "Frieren", null, null, Options: options);
}
