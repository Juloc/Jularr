using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.MediaMapping;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Tracking;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class AniListSyncTests
{
    private const string Owner = "owner";
    private const string Learner = "learner-1";
    private const string OwnerToken = "owner-token";
    private const string LearnerToken = "learner-token";

    [TestMethod]
    public async Task OffNeverWritesAsync()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, 42, OwnerToken);
        var anime = await fixture.AddAnimeAsync("Off Anime", 555, episodes: 12);
        await fixture.WatchAsync(Owner, anime, 3, completed: true, fixture.Time.Ago(TimeSpan.FromMinutes(10)));
        fixture.Remote.Put(OwnerToken, 555, progress: 1);

        var summary = await fixture.RunAsync();

        Assert.AreEqual(0, summary.Evaluated);
        Assert.AreEqual(0, fixture.Remote.Calls, "Off (manual only) must never contact AniList.");
        Assert.AreEqual(1, fixture.Remote.Progress(OwnerToken, 555));
    }

    [TestMethod]
    public async Task OnCompletionWritesOnlyAfterAnEpisodeOrChapterIsFinishedAsync()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, 42, OwnerToken);
        await fixture.EnableAsync(Owner, AniListSyncMode.OnCompletion);
        var anime = await fixture.AddAnimeAsync("Completion Anime", 555, episodes: 12);
        fixture.Remote.Put(OwnerToken, 555, progress: 2);

        await fixture.WatchAsync(Owner, anime, 3, completed: false, fixture.Time.Ago(TimeSpan.FromMinutes(5)));
        await fixture.RunAsync();
        Assert.AreEqual(0, fixture.Remote.Calls, "A partially watched episode is not a completion.");

        await fixture.WatchAsync(Owner, anime, 3, completed: true, fixture.Time.Ago(TimeSpan.FromSeconds(5)));
        var written = await fixture.RunAsync();
        Assert.AreEqual(1, written.Written);
        Assert.AreEqual(3, fixture.Remote.Progress(OwnerToken, 555));
        Assert.IsTrue(fixture.Remote.Mutations.All(x => x.Contains("\"progress\":3")));

        fixture.Time.Advance(TimeSpan.FromMinutes(10));
        var callsBefore = fixture.Remote.Calls;
        await fixture.WatchAsync(Owner, anime, 4, completed: false, fixture.Time.Ago(TimeSpan.FromMinutes(5)));
        await fixture.RunAsync();
        Assert.AreEqual(callsBefore, fixture.Remote.Calls, "Watching the next episode halfway does not write.");
    }

    [TestMethod]
    public async Task GapBeforeAFinishedEpisodeStopsTheWrittenProgressAndNeverLowersAsync()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, 42, OwnerToken);
        await fixture.EnableAsync(Owner, AniListSyncMode.OnCompletion);
        var anime = await fixture.AddAnimeAsync("Gap Anime", 555, episodes: 12);
        fixture.Remote.Put(OwnerToken, 555, progress: 0);

        await CanonicalProgressSeed.SetAsync(fixture.Db, Owner, anime.Episodes[0], 0, null, true, fixture.Time.Ago(TimeSpan.FromMinutes(10)));
        await CanonicalProgressSeed.SetAsync(fixture.Db, Owner, anime.Episodes[2], 0, null, true, fixture.Time.Ago(TimeSpan.FromSeconds(5)));
        var written = await fixture.RunAsync();

        Assert.AreEqual(1, written.Written);
        Assert.AreEqual(1, fixture.Remote.Progress(OwnerToken, 555), "E1 and E3 finished with E2 open must write 1, never 3.");

        fixture.Time.Advance(TimeSpan.FromMinutes(10));
        await CanonicalProgressSeed.SetAsync(fixture.Db, Owner, anime.Episodes[1], 0, null, true, fixture.Time.Ago(TimeSpan.FromSeconds(5)));
        await fixture.RunAsync();
        Assert.AreEqual(3, fixture.Remote.Progress(OwnerToken, 555), "Closing the gap advances to the next open episode.");

        fixture.Time.Advance(TimeSpan.FromMinutes(10));
        var callsBefore = fixture.Remote.Calls;
        await CanonicalProgressSeed.SetAsync(fixture.Db, Owner, anime.Episodes[3], 600_000, 1_440_000, false, fixture.Time.Ago(TimeSpan.FromSeconds(5)));
        await fixture.RunAsync();
        Assert.AreEqual(callsBefore, fixture.Remote.Calls, "A seek or partial watch of E4 is only a resume point and never reaches AniList.");
        Assert.AreEqual(3, fixture.Remote.Progress(OwnerToken, 555));
    }

    [TestMethod]
    public async Task RepeatingAniListEntryWritesForwardProgressAsync()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, 42, OwnerToken);
        await fixture.EnableAsync(Owner, AniListSyncMode.OnCompletion);
        var anime = await fixture.AddAnimeAsync("Rewatch Anime", 555, episodes: 12);
        fixture.Remote.Put(OwnerToken, 555, progress: 1, status: "REPEATING");

        await fixture.WatchAsync(Owner, anime, 3, completed: true, fixture.Time.Ago(TimeSpan.FromSeconds(5)));
        var written = await fixture.RunAsync();

        Assert.AreEqual(1, written.Written);
        Assert.AreEqual(3, fixture.Remote.Progress(OwnerToken, 555));
    }

    [TestMethod]
    public async Task PlanningAnimeStartsWatchingWithStartDateAsync()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, 42, OwnerToken);
        await fixture.EnableAsync(Owner, AniListSyncMode.OnCompletion);
        var anime = await fixture.AddAnimeAsync("Planning Anime", 556, episodes: 12);
        fixture.Remote.Put(OwnerToken, 556, progress: 0, status: "PLANNING");

        await fixture.WatchAsync(Owner, anime, 3, completed: true, fixture.Time.Ago(TimeSpan.FromSeconds(5)));
        var written = await fixture.RunAsync();
        var today = fixture.Time.GetUtcNow();

        Assert.AreEqual(1, written.Written);
        Assert.AreEqual(3, fixture.Remote.Progress(OwnerToken, 556));
        Assert.AreEqual("CURRENT", fixture.Remote.Status(OwnerToken, 556));
        Assert.AreEqual(
            new AniListFuzzyDate(today.Year, today.Month, today.Day),
            fixture.Remote.StartedAt(OwnerToken, 556));
        StringAssert.Contains(fixture.Remote.Mutations.Single(), "status: CURRENT");
    }

    [TestMethod]
    public async Task PlanningAnimeStartDateUsesTheConfiguredLocalDayAsync()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        // A zone whose calendar day differs from the UTC day at the fixture's current time.
        var utcHour = fixture.Time.GetUtcNow().Hour;
        fixture.Time.Zone = TimeZoneInfo.CreateCustomTimeZone(
            "jularr-test-shifted",
            TimeSpan.FromHours(utcHour < 12 ? -12 : 14),
            "Shifted",
            "Shifted");
        await fixture.ConnectAsync(Owner, 42, OwnerToken);
        await fixture.EnableAsync(Owner, AniListSyncMode.OnCompletion);
        var anime = await fixture.AddAnimeAsync("Late Night Anime", 558, episodes: 12);
        fixture.Remote.Put(OwnerToken, 558, progress: 0, status: "PLANNING");

        await fixture.WatchAsync(Owner, anime, 1, completed: true, fixture.Time.Ago(TimeSpan.FromSeconds(5)));
        var written = await fixture.RunAsync();
        var localToday = TimeZoneInfo.ConvertTime(fixture.Time.GetUtcNow(), fixture.Time.Zone);
        var utcToday = fixture.Time.GetUtcNow();

        Assert.AreEqual(1, written.Written);
        Assert.AreEqual(
            new AniListFuzzyDate(localToday.Year, localToday.Month, localToday.Day),
            fixture.Remote.StartedAt(OwnerToken, 558));
        Assert.AreNotEqual(
            new AniListFuzzyDate(utcToday.Year, utcToday.Month, utcToday.Day),
            fixture.Remote.StartedAt(OwnerToken, 558));
    }

    [TestMethod]
    public async Task PlanningAnimePreservesExistingStartDateAsync()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, 42, OwnerToken);
        await fixture.EnableAsync(Owner, AniListSyncMode.OnCompletion);
        var anime = await fixture.AddAnimeAsync("Planned Earlier", 557, episodes: 12);
        var existingStart = new AniListFuzzyDate(2026, 8, 15);
        fixture.Remote.Put(
            OwnerToken,
            557,
            progress: 0,
            status: "PLANNING",
            startedAt: existingStart);

        await fixture.WatchAsync(Owner, anime, 2, completed: true, fixture.Time.Ago(TimeSpan.FromSeconds(5)));
        var written = await fixture.RunAsync();

        Assert.AreEqual(1, written.Written);
        Assert.AreEqual("CURRENT", fixture.Remote.Status(OwnerToken, 557));
        Assert.AreEqual(existingStart, fixture.Remote.StartedAt(OwnerToken, 557));
    }

    [TestMethod]
    public async Task PlanningMangaStartsReadingWithStartDateAsync()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, 42, OwnerToken);
        await fixture.EnableAsync(Owner, AniListSyncMode.OnCompletion);
        var seriesId = await fixture.AddMangaAsync("Planning Manga", 322, chapters: 3);
        fixture.Remote.Put(OwnerToken, 322, progress: 0, chapters: 50, status: "PLANNING");

        await fixture.ReadMangaAsync(
            Owner,
            seriesId,
            chapter: 2,
            page: 9,
            fixture.Time.Ago(TimeSpan.FromSeconds(30)));
        var written = await fixture.RunAsync();
        var today = fixture.Time.GetUtcNow();

        Assert.AreEqual(1, written.Written);
        Assert.AreEqual(2, fixture.Remote.Progress(OwnerToken, 322));
        Assert.AreEqual("CURRENT", fixture.Remote.Status(OwnerToken, 322));
        Assert.AreEqual(
            new AniListFuzzyDate(today.Year, today.Month, today.Day),
            fixture.Remote.StartedAt(OwnerToken, 322));
    }

    [TestMethod]
    public async Task OnCompletionWritesFinishedMangaChapterAsync()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, 42, OwnerToken);
        await fixture.EnableAsync(Owner, AniListSyncMode.OnCompletion);
        var seriesId = await fixture.AddMangaAsync("Completion Manga", 321, chapters: 3);
        fixture.Remote.Put(OwnerToken, 321, progress: 1, chapters: 50);

        await fixture.ReadMangaAsync(Owner, seriesId, chapter: 2, page: 9, fixture.Time.Ago(TimeSpan.FromSeconds(30)));
        await fixture.RunAsync();
        Assert.AreEqual(2, fixture.Remote.Progress(OwnerToken, 321));

        var callsBefore = fixture.Remote.Calls;
        await fixture.ReadMangaAsync(Owner, seriesId, chapter: 3, page: 3, fixture.Time.Ago(TimeSpan.FromSeconds(10)));
        await fixture.RunAsync();
        Assert.AreEqual(callsBefore, fixture.Remote.Calls, "Reading inside chapter 3 finishes nothing new.");
        Assert.IsTrue(fixture.Remote.Mutations.All(x => !x.Contains("\"progressVolumes\":")), "Chapter sync never sends volumes.");
    }

    [TestMethod]
    public async Task ContinuousDebouncesAndWritesForwardOnlyAsync()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, 42, OwnerToken);
        await fixture.EnableAsync(Owner, AniListSyncMode.Continuous);
        var workId = await fixture.AddNovelAsync("Continuous Novel", 777, chapters: 8);
        fixture.Remote.Put(OwnerToken, 777, progress: 2, chapters: 40);

        await fixture.ReadNovelAsync(Owner, workId, chapter: 6, permille: 1000, fixture.Time.Ago(TimeSpan.FromSeconds(30)));
        await fixture.RunAsync();
        Assert.AreEqual(0, fixture.Remote.Calls, "Reading is still active; the checkpoint is debounced.");

        fixture.Time.Advance(AniListSyncReconciler.ContinuousDebounce);
        var written = await fixture.RunAsync();
        Assert.AreEqual(1, written.Written);
        Assert.AreEqual(6, fixture.Remote.Progress(OwnerToken, 777));

        // Going back to an earlier chapter is a newer checkpoint but never lowers AniList.
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        await fixture.ReadNovelAsync(Owner, workId, chapter: 3, permille: 500, fixture.Time.Ago(TimeSpan.FromMinutes(3)));
        var backwards = await fixture.RunAsync();
        Assert.AreEqual(1, backwards.Evaluated);
        Assert.AreEqual(0, backwards.Written);
        Assert.AreEqual(6, fixture.Remote.Progress(OwnerToken, 777));
        Assert.AreEqual(1, fixture.Remote.Mutations.Count);
    }

    [TestMethod]
    public async Task AmbiguousPendingReviewAndUnmatchedNeverWriteAsync()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, 42, OwnerToken);
        await fixture.EnableAsync(Owner, AniListSyncMode.OnCompletion);
        var reviewed = await fixture.AddMangaAsync("Reviewed Manga", 321, chapters: 4);
        var ambiguousAnime = await fixture.AddAnimeAsync("Ambiguous Anime", 555, episodes: 12);
        var unmatched = await fixture.AddMangaAsync("Loose Manga", null, chapters: 2);
        fixture.Remote.Put(OwnerToken, 321, progress: 0, chapters: 50);
        fixture.Remote.Put(OwnerToken, 555, progress: 0);
        await fixture.ReviewStore.UpsertAsync(
            "manga", reviewed.ToString(), "Reviewed Manga", "reading-segments", "Ambiguous relation chain.", []);
        await fixture.ReviewStore.UpsertAsync(
            "anime", ambiguousAnime.Id.ToString(), "Ambiguous Anime", "identity", "Two AniList entries match equally well.", []);

        await fixture.ReadMangaAsync(Owner, reviewed, chapter: 3, page: 9, fixture.Time.Ago(TimeSpan.FromSeconds(30)));
        await fixture.ReadMangaAsync(Owner, unmatched, chapter: 2, page: 9, fixture.Time.Ago(TimeSpan.FromSeconds(30)));
        await fixture.WatchAsync(Owner, ambiguousAnime, 2, completed: true, fixture.Time.Ago(TimeSpan.FromSeconds(30)));

        var summary = await fixture.RunAsync();

        Assert.AreEqual(3, summary.Evaluated);
        Assert.AreEqual(0, fixture.Remote.Calls, "Pending review and unmatched works never contact AniList.");
        var overview = await fixture.Overview(Owner).GetOverviewAsync(CancellationToken.None);
        Assert.AreEqual(2, overview.Activity.Count, "Unmatched local-only works are not sync activity.");
        Assert.IsTrue(overview.Activity.All(x => x.Status == AniListSyncItemStatus.Blocked));
        Assert.AreEqual(
            "Ambiguous relation chain.",
            overview.Activity.Single(x => x.LocalId == reviewed).Message);
        Assert.IsTrue(overview.Activity.All(x => x.NextAttemptAt >= fixture.Time.GetUtcNow() + AniListSyncReconciler.BlockedRecheck));

        // Nothing is retried before the recheck time.
        await fixture.RunAsync();
        Assert.AreEqual(0, fixture.Remote.Calls);
    }

    [TestMethod]
    public async Task RemoteAheadIsNeverLoweredAndCountsAsHandledAsync()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, 42, OwnerToken);
        await fixture.EnableAsync(Owner, AniListSyncMode.OnCompletion);
        var anime = await fixture.AddAnimeAsync("Ahead Anime", 555, episodes: 12);
        fixture.Remote.Put(OwnerToken, 555, progress: 7);
        await fixture.WatchAsync(Owner, anime, 3, completed: true, fixture.Time.Ago(TimeSpan.FromSeconds(30)));

        var summary = await fixture.RunAsync();

        Assert.AreEqual(1, summary.Evaluated);
        Assert.AreEqual(0, fixture.Remote.Mutations.Count);
        Assert.AreEqual(7, fixture.Remote.Progress(OwnerToken, 555));
        var item = (await fixture.State(Owner)).Items.Single();
        Assert.AreEqual(AniListSyncItemStatus.UpToDate, item.Status);
        Assert.AreEqual("S1E3", item.HandledCompletionMarker);
        Assert.IsNull(item.NextAttemptAt);
    }

    [TestMethod]
    public async Task RepeatedPassesAreIdempotentAsync()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, 42, OwnerToken);
        await fixture.EnableAsync(Owner, AniListSyncMode.Continuous);
        var anime = await fixture.AddAnimeAsync("Idempotent Anime", 555, episodes: 12);
        fixture.Remote.Put(OwnerToken, 555, progress: 1);
        await fixture.WatchAsync(Owner, anime, 4, completed: true, fixture.Time.Ago(TimeSpan.FromMinutes(5)));

        await fixture.RunAsync();
        Assert.AreEqual(1, fixture.Remote.Mutations.Count);

        var callsAfterWrite = fixture.Remote.Calls;
        await fixture.RunAsync();
        await fixture.RunAsync();
        Assert.AreEqual(callsAfterWrite, fixture.Remote.Calls, "A handled checkpoint is not evaluated again.");

        // A newer checkpoint with the same resolved progress re-checks but never writes again.
        await fixture.WatchAsync(Owner, anime, 5, completed: false, fixture.Time.Ago(TimeSpan.FromMinutes(3)));
        var recheck = await fixture.RunAsync();
        Assert.AreEqual(1, recheck.Evaluated);
        Assert.AreEqual(0, recheck.Written);
        Assert.AreEqual(1, fixture.Remote.Mutations.Count);
        Assert.AreEqual(4, fixture.Remote.Progress(OwnerToken, 555));
    }

    [TestMethod]
    public async Task FailuresBackOffExponentiallyAndKeepTheCursorAsync()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, 42, OwnerToken);
        await fixture.EnableAsync(Owner, AniListSyncMode.OnCompletion);
        var anime = await fixture.AddAnimeAsync("Flaky Anime", 555, episodes: 12);
        fixture.Remote.Put(OwnerToken, 555, progress: 1);
        await fixture.WatchAsync(Owner, anime, 3, completed: true, fixture.Time.Ago(TimeSpan.FromSeconds(30)));

        fixture.Remote.FailNext(2, () => SyncRemote.Json(HttpStatusCode.InternalServerError, """{"errors":[{"message":"Internal"}]}"""));

        var first = await fixture.RunAsync();
        Assert.AreEqual(1, first.Failed);
        var failed = (await fixture.State(Owner)).Items.Single();
        Assert.AreEqual(AniListSyncItemStatus.Failed, failed.Status);
        Assert.AreEqual(1, failed.FailureCount);
        Assert.IsNull(failed.HandledLocalUpdatedAt, "The cursor only advances after success.");
        Assert.AreEqual(fixture.Time.GetUtcNow() + AniListSyncReconciler.BaseBackoff, failed.NextAttemptAt);
        StringAssert.Contains(failed.Message, "HTTP 500");

        var calls = fixture.Remote.Calls;
        await fixture.RunAsync();
        Assert.AreEqual(calls, fixture.Remote.Calls, "No retry before the backoff elapsed.");

        fixture.Time.Advance(AniListSyncReconciler.BaseBackoff);
        await fixture.RunAsync();
        var second = (await fixture.State(Owner)).Items.Single();
        Assert.AreEqual(2, second.FailureCount);
        Assert.AreEqual(fixture.Time.GetUtcNow() + (2 * AniListSyncReconciler.BaseBackoff), second.NextAttemptAt);

        fixture.Time.Advance(2 * AniListSyncReconciler.BaseBackoff);
        var recovered = await fixture.RunAsync();
        Assert.AreEqual(1, recovered.Written);
        var synced = (await fixture.State(Owner)).Items.Single();
        Assert.AreEqual(AniListSyncItemStatus.Synced, synced.Status);
        Assert.AreEqual(0, synced.FailureCount);
        Assert.IsNotNull(synced.HandledLocalUpdatedAt);
        Assert.AreEqual(3, fixture.Remote.Progress(OwnerToken, 555));

        Assert.AreEqual(TimeSpan.FromMinutes(4), AniListSyncReconciler.RetryDelay(AniListSyncItemStatus.Failed, 3));
        Assert.AreEqual(AniListSyncReconciler.MaxBackoff, AniListSyncReconciler.RetryDelay(AniListSyncItemStatus.Failed, 40));
        Assert.AreEqual(AniListSyncReconciler.MaxBlockedRecheck, AniListSyncReconciler.RetryDelay(AniListSyncItemStatus.Blocked, 40));
    }

    [TestMethod]
    public async Task RateLimitHonorsRetryAfterAndIsVisibleInActivityAsync()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, 42, OwnerToken);
        await fixture.EnableAsync(Owner, AniListSyncMode.OnCompletion);
        var first = await fixture.AddAnimeAsync("First Anime", 555, episodes: 12);
        var second = await fixture.AddAnimeAsync("Second Anime", 556, episodes: 12);
        fixture.Remote.Put(OwnerToken, 555, progress: 1);
        fixture.Remote.Put(OwnerToken, 556, progress: 1);
        await fixture.WatchAsync(Owner, first, 3, completed: true, fixture.Time.Ago(TimeSpan.FromMinutes(2)));
        await fixture.WatchAsync(Owner, second, 2, completed: true, fixture.Time.Ago(TimeSpan.FromMinutes(1)));

        fixture.Remote.FailNext(1, () =>
        {
            var response = SyncRemote.Json((HttpStatusCode)429, """{"errors":[{"message":"Too Many Requests.","status":429}]}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });

        var limited = await fixture.RunAsync();
        Assert.AreEqual(fixture.Time.GetUtcNow().AddSeconds(120), limited.RateLimitedUntil);
        Assert.AreEqual(1, limited.Evaluated, "The batch stops at the first rate-limited request.");
        Assert.AreEqual(1, fixture.Remote.Calls);

        var overview = await fixture.Overview(Owner).GetOverviewAsync(CancellationToken.None);
        var item = overview.Activity.Single();
        Assert.AreEqual(first.Id, item.LocalId);
        Assert.AreEqual(AniListSyncItemStatus.Failed, item.Status);
        StringAssert.Contains(item.Message, "rate limit");
        Assert.AreEqual(fixture.Time.GetUtcNow().AddSeconds(120), item.NextAttemptAt);
        Assert.AreEqual(fixture.Time.GetUtcNow().AddSeconds(120), overview.PausedUntil);

        fixture.Time.Advance(TimeSpan.FromSeconds(60));
        var paused = await fixture.RunAsync();
        Assert.AreEqual(0, paused.Evaluated);
        Assert.AreEqual(1, fixture.Remote.Calls, "Nothing is sent while AniList asks to wait.");

        fixture.Time.Advance(TimeSpan.FromSeconds(61));
        var resumed = await fixture.RunAsync();
        Assert.AreEqual(2, resumed.Written);
        Assert.AreEqual(3, fixture.Remote.Progress(OwnerToken, 555));
        Assert.AreEqual(2, fixture.Remote.Progress(OwnerToken, 556));
    }

    [TestMethod]
    public void RetryAfterPrefersHeaderThenResetThenDefault()
    {
        var now = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

        using var delta = new HttpResponseMessage((HttpStatusCode)429);
        delta.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        Assert.AreEqual(TimeSpan.FromSeconds(30), AniListRateLimitGate.ResolveRetryAfter(delta, now));

        using var date = new HttpResponseMessage((HttpStatusCode)429);
        date.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddMinutes(2));
        Assert.AreEqual(TimeSpan.FromMinutes(2), AniListRateLimitGate.ResolveRetryAfter(date, now));

        using var reset = new HttpResponseMessage((HttpStatusCode)429);
        reset.Headers.Add("X-RateLimit-Reset", now.AddSeconds(45).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        Assert.AreEqual(TimeSpan.FromSeconds(45), AniListRateLimitGate.ResolveRetryAfter(reset, now));

        using var none = new HttpResponseMessage((HttpStatusCode)429);
        Assert.AreEqual(AniListRateLimitGate.DefaultRetryAfter, AniListRateLimitGate.ResolveRetryAfter(none, now));

        using var huge = new HttpResponseMessage((HttpStatusCode)429);
        huge.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(3));
        Assert.AreEqual(AniListRateLimitGate.MaxRetryAfter, AniListRateLimitGate.ResolveRetryAfter(huge, now));
    }

    [TestMethod]
    public async Task ProfilesUseOnlyTheirOwnTokenSettingAndStateAsync()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, 42, OwnerToken);
        await fixture.ConnectAsync(Learner, 77, LearnerToken);
        await fixture.EnableAsync(Owner, AniListSyncMode.OnCompletion);
        await fixture.EnableAsync(Learner, AniListSyncMode.OnCompletion);
        var shared = await fixture.AddAnimeAsync("Shared Anime", 555, episodes: 12);
        fixture.Remote.Put(OwnerToken, 555, progress: 1);
        fixture.Remote.Put(LearnerToken, 555, progress: 1);

        await fixture.WatchAsync(Owner, shared, 5, completed: true, fixture.Time.Ago(TimeSpan.FromMinutes(1)));
        await fixture.WatchAsync(Learner, shared, 2, completed: true, fixture.Time.Ago(TimeSpan.FromMinutes(1)));

        var summary = await fixture.RunAsync();

        Assert.AreEqual(2, summary.Written);
        Assert.AreEqual(5, fixture.Remote.Progress(OwnerToken, 555), "Owner progress goes to the owner's AniList.");
        Assert.AreEqual(2, fixture.Remote.Progress(LearnerToken, 555), "Learner progress goes to the learner's AniList.");
        Assert.IsTrue(fixture.Remote.Requests.All(x => x.Token is OwnerToken or LearnerToken));

        var learnerOverview = await fixture.Overview(Learner).GetOverviewAsync(CancellationToken.None);
        Assert.AreEqual(2, learnerOverview.Activity.Single().LocalProgress);
        Assert.AreEqual(5, (await fixture.Overview(Owner).GetOverviewAsync(CancellationToken.None)).Activity.Single().LocalProgress);

        // Turning sync off for one profile does not affect the other.
        Assert.IsTrue(await fixture.Overview(Learner).SetModeAsync(AniListSyncMode.Off, CancellationToken.None));
        await fixture.WatchAsync(Owner, shared, 6, completed: true, fixture.Time.Ago(TimeSpan.FromSeconds(10)));
        await fixture.WatchAsync(Learner, shared, 3, completed: true, fixture.Time.Ago(TimeSpan.FromSeconds(10)));
        await fixture.RunAsync();
        Assert.AreEqual(6, fixture.Remote.Progress(OwnerToken, 555));
        Assert.AreEqual(2, fixture.Remote.Progress(LearnerToken, 555));
    }

    [TestMethod]
    public async Task SyncModeSurvivesTokenRefreshAndIgnoresProgressBeforeEnablingAsync()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, 42, OwnerToken);
        var anime = await fixture.AddAnimeAsync("Baseline Anime", 555, episodes: 12);
        fixture.Remote.Put(OwnerToken, 555, progress: 1);
        await fixture.WatchAsync(Owner, anime, 4, completed: true, fixture.Time.Ago(TimeSpan.FromMinutes(30)));

        var service = fixture.Overview(Owner);
        Assert.IsTrue(await service.SetModeAsync(AniListSyncMode.Continuous, CancellationToken.None));
        var enabled = await service.GetOverviewAsync(CancellationToken.None);
        Assert.AreEqual(AniListSyncMode.Continuous, enabled.Mode);
        Assert.AreEqual(fixture.Time.GetUtcNow(), enabled.EnabledAt);

        fixture.Time.Advance(TimeSpan.FromMinutes(10));
        await fixture.RunAsync();
        Assert.AreEqual(0, fixture.Remote.Calls, "Progress made before enabling is left to manual sync.");

        Assert.IsTrue(await service.SetModeAsync(AniListSyncMode.OnCompletion, CancellationToken.None));
        Assert.AreEqual(
            enabled.EnabledAt,
            (await service.GetOverviewAsync(CancellationToken.None)).EnabledAt,
            "Switching between automatic modes keeps the original baseline.");

        // Reconnecting the same AniList user (token refresh) keeps the setting;
        // connecting a different AniList user starts at Off.
        fixture.Remote.Viewer("refreshed-owner-token-0123456789", 42);
        await fixture.Service(Owner).ConnectAsync(12345, "refreshed-owner-token-0123456789", CancellationToken.None);
        var refreshed = await service.GetOverviewAsync(CancellationToken.None);
        Assert.AreEqual(AniListSyncMode.OnCompletion, refreshed.Mode);
        Assert.AreEqual(enabled.EnabledAt, refreshed.EnabledAt);

        fixture.Remote.Viewer("other-anilist-user-token-0123456789", 99);
        await fixture.Service(Owner).ConnectAsync(12345, "other-anilist-user-token-0123456789", CancellationToken.None);
        var otherUser = await service.GetOverviewAsync(CancellationToken.None);
        Assert.AreEqual(AniListSyncMode.Off, otherUser.Mode);
        Assert.IsNull(otherUser.EnabledAt);

        Assert.IsFalse(await fixture.Overview(Learner).SetModeAsync(AniListSyncMode.Continuous, CancellationToken.None),
            "A profile without AniList connection cannot enable sync.");
    }

    internal sealed class SyncRemote : HttpMessageHandler
    {
        private readonly Dictionary<(string Token, int MediaId), Entry> entries = [];
        private readonly Dictionary<int, int?> chapterCounts = [];
        private readonly Dictionary<string, int> viewers = [];
        private readonly Queue<Func<HttpResponseMessage>> failures = new();

        public int Calls { get; private set; }
        public List<(string Token, string Body)> Requests { get; } = [];

        public List<string> Mutations =>
            Requests.Where(x => x.Body.Contains("SaveMediaListEntry")).Select(x => x.Body).ToList();

        public void Viewer(string token, int viewerId) => viewers[token] = viewerId;

        public void Put(
            string token,
            int mediaId,
            int progress,
            int? chapters = null,
            string status = "CURRENT",
            AniListFuzzyDate? startedAt = null)
        {
            var effectiveStartedAt = startedAt ??
                (string.Equals(status, "PLANNING", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : new AniListFuzzyDate(2026, 1, 2));
            entries[(token, mediaId)] = new Entry(
                mediaId,
                viewers.GetValueOrDefault(token),
                progress,
                0,
                status,
                effectiveStartedAt);
            chapterCounts[mediaId] = chapters;
        }

        public int Progress(string token, int mediaId) => entries[(token, mediaId)].Progress;
        public string Status(string token, int mediaId) => entries[(token, mediaId)].Status;
        public AniListFuzzyDate? StartedAt(string token, int mediaId) => entries[(token, mediaId)].StartedAt;

        public void FailNext(int count, Func<HttpResponseMessage> response)
        {
            for (var index = 0; index < count; index++)
            {
                failures.Enqueue(response);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var token = request.Headers.Authorization?.Parameter ?? "";
            Requests.Add((token, body));

            if (failures.TryDequeue(out var failure))
            {
                return failure();
            }

            if (body.Contains("Viewer"))
            {
                return Json(
                    HttpStatusCode.OK,
                    """{"data":{"Viewer":{"id":""" + viewers[token].ToString(CultureInfo.InvariantCulture) +
                    ""","name":"viewer","avatar":{"medium":null}}}}""");
            }

            if (body.Contains("SaveMediaListEntry"))
            {
                var id = Variable(body, "id")!.Value;
                var current = entries[(token, id)];
                var startsTracking = body.Contains("status: CURRENT", StringComparison.Ordinal);
                var updated = current with
                {
                    Progress = Variable(body, "progress")!.Value,
                    ProgressVolumes = Variable(body, "progressVolumes") ?? current.ProgressVolumes,
                    Status = startsTracking ? "CURRENT" : current.Status,
                    StartedAt = startsTracking
                        ? new AniListFuzzyDate(
                            Variable(body, "startYear"),
                            Variable(body, "startMonth"),
                            Variable(body, "startDay"))
                        : current.StartedAt
                };
                entries[(token, id)] = updated;
                return Json(HttpStatusCode.OK, EntryJson("SaveMediaListEntry", updated));
            }

            if (body.Contains("MediaList("))
            {
                var mediaId = Variable(body, "mediaId")!.Value;
                return entries.TryGetValue((token, mediaId), out var entry)
                    ? Json(HttpStatusCode.OK, EntryJson("MediaList", entry))
                    : Json(
                        HttpStatusCode.NotFound,
                        """{"errors":[{"message":"Not Found.","status":404}],"data":{"MediaList":null}}""");
            }

            var metadataId = Variable(body, "id")!.Value;
            return Json(
                HttpStatusCode.OK,
                """{"data":{"Media":{"id":""" + metadataId.ToString(CultureInfo.InvariantCulture) +
                ""","chapters":""" +
                (chapterCounts.GetValueOrDefault(metadataId)?.ToString(CultureInfo.InvariantCulture) ?? "null") +
                "}}}");
        }

        public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static int? Variable(string body, string name)
        {
            var match = Regex.Match(body, $"\"{name}\":(\\d+)");
            return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        }

        private static string EntryJson(string root, Entry entry) =>
            "{\"data\":{\"" + root + "\":{" +
            "\"id\":" + entry.MediaId.ToString(CultureInfo.InvariantCulture) +
            ",\"userId\":" + entry.UserId.ToString(CultureInfo.InvariantCulture) +
            ",\"mediaId\":" + entry.MediaId.ToString(CultureInfo.InvariantCulture) +
            ",\"status\":\"" + entry.Status + "\"" +
            ",\"progress\":" + entry.Progress.ToString(CultureInfo.InvariantCulture) +
            ",\"progressVolumes\":" + entry.ProgressVolumes.ToString(CultureInfo.InvariantCulture) +
            ",\"score\":0,\"repeat\":0,\"priority\":0" +
            ",\"private\":false,\"notes\":\"keep me\",\"hiddenFromStatusLists\":false" +
            ",\"customLists\":null,\"advancedScores\":null" +
            ",\"startedAt\":" + FuzzyDateJson(entry.StartedAt) +
            ",\"completedAt\":{\"year\":null,\"month\":null,\"day\":null},\"updatedAt\":1}}}";

        private static string FuzzyDateJson(AniListFuzzyDate? date) =>
            $"{{\"year\":{JsonInt(date?.Year)},\"month\":{JsonInt(date?.Month)},\"day\":{JsonInt(date?.Day)}}}";

        private static string JsonInt(int? value) =>
            value?.ToString(CultureInfo.InvariantCulture) ?? "null";

        private sealed record Entry(
            int MediaId,
            int UserId,
            int Progress,
            int ProgressVolumes,
            string Status,
            AniListFuzzyDate? StartedAt);
    }

    private sealed class SyncTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;

        public override TimeZoneInfo LocalTimeZone => Zone;

        public void Advance(TimeSpan by) => now += by;

        public DateTime Ago(TimeSpan by) => (now - by).UtcDateTime;
    }

    private sealed record TestAnime(Guid Id, IReadOnlyList<Guid> Episodes);

    private sealed class SyncFixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly AniListAccountStore accountStore;
        private readonly AniListSyncStateStore stateStore;
        private readonly ReadingSegmentMappingStore segmentStore;
        private readonly AniListRateLimitGate gate = new();

        private SyncFixture(string root, AppDbContext db)
        {
            this.root = root;
            Db = db;
            accountStore = new AniListAccountStore(
                new EphemeralDataProtectionProvider(),
                NullLogger<AniListAccountStore>.Instance,
                new DirectoryInfo(root));
            stateStore = new AniListSyncStateStore(
                NullLogger<AniListSyncStateStore>.Instance,
                new DirectoryInfo(root));
            var mappingDirectory = new DirectoryInfo(Path.Combine(root, "anilist"));
            ReviewStore = new MediaMappingReviewStore(
                NullLogger<MediaMappingReviewStore>.Instance,
                mappingDirectory);
            segmentStore = new ReadingSegmentMappingStore(
                NullLogger<ReadingSegmentMappingStore>.Instance,
                mappingDirectory);
        }

        public AppDbContext Db { get; }
        public MediaMappingReviewStore ReviewStore { get; }
        public SyncRemote Remote { get; } = new();
        public SyncTime Time { get; } = new(new DateTimeOffset(
            DateTime.UtcNow.Ticks - (DateTime.UtcNow.Ticks % TimeSpan.TicksPerSecond),
            TimeSpan.Zero));

        public static async Task<SyncFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "jularr-tests",
                $"anilist-sync-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(root, "anilist"));
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "jularr.db")};Foreign Keys=True")
                .Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            foreach (var (id, role) in new[] { (Owner, AccountRole.Owner), (Learner, AccountRole.User) })
            {
                db.OwnerAccounts.Add(new OwnerAccount
                {
                    Id = id,
                    UserName = id,
                    NormalizedUserName = id.ToUpperInvariant(),
                    PasswordHash = "hash",
                    Role = role
                });
            }

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new SyncFixture(root, db);
        }

        public Task<AniListSyncRunSummary> RunAsync() =>
            new AniListSyncReconciler(
                    Db,
                    accountStore,
                    stateStore,
                    gate,
                    Service,
                    Time,
                    NullLogger.Instance)
                .RunOnceAsync(CancellationToken.None);

        public AniListSyncService Overview(string profileId) =>
            new(accountStore, stateStore, gate, AniListSyncReconciler.ProfileAccount(profileId), Time);

        public Task<AniListSyncState> State(string profileId) =>
            stateStore.LoadAsync(profileId, profileId == Owner ? 42 : 77, CancellationToken.None);

        public Task ConnectAsync(string profileId, int viewerId, string token)
        {
            Remote.Viewer(token, viewerId);
            return accountStore.SaveAsync(
                profileId,
                new StoredAniListAccount(
                    12345,
                    viewerId,
                    $"viewer-{viewerId}",
                    null,
                    token,
                    Time.GetUtcNow(),
                    null),
                CancellationToken.None);
        }

        public async Task EnableAsync(string profileId, AniListSyncMode mode) =>
            Assert.IsTrue(await accountStore.UpdateSyncModeAsync(
                profileId,
                mode,
                Time.GetUtcNow().AddHours(-1),
                CancellationToken.None));

        public async Task<TestAnime> AddAnimeAsync(string title, int aniListId, int episodes)
        {
            var anime = new Anime { Key = Guid.NewGuid().ToString("N"), Title = title };
            Db.Add(anime);
            var episodeIds = new List<Guid>();
            for (var number = 1; number <= episodes; number++)
            {
                var episode = new Episode
                {
                    AnimeId = anime.Id,
                    SeasonNumber = 1,
                    Number = number,
                    Title = $"Episode {number}"
                };
                episodeIds.Add(episode.Id);
                Db.Add(episode);
            }

            Db.Add(new AnimeMetadata
            {
                AnimeId = anime.Id,
                Provider = AniListMetadataProvider.ProviderKey,
                ExternalId = aniListId.ToString(CultureInfo.InvariantCulture),
                PreferredTitle = title,
                EpisodeCount = episodes
            });

            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return new TestAnime(anime.Id, episodeIds);
        }

        public async Task WatchAsync(string profileId, TestAnime anime, int number, bool completed, DateTime updatedAt)
        {
            var episodeId = anime.Episodes[number - 1];
            // Watching proceeds in order: finishing episode N means episodes 1..N-1 were finished before it.
            if (completed)
            {
                foreach (var earlier in anime.Episodes.Take(number - 1))
                {
                    await CanonicalProgressSeed.SetAsync(Db, profileId, earlier, 0, null, true, updatedAt.AddMinutes(-1));
                }
            }

            await CanonicalProgressSeed.SetAsync(Db, profileId, episodeId, completed ? 1_400_000 : 600_000, 1_440_000, completed, updatedAt);
            Db.ChangeTracker.Clear();
        }

        public async Task<Guid> AddMangaAsync(string title, int? aniListId, int chapters)
        {
            var repository = new MangaRepository(Db);
            var seriesId = Guid.NewGuid();
            await repository.UpsertSeriesAsync(
                seriesId,
                title,
                Path.Combine(root, "manga", seriesId.ToString("N")),
                CancellationToken.None);

            for (var number = 1; number <= chapters; number++)
            {
                await repository.UpsertChapterAsync(
                    new MangaChapterItem(
                        Guid.NewGuid(),
                        seriesId,
                        number,
                        null,
                        $"Chapter {number}",
                        10,
                        "folder",
                        DateTime.UtcNow),
                    Path.Combine(root, "manga", seriesId.ToString("N"), number.ToString(CultureInfo.InvariantCulture)),
                    CancellationToken.None);
            }

            if (aniListId is int id)
            {
                await repository.UpdateMetadataAsync(
                    seriesId,
                    new MangaAniListCandidate(id.ToString(CultureInfo.InvariantCulture), title, null, null, null, null, null),
                    CancellationToken.None);
            }

            return seriesId;
        }

        public async Task ReadMangaAsync(string profileId, Guid seriesId, int chapter, int page, DateTime updatedAt)
        {
            var repository = new MangaRepository(Db);
            var item = (await repository.GetChaptersAsync(seriesId, CancellationToken.None))
                .Single(x => x.Number == chapter);
            var read = await repository.GetChapterAsync(item.Id, CancellationToken.None);
            await repository.SaveProgressAsync(profileId, read!, page, CancellationToken.None);
            var stamp = updatedAt.ToString("O", CultureInfo.InvariantCulture);
            await Db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE "MangaProgress" SET "UpdatedAt" = {stamp}
                WHERE "ProfileId" = {profileId} AND "SeriesId" = {seriesId.ToString()}
                """);
        }

        public async Task<Guid> AddNovelAsync(string title, int aniListId, int chapters)
        {
            var work = new NovelWork
            {
                SourceProvider = "test",
                SourceKey = Guid.NewGuid().ToString("N"),
                SourceUrl = "https://example.invalid/novel",
                Title = title,
                MetadataProvider = "anilist",
                MetadataExternalId = aniListId.ToString(CultureInfo.InvariantCulture),
                MetadataTitle = title
            };
            Db.Add(work);
            var volume = new NovelVolume { WorkId = work.Id, Number = 1, SourceKey = "web" };
            Db.Add(volume);

            for (var number = 1; number <= chapters; number++)
            {
                Db.Add(new NovelChapter
                {
                    WorkId = work.Id,
                    VolumeId = volume.Id,
                    Number = number,
                    SourceUrl = $"https://example.invalid/novel/{number}",
                    Title = $"Chapter {number}",
                    OriginalText = "本文",
                    SourceHash = Guid.NewGuid().ToString("N")
                });
            }

            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return work.Id;
        }

        public async Task ReadNovelAsync(string profileId, Guid workId, int chapter, int permille, DateTime updatedAt)
        {
            var chapterId = await Db.NovelChapters
                .Where(x => x.WorkId == workId && x.Number == chapter)
                .Select(x => x.Id)
                .SingleAsync();
            var progress = await Db.NovelProgress.SingleOrDefaultAsync(
                x => x.ProfileId == profileId && x.WorkId == workId);
            if (progress is null)
            {
                progress = new NovelProgress { ProfileId = profileId, WorkId = workId };
                Db.Add(progress);
            }

            progress.ChapterId = chapterId;
            progress.PositionPermille = permille;
            progress.UpdatedAt = updatedAt;
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public AniListAccountService Service(string profileId) =>
            new(
                new HttpClient(
                    new AniListRateLimitHandler(gate, Time) { InnerHandler = Remote },
                    disposeHandler: false)
                {
                    BaseAddress = new Uri("https://graphql.anilist.co/")
                },
                accountStore,
                Db,
                new AnimeMetadataService(Db, [], accountStore, ReviewStore),
                segmentStore,
                ReviewStore,
                AniListSyncReconciler.ProfileAccount(profileId),
                NullLogger<AniListAccountService>.Instance,
                Time);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
