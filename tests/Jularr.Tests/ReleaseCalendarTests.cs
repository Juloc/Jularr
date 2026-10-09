using Jularr.Web.Features.Monitoring;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Calendar;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Metadata;

namespace Jularr.Tests;

[TestClass]
public sealed class ReleaseCalendarTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    // ---- Date precision ----

    [TestMethod]
    public void FuzzyDatePartsKeepOnlyTheKnownPrecision()
    {
        Assert.AreEqual(ReleaseDatePrecision.Unknown, ReleaseDate.FromParts(null, 10, 5).Precision);
        Assert.AreEqual(ReleaseDatePrecision.Year, ReleaseDate.FromParts(2026, null, 5).Precision, "A day without a month is not a date.");
        Assert.AreEqual(ReleaseDatePrecision.Month, ReleaseDate.FromParts(2026, 10, null).Precision);
        Assert.AreEqual(ReleaseDatePrecision.Month, ReleaseDate.FromParts(2026, 2, 30).Precision, "An impossible day falls back to the month.");
        var day = ReleaseDate.FromParts(2026, 10, 5);
        Assert.AreEqual(ReleaseDatePrecision.Day, day.Precision);
        Assert.AreEqual("2026-10-05", day.ToStorage());
    }

    [TestMethod]
    [DataRow("2026", ReleaseDatePrecision.Year, "2026")]
    [DataRow("2026-10", ReleaseDatePrecision.Month, "2026-10")]
    [DataRow("2026-Q4", ReleaseDatePrecision.Quarter, "2026-Q4")]
    [DataRow("Q4 2026", ReleaseDatePrecision.Quarter, "2026-Q4")]
    [DataRow("2026-10-05", ReleaseDatePrecision.Day, "2026-10-05")]
    [DataRow("2026-10-05T00:00:00+09:00", ReleaseDatePrecision.Day, "2026-10-05")]
    [DataRow("2026-10-05T00:00:00Z", ReleaseDatePrecision.Day, "2026-10-05")]
    [DataRow("2026-10-05T20:00:00+09:00", ReleaseDatePrecision.DateTime, "2026-10-05T11:00:00Z")]
    public void BookAndProviderDateTextParsesWithHonestPrecision(string text, ReleaseDatePrecision precision, string stored)
    {
        Assert.IsTrue(ReleaseDate.TryParse(text, out var date));
        Assert.AreEqual(precision, date.Precision);
        Assert.AreEqual(stored, date.ToStorage());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("sometime")]
    [DataRow("2026-13")]
    [DataRow("Q5 2026")]
    public void UnparseableDateTextIsRejected(string text)
    {
        Assert.IsFalse(ReleaseDate.TryParse(text, out var date));
        Assert.AreEqual(ReleaseDatePrecision.Unknown, date.Precision);
    }

    [TestMethod]
    public void StoredInstantsRoundTripEvenAtMidnightUtc()
    {
        var midnight = ReleaseDate.FromInstant(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var restored = ReleaseDate.FromStorage(midnight.ToStorage(), ReleaseDatePrecision.DateTime);
        Assert.AreEqual(ReleaseDatePrecision.DateTime, restored.Precision);
        Assert.AreEqual(midnight.Instant, restored.Instant);

        foreach (var date in new[] { ReleaseDate.FromYear(2026), ReleaseDate.FromQuarter(2026, 4), ReleaseDate.FromMonth(2026, 10), ReleaseDate.FromDay(new DateOnly(2026, 10, 5)) })
        {
            Assert.AreEqual(date, ReleaseDate.FromStorage(date.ToStorage(), date.Precision));
        }

        Assert.AreEqual(ReleaseDate.Unknown, ReleaseDate.FromStorage("2026", ReleaseDatePrecision.Day), "A stored value never gains precision.");
    }

    [TestMethod]
    public void InstantsFallOnTheViewersLocalDay()
    {
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        var airing = ReleaseDate.FromInstant(new DateTimeOffset(2026, 10, 5, 16, 30, 0, TimeSpan.Zero));

        Assert.AreEqual(new DateOnly(2026, 10, 5), airing.ExactDay(Utc));
        Assert.AreEqual(new DateOnly(2026, 10, 6), airing.ExactDay(tokyo));
    }

    [TestMethod]
    public void ImpreciseDatesAreReleasedOnlyOnceTheWholePeriodHasPassed()
    {
        Assert.IsFalse(ReleaseDate.FromMonth(2026, 10).IsReleased(Now, Utc), "October has not ended.");
        Assert.IsTrue(ReleaseDate.FromMonth(2026, 9).IsReleased(Now, Utc));
        Assert.IsFalse(ReleaseDate.FromQuarter(2026, 4).IsReleased(Now, Utc));
        Assert.IsFalse(ReleaseDate.FromYear(2026).IsReleased(Now, Utc));
        Assert.IsFalse(ReleaseDate.Unknown.IsReleased(Now, Utc));
        Assert.IsTrue(ReleaseDate.FromDay(new DateOnly(2026, 10, 1)).IsReleased(Now, Utc), "An exact day is out from its start.");
        Assert.IsFalse(ReleaseDate.FromInstant(Now.AddMinutes(1)).IsReleased(Now, Utc));
        Assert.IsTrue(ReleaseDate.FromInstant(Now).IsReleased(Now, Utc));
    }

    [TestMethod]
    public void PeriodsCoverTheWholeMonthQuarterOrYear()
    {
        Assert.AreEqual((new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31)), ReleaseDate.FromQuarter(2026, 4).Period(Utc));
        Assert.AreEqual((new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)), ReleaseDate.FromMonth(2026, 2).Period(Utc));
        Assert.IsNull(ReleaseDate.Unknown.Period(Utc));
        Assert.IsTrue(ReleaseDate.FromYear(2026).Overlaps(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), Utc));
        Assert.IsFalse(ReleaseDate.FromMonth(2026, 11).Overlaps(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 31), Utc));
    }

    // ---- Normalization ----

    private const string AniListResponse = """
        {
          "data": {
            "media": {
              "media": [
                { "id": 101, "type": "ANIME", "status": "RELEASING", "startDate": { "year": 2026, "month": 7, "day": 3 },
                  "nextAiringEpisode": { "episode": 13, "airingAt": 1791640800 } },
                { "id": 202, "type": "ANIME", "status": "NOT_YET_RELEASED", "startDate": { "year": 2027, "month": 1, "day": null },
                  "nextAiringEpisode": null },
                { "id": 303, "type": "MANGA", "status": "NOT_YET_RELEASED", "startDate": { "year": 2027, "month": null, "day": null },
                  "nextAiringEpisode": null },
                { "id": 404, "type": "ANIME", "status": "NOT_YET_RELEASED", "startDate": { "year": 2026, "month": 10, "day": 10 },
                  "nextAiringEpisode": { "episode": 1, "airingAt": 1791640800 } }
              ]
            },
            "schedule": {
              "pageInfo": { "hasNextPage": true },
              "airingSchedules": [
                { "mediaId": 101, "episode": 12, "airingAt": 1791036000 },
                { "mediaId": 101, "episode": 13, "airingAt": 1791640800 },
                { "mediaId": 101, "episode": 0, "airingAt": 1791640800 }
              ]
            }
          }
        }
        """;

    [TestMethod]
    public void AniListResponseParsesMediaAiringsAndPaging()
    {
        var schedule = AniListReleaseScheduleParser.Parse(AniListResponse);

        Assert.AreEqual(4, schedule.Media.Count);
        Assert.AreEqual(2, schedule.Airings.Count, "Episode 0 is not a valid airing.");
        Assert.IsTrue(schedule.HasMoreAirings);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1791036000), schedule.Airings[0].AiringAt);
        Assert.IsNull(schedule.Media[1].StartDay);
    }

    [TestMethod]
    public void AniListGraphQlErrorsAreProviderFailures()
    {
        Assert.ThrowsExactly<MetadataProviderException>(() =>
            AniListReleaseScheduleParser.Parse("""{ "errors": [ { "message": "Too Many Requests." } ], "data": null }"""));
    }

    [TestMethod]
    public void NormalizationStoresExactAiringsAndStartDatesWithoutInventingDays()
    {
        var schedule = AniListReleaseScheduleParser.Parse(AniListResponse);
        var snapshots = AniListReleaseNormalizer.Normalize([101, 202, 303, 404, 505], schedule.Media, schedule.Airings)
            .ToDictionary(snapshot => snapshot.ExternalId);

        var releasing = snapshots["101"].Releases.OrderBy(release => release.UnitNumber).ToArray();
        Assert.AreEqual(2, releasing.Length, "The next airing duplicates the schedule and is stored once.");
        Assert.IsTrue(releasing.All(release => release.Kind == ReleaseKind.Episode && release.Date.Precision == ReleaseDatePrecision.DateTime));
        Assert.AreEqual("RELEASING", snapshots["101"].ProviderStatus);

        var upcoming = snapshots["202"].Releases.Single();
        Assert.AreEqual(ReleaseKind.SeasonPremiere, upcoming.Kind);
        Assert.AreEqual(1, upcoming.UnitNumber);
        Assert.AreEqual(ReleaseDatePrecision.Month, upcoming.Date.Precision, "AniList only knows January 2027.");

        var manga = snapshots["303"].Releases.Single();
        Assert.AreEqual(ReleaseKind.SeriesStart, manga.Kind);
        Assert.AreEqual(0, manga.UnitNumber);
        Assert.AreEqual(ReleaseDate.FromYear(2027), manga.Date);

        var scheduled = snapshots["404"].Releases.Single();
        Assert.AreEqual(ReleaseDatePrecision.DateTime, scheduled.Date.Precision, "A scheduled first airing wins over the plain start date.");

        Assert.AreEqual(0, snapshots["505"].Releases.Count, "An entry AniList no longer returns clears its future releases.");
        Assert.IsNull(snapshots["505"].ProviderStatus);
    }

    // ---- Bounded refresh ----

    [TestMethod]
    public void RefreshSkipsEndedSeriesAndRespectsFreshnessAndBounds()
    {
        var now = Now.UtcDateTime;
        var sources = new Dictionary<string, ReleaseCacheSource>
        {
            ["2"] = new("anilist", "2", "RELEASING", now.AddHours(-1), now.AddHours(-1), null),
            ["3"] = new("anilist", "3", "RELEASING", now.AddHours(-13), now.AddHours(-13), null),
            ["4"] = new("anilist", "4", "FINISHED", now.AddDays(-2), now.AddDays(-2), null),
            ["5"] = new("anilist", "5", "FINISHED", now.AddDays(-31), now.AddDays(-31), null),
            ["6"] = new("anilist", "6", "RELEASING", now.AddDays(-1), now.AddMinutes(-10), "AniList returned HTTP 500."),
            ["9"] = new("anilist", "9", "RELEASING", null, now.AddHours(-2), "AniList returned HTTP 500.")
        };
        var targets = new[]
        {
            new AniListReleaseTarget(1, "RELEASING", false),
            new AniListReleaseTarget(2, "RELEASING", false),
            new AniListReleaseTarget(3, null, false),
            new AniListReleaseTarget(4, "RELEASING", false),
            new AniListReleaseTarget(5, null, false),
            new AniListReleaseTarget(6, null, false),
            new AniListReleaseTarget(7, "FINISHED", false),
            new AniListReleaseTarget(8, "RELEASING", true),
            new AniListReleaseTarget(9, null, false),
            new AniListReleaseTarget(10, "NOT_YET_RELEASED", true)
        };

        var due = ReleaseCalendarRefresher.SelectDue(targets, sources, now).Select(target => target.Id).ToArray();

        CollectionAssert.AreEqual(new[] { 1, 9, 10, 5, 3 }, due, "Never-fetched entries first, then the oldest data.");

        var many = Enumerable.Range(1, ReleaseCalendarRefresher.MaxIdsPerRun + 50).Select(id => new AniListReleaseTarget(id, null, false));
        Assert.AreEqual(ReleaseCalendarRefresher.MaxIdsPerRun, ReleaseCalendarRefresher.SelectDue(many, new Dictionary<string, ReleaseCacheSource>(), now).Count);
    }

    // ---- Filtering and assembly ----

    private static ReleaseEvent Event(
        ReleaseMediaType type,
        ReleaseDate date,
        ReleaseLocalStatus? local = null,
        string title = "Title",
        int unit = 1) =>
        new(
            ReleaseEvent.BuildId(type, Guid.NewGuid(), ReleaseKind.Episode, new ReleaseUnit(unit)),
            type,
            Guid.NewGuid(),
            null,
            ReleaseKind.Episode,
            title,
            new ReleaseUnit(unit),
            date,
            "anilist",
            "1",
            local ?? ReleaseLocalStatus.InLibraryOnly);

    [TestMethod]
    public void FilterByMediaTypeAndLibraryState()
    {
        var released = ReleaseDate.FromInstant(Now.AddDays(-1));
        var upcoming = ReleaseDate.FromInstant(Now.AddDays(1));
        var missing = Event(ReleaseMediaType.Anime, released, new ReleaseLocalStatus(true, true, ReleaseLocalState.Missing));
        var monitoredUpcoming = Event(ReleaseMediaType.Anime, upcoming, new ReleaseLocalStatus(true, true, ReleaseLocalState.Monitored));
        var available = Event(ReleaseMediaType.Book, released, new ReleaseLocalStatus(true, null, ReleaseLocalState.Available));
        var chapterMissing = Event(ReleaseMediaType.LightNovel, released, new ReleaseLocalStatus(true, null, ReleaseLocalState.Missing));
        var all = new[] { missing, monitoredUpcoming, available, chapterMissing };

        IEnumerable<ReleaseEvent> Apply(ReleaseCalendarFilter filter) => all.Where(release => filter.Matches(release, Now, Utc));

        CollectionAssert.AreEqual(all, Apply(ReleaseCalendarFilter.All).ToArray());
        CollectionAssert.AreEqual(new[] { missing, monitoredUpcoming }, Apply(new(new HashSet<ReleaseMediaType> { ReleaseMediaType.Anime }, ReleaseStateFilter.All)).ToArray());
        CollectionAssert.AreEqual(new[] { missing, monitoredUpcoming }, Apply(new(new HashSet<ReleaseMediaType>(), ReleaseStateFilter.Monitored)).ToArray());
        CollectionAssert.AreEqual(new[] { missing, chapterMissing }, Apply(new(new HashSet<ReleaseMediaType>(), ReleaseStateFilter.Missing)).ToArray());
        CollectionAssert.AreEqual(new[] { available }, Apply(new(new HashSet<ReleaseMediaType>(), ReleaseStateFilter.Available)).ToArray());
        Assert.IsFalse(
            ReleaseCalendarFilter.IsMissing(Event(ReleaseMediaType.Anime, upcoming, new ReleaseLocalStatus(true, true, ReleaseLocalState.Wanted)), Now, Utc),
            "Nothing is missing before it is released.");
    }

    [TestMethod]
    public void AssemblerPutsExactDatesOnDaysAndPeriodsIntoTheImpreciseList()
    {
        var start = new DateOnly(2026, 10, 1);
        var end = new DateOnly(2026, 10, 7);
        var late = Event(ReleaseMediaType.Anime, ReleaseDate.FromInstant(new DateTimeOffset(2026, 10, 3, 18, 0, 0, TimeSpan.Zero)), title: "B");
        var early = Event(ReleaseMediaType.Anime, ReleaseDate.FromInstant(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero)), title: "Z");
        var day = Event(ReleaseMediaType.Book, ReleaseDate.FromDay(new DateOnly(2026, 10, 7)));
        var outside = Event(ReleaseMediaType.Book, ReleaseDate.FromDay(new DateOnly(2026, 10, 8)));
        var month = Event(ReleaseMediaType.Manga, ReleaseDate.FromMonth(2026, 10));
        var otherMonth = Event(ReleaseMediaType.Manga, ReleaseDate.FromMonth(2026, 12));
        var unknown = Event(ReleaseMediaType.LightNovel, ReleaseDate.Unknown);

        var (days, imprecise) = ReleaseCalendarAssembler.Assemble(
            [late, early, day, outside, month, otherMonth, unknown, late],
            start,
            end,
            Utc,
            ReleaseCalendarFilter.All,
            Now);

        Assert.AreEqual(7, days.Count);
        CollectionAssert.AreEqual(new[] { early, late }, days[2].Events.ToArray(), "Ordered by time; the duplicate is dropped.");
        CollectionAssert.AreEqual(new[] { day }, days[6].Events.ToArray());
        Assert.IsFalse(days.SelectMany(item => item.Events).Contains(outside));
        CollectionAssert.AreEqual(new[] { month, unknown }, imprecise.ToArray(), "Periods first, unknown dates last; no fake day.");

        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        var (tokyoDays, _) = ReleaseCalendarAssembler.Assemble([late], start, end, tokyo, ReleaseCalendarFilter.All, Now);
        CollectionAssert.AreEqual(new[] { late }, tokyoDays[3].Events.ToArray(), "18:00 UTC is the next day in Tokyo.");
    }

    // ---- Library-state merge ----

    private static AnimeEpisodeMetadataMapping Mapping(int season, int localStart, int localEnd, int remoteStart, string externalId, int? count = null) =>
        new(Guid.NewGuid(), Guid.Empty, season, localStart, localEnd, remoteStart, "anilist", externalId, "Part", count, DateTimeOffset.UtcNow);

    private static AnimeReleaseLibraryEntry Entry(
        string? matched,
        IReadOnlyList<AnimeEpisodeMetadataMapping>? mappings = null,
        params (int Season, int Episode, bool HasFile)[] episodes) =>
        new(
            Guid.NewGuid(),
            "frieren",
            "Frieren",
            null,
            matched,
            mappings ?? [],
            episodes.ToDictionary(item => (item.Season, item.Episode), item => new AnimeReleaseLocalEpisode(Guid.NewGuid(), item.HasFile)));

    [TestMethod]
    public void AiringsAreMappedToLocalEpisodesLikeTheAcquisitionInventory()
    {
        var single = Entry("100", null, (1, 1, true), (1, 2, false));
        Assert.AreEqual((1, 5), AnimeReleaseStateResolver.ResolveLocalSlot(single, "100", 5));
        Assert.IsNull(AnimeReleaseStateResolver.ResolveLocalSlot(single, "999", 5), "Unrelated AniList ids are not placed.");

        var seasons = Entry("100", null, (1, 1, true), (2, 1, true));
        Assert.IsNull(AnimeReleaseStateResolver.ResolveLocalSlot(seasons, "100", 3), "Several seasons without mappings are ambiguous.");

        var mapped = Entry(
            "100",
            [Mapping(1, 1, 12, 1, "100", 12), Mapping(2, 1, 3, 1, "200")],
            (1, 1, true), (2, 1, true));
        Assert.AreEqual((2, 2), AnimeReleaseStateResolver.ResolveLocalSlot(mapped, "200", 2), "Explicit range.");
        Assert.AreEqual((2, 6), AnimeReleaseStateResolver.ResolveLocalSlot(mapped, "200", 6), "An airing season continues past the mapped range.");
        Assert.IsNull(AnimeReleaseStateResolver.ResolveLocalSlot(mapped, "100", 13), "Beyond the AniList episode count.");

        var offset = Entry("300", [Mapping(1, 13, 24, 1, "300", 12)], (1, 13, false));
        Assert.AreEqual((1, 15), AnimeReleaseStateResolver.ResolveLocalSlot(offset, "300", 3), "Absolute local numbering is kept.");
    }

    private static WorkMonitoringView View(bool workMonitored, Dictionary<(int, int), bool>? episodes = null)
    {
        var workId = Guid.NewGuid();
        var decisions = new Dictionary<Guid, MonitoringDecision> { [workId] = new(MonitoringTargetKind.Work, workMonitored) };
        return new WorkMonitoringView(workId, decisions, relationMonitored: false, new NumberedDecisions(episodes ?? [], new Dictionary<int, bool>()));
    }

    [TestMethod]
    public void LocalStateComesFromLibraryFilesAndMonitoring()
    {
        var entry = Entry("100", null, (1, 1, true), (1, 2, false), (1, 3, false), (1, 4, false), (1, 5, false));
        var view = View(workMonitored: true, episodes: new Dictionary<(int, int), bool> { [(1, 7)] = false });
        var key3 = new AnimeEpisodeKey("frieren", 1, 3);
        var key4 = new AnimeEpisodeKey("frieren", 1, 4);
        var key5 = new AnimeEpisodeKey("frieren", 1, 5);
        var wanted = new[] { new AnimeWantedEpisode(key4, AnimeWantedReason.Missing, Now), new AnimeWantedEpisode(key5, AnimeWantedReason.Missing, Now) };
        // The request of the anime has episode 3 on its way, or came back from a search with nothing; without a request the wanted episodes only wait.
        var grabbing = new AnimeEpisodeStateMap(wanted, new Dictionary<string, AnimeRequestPayload> { ["frieren"] = new("frieren", [key3]) });
        var searched = new AnimeEpisodeStateMap(wanted, new Dictionary<string, AnimeRequestPayload> { ["frieren"] = new("frieren") { Searches = 1, NextSearchUtc = Now.UtcDateTime.AddHours(1) } });
        var waiting = new AnimeEpisodeStateMap(wanted, new Dictionary<string, AnimeRequestPayload>());
        var monitoring = waiting;

        ReleaseLocalState State(int episode, bool released, AnimeEpisodeStateMap? states = null) =>
            AnimeReleaseStateResolver.Resolve(entry, (1, episode), states ?? waiting, view, released).State;

        Assert.AreEqual(ReleaseLocalState.Available, State(1, true));
        Assert.AreEqual(ReleaseLocalState.Missing, State(2, true));
        Assert.AreEqual(ReleaseLocalState.Grabbed, State(3, true, grabbing));
        Assert.AreEqual(ReleaseLocalState.Failed, State(4, true, searched));
        Assert.AreEqual(ReleaseLocalState.Wanted, State(5, true));
        Assert.AreEqual(ReleaseLocalState.Monitored, State(6, false), "Upcoming and monitored.");
        Assert.AreEqual(ReleaseLocalState.NotMonitored, State(7, true), "The episode override turns monitoring off.");
        Assert.IsTrue(AnimeReleaseStateResolver.Resolve(entry, (1, 6), monitoring, view, false).Monitored);

        var unmonitored = AnimeReleaseStateResolver.Resolve(entry, (1, 2), AnimeEpisodeStateMap.Empty, View(workMonitored: false), released: true);
        Assert.AreEqual(ReleaseLocalState.NotMonitored, unmonitored.State, "A release date alone never makes an episode wanted.");
        Assert.AreEqual(false, unmonitored.Monitored);

        var unplaced = AnimeReleaseStateResolver.Resolve(entry, null, monitoring, view, released: true);
        Assert.AreEqual(ReleaseLocalState.None, unplaced.State);
        Assert.IsTrue(unplaced.InLibrary);
    }

    [TestMethod]
    public void EventsReferenceTheCanonicalAnimeAndLocalEpisode()
    {
        var entry = Entry("100", null, (1, 1, true));
        var releases = new[]
        {
            new CachedRelease("anilist", "100", ReleaseKind.SeasonPremiere, 1, ReleaseDate.FromInstant(Now.AddDays(-7))),
            new CachedRelease("anilist", "100", ReleaseKind.Episode, 2, ReleaseDate.FromInstant(Now.AddDays(1))),
            new CachedRelease("anilist", "999", ReleaseKind.Episode, 2, ReleaseDate.FromInstant(Now.AddDays(1)))
        };

        var events = AnimeReleaseStateResolver.ToEvents(releases, [entry], AnimeEpisodeStateMap.Empty, new Dictionary<string, WorkMonitoringView> { ["frieren"] = View(workMonitored: false) }, Now, Utc);

        Assert.AreEqual(2, events.Count);
        var premiere = events.Single(release => release.Kind == ReleaseKind.SeasonPremiere);
        Assert.AreEqual(entry.AnimeId, premiere.MediaId);
        Assert.AreEqual(entry.Episodes[(1, 1)].EpisodeId, premiere.UnitId);
        Assert.AreEqual("Frieren", premiere.Title);
        Assert.AreEqual(ReleaseLocalState.Available, premiere.Local.State);
        Assert.IsNull(events.Single(release => release.Kind == ReleaseKind.Episode).UnitId, "No local episode yet.");
    }

    // ---- Presentation ----

    [TestMethod]
    public void PresenterShowsDatesAtTheirPrecision()
    {
        var presenter = new ReleaseCalendarPresenter(UiTextBundle.English, Utc, Now, playbackEnabled: true);

        Assert.AreEqual("October 2026", presenter.DateLabel(ReleaseDate.FromMonth(2026, 10)));
        Assert.AreEqual("Q4 2026", presenter.DateLabel(ReleaseDate.FromQuarter(2026, 4)));
        Assert.AreEqual("2026", presenter.DateLabel(ReleaseDate.FromYear(2026)));
        Assert.AreEqual("Date unknown", presenter.DateLabel(ReleaseDate.Unknown));
        Assert.IsNull(presenter.TimeLabel(ReleaseDate.FromDay(new DateOnly(2026, 10, 5))), "A day has no time.");
        Assert.IsNotNull(presenter.TimeLabel(ReleaseDate.FromInstant(Now)));
        StringAssert.StartsWith(presenter.DayHeading(new DateOnly(2026, 10, 1)), "Today");
        StringAssert.StartsWith(presenter.DayHeading(new DateOnly(2026, 10, 2)), "Tomorrow");
    }

    [TestMethod]
    public void AnAvailableEpisodeOpensThePlayerOnlyWhereThereIsOne()
    {
        var available = Event(ReleaseMediaType.Anime, ReleaseDate.FromInstant(Now.AddDays(-1)), new ReleaseLocalStatus(true, true, ReleaseLocalState.Available)) with { UnitId = Guid.NewGuid() };

        var playing = new ReleaseCalendarPresenter(UiTextBundle.English, Utc, Now, playbackEnabled: true);
        var managerOnly = new ReleaseCalendarPresenter(UiTextBundle.English, Utc, Now, playbackEnabled: false);

        StringAssert.StartsWith(playing.Href(available), "/Library/Episode/");
        Assert.AreEqual($"/Library/Anime/{available.MediaId}", managerOnly.Href(available), "Without Playback the title page is the destination, never the player.");
    }

    [TestMethod]
    [DataRow(ReleaseLocalState.Wanted, "Looking for media")]
    [DataRow(ReleaseLocalState.Searching, "Looking for media")]
    [DataRow(ReleaseLocalState.Grabbed, "Getting media")]
    [DataRow(ReleaseLocalState.Failed, "Needs attention")]
    public void TheCalendarNamesAcquisitionInConsumerWords(ReleaseLocalState state, string expected)
    {
        var presenter = new ReleaseCalendarPresenter(UiTextBundle.English, Utc, Now, playbackEnabled: true);

        Assert.AreEqual(expected, presenter.State(new ReleaseLocalStatus(true, true, state)).Label);
    }

    [TestMethod]
    public void PresenterLabelsUnitsAndKinds()
    {
        var presenter = new ReleaseCalendarPresenter(UiTextBundle.English, Utc, Now, playbackEnabled: true);
        ReleaseEvent With(ReleaseKind kind, ReleaseUnit? unit) =>
            Event(ReleaseMediaType.Anime, ReleaseDate.Unknown) with { Kind = kind, Unit = unit };

        Assert.AreEqual("Premiere · Episode 1", presenter.UnitLabel(With(ReleaseKind.SeasonPremiere, new ReleaseUnit(1, 1))));
        Assert.AreEqual("S2 · Episode 5", presenter.UnitLabel(With(ReleaseKind.Episode, new ReleaseUnit(5, 2))));
        Assert.AreEqual("Chapter 143.5", presenter.UnitLabel(With(ReleaseKind.Chapter, new ReleaseUnit(143.5))));
        Assert.AreEqual("Series start", presenter.UnitLabel(With(ReleaseKind.SeriesStart, null)));
        Assert.AreEqual("Digital release", presenter.UnitLabel(With(ReleaseKind.Digital, null)));
    }
}
