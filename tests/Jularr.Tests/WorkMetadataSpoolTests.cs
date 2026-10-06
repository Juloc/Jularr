using System.Net;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// The background metadata spool of #820 slice 1: TMDB metadata and artwork of a durable Movie/Series Work are persisted once,
/// refreshed without duplicates, never overwrite an owner's correction and survive provider failures.
/// </summary>
[TestClass]
public sealed class WorkMetadataSpoolTests
{
    [TestMethod]
    public async Task IngestPersistsOneRowSetAndARerunNeitherDuplicatesNorDownloadsAgain()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var work = await fixture.AddMovieAsync();
        await fixture.Queue.RequestMetadataRefreshAsync(work.Id, interactive: false, CancellationToken.None);

        Assert.AreEqual(new WorkMetadataPass(null, null), await fixture.RunSpoolAsync(), "Nothing is left once the only entry ran.");

        var facts = await fixture.Db.Set<WorkMetadataFacts>().AsNoTracking().SingleAsync();
        Assert.AreEqual(work.Id, facts.WorkId);
        Assert.AreEqual(139, facts.RuntimeMinutes);
        Assert.AreEqual(new DateOnly(1999, 10, 15), facts.ReleaseDate);
        Assert.AreEqual(8.4, facts.Rating);
        Assert.AreEqual("R", facts.Certification, "The certification of the locale's region (en -> US), not another country's.");
        Assert.AreEqual("US", facts.CertificationCountry);
        CollectionAssert.AreEqual(new[] { "Regency Enterprises", "Fox 2000 Pictures" }, facts.Studios);
        CollectionAssert.AreEqual(new[] { "US" }, facts.ProductionCountries);

        var values = await fixture.Db.Set<WorkLocalizedValue>().AsNoTracking().OrderBy(x => x.Field).ThenBy(x => x.Position).ToListAsync();
        Assert.IsTrue(values.All(x => x.Locale == WorkMetadataLocales.InstanceDefault && x.Origin == WorkMetadataValueOrigin.ProviderLocalized && x.Source == "tmdb"));
        CollectionAssert.AreEqual(
            new[] { "Fight Club", "An insomniac meets a soap maker.", "Mischief. Mayhem. Soap.", "Drama", "Thriller", "BdJKm16Co6M" },
            values.Select(x => x.Value).ToArray(),
            "Only the valid YouTube trailer survives the adapter.");
        Assert.AreEqual(3, await fixture.Db.Set<WorkCredit>().CountAsync(), "Two cast members and the director; other crew jobs are not kept.");

        var artwork = await fixture.Db.Set<WorkArtwork>().AsNoTracking().OrderBy(x => x.Slot).ThenBy(x => x.Language).ToListAsync();
        CollectionAssert.AreEqual(
            new[] { "Poster::/poster-neutral.jpg", "Poster:en:/poster-en-best.jpg", "Backdrop::/backdrop-neutral-best.jpg", "Logo:en:/logo-en.png" },
            artwork.Select(x => $"{x.Slot}:{x.Language}:{x.ProviderFilePath}").ToArray());
        Assert.IsTrue(artwork.All(x => x.CacheKey is not null && File.Exists(fixture.Cache.PathFor(x.CacheKey))));
        Assert.AreEqual(4, fixture.CdnRequests.Count);
        Assert.IsTrue(fixture.CdnRequests.All(x => x.StartsWith("/t/p/", StringComparison.Ordinal)));

        var entry = await fixture.RefreshEntryAsync(work.Id);
        Assert.AreEqual(WorkMetadataRefreshStatus.Fresh, entry.Status);
        Assert.AreEqual(WorkMetadataRefreshPriority.Stale, entry.Priority);
        Assert.AreEqual(fixture.Clock.GetUtcNow().UtcDateTime + WorkMetadataRefresher.StaleAfter, entry.NextAttemptAt);
        Assert.IsNull(entry.LastError);

        fixture.Clock.Advance(WorkMetadataRefresher.StaleAfter + TimeSpan.FromDays(1));
        await fixture.RunSpoolAsync();

        Assert.AreEqual(2, fixture.TmdbRequests.Count, "The stale entry was refreshed.");
        Assert.AreEqual(1, await fixture.Db.Set<WorkMetadataFacts>().CountAsync());
        Assert.AreEqual(6, await fixture.Db.Set<WorkLocalizedValue>().CountAsync());
        Assert.AreEqual(3, await fixture.Db.Set<WorkCredit>().CountAsync());
        Assert.AreEqual(4, await fixture.Db.Set<WorkArtwork>().CountAsync());
        Assert.AreEqual(4, fixture.CdnRequests.Count, "Unchanged artwork whose derivative exists is not downloaded again.");
        Assert.AreEqual(1, await fixture.Db.Set<WorkMetadataRefresh>().CountAsync());
    }

    [TestMethod]
    public async Task OversizedProviderTextIsClippedInsteadOfFailingTheRefresh()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var work = await fixture.AddMovieAsync();
        await fixture.Queue.RequestMetadataRefreshAsync(work.Id, interactive: false, CancellationToken.None);
        var longCharacter = new string('x', 900);
        fixture.Tmdb = _ => WorkMetadataFixture.Json(WorkMetadataFixture.MovieJson().Replace("\"Narrator\"", $"\"{longCharacter}\"", StringComparison.Ordinal));

        await fixture.RunSpoolAsync();

        Assert.AreEqual(WorkMetadataRefreshStatus.Fresh, (await fixture.RefreshEntryAsync(work.Id)).Status);
        Assert.AreEqual(300, (await fixture.Db.Set<WorkCredit>().AsNoTracking().SingleAsync(x => x.Name == "Edward Norton")).Role!.Length);
    }

    [TestMethod]
    public async Task ATrailerKeyWithATrailingNewlineIsNotStored()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var work = await fixture.AddMovieAsync();
        await fixture.Queue.RequestMetadataRefreshAsync(work.Id, interactive: false, CancellationToken.None);
        fixture.Tmdb = _ => WorkMetadataFixture.Json(WorkMetadataFixture.MovieJson().Replace("\"BdJKm16Co6M\"", "\"BdJKm16Co6M\\n\"", StringComparison.Ordinal));

        await fixture.RunSpoolAsync();

        Assert.AreEqual(WorkMetadataRefreshStatus.Fresh, (await fixture.RefreshEntryAsync(work.Id)).Status);
        Assert.IsFalse(await fixture.Db.Set<WorkLocalizedValue>().AnyAsync(x => x.Field == WorkLocalizedField.Trailer));
    }

    [TestMethod]
    public async Task AClaimCountsTheAttemptSoARunThatKillsTheProcessBacksOff()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var work = await fixture.AddMovieAsync();
        await fixture.Queue.RequestMetadataRefreshAsync(work.Id, interactive: false, CancellationToken.None);
        var now = fixture.Clock.GetUtcNow().UtcDateTime;
        var lease = TimeSpan.FromMinutes(10);
        WorkMediaType[] types = [WorkMediaType.Movie];

        // Every claim below is a run that died with the process: nothing records its outcome.
        var first = await fixture.Store.ClaimNextDueAsync(types, now, lease, WorkMetadataRefresher.BaseBackoff, WorkMetadataRefresher.MaxBackoff, CancellationToken.None);
        Assert.AreEqual(1, first!.Attempts);
        Assert.AreEqual(now + lease, (await fixture.RefreshEntryAsync(work.Id)).NextAttemptAt, "Early crashes wait for the lease.");

        await fixture.Db.Database.ExecuteSqlAsync($"""UPDATE "WorkMetadataRefreshes" SET "Attempts" = 6, "NextAttemptAt" = {now}""");
        var later = await fixture.Store.ClaimNextDueAsync(types, now, lease, WorkMetadataRefresher.BaseBackoff, WorkMetadataRefresher.MaxBackoff, CancellationToken.None);
        Assert.AreEqual(7, later!.Attempts);
        Assert.AreEqual(now + WorkMetadataRefresher.Backoff(7), (await fixture.RefreshEntryAsync(work.Id)).NextAttemptAt, "A crash loop backs off like any failure.");

        fixture.Clock.Advance(WorkMetadataRefresher.Backoff(7));
        await fixture.RunSpoolAsync();
        var entry = await fixture.RefreshEntryAsync(work.Id);
        Assert.AreEqual(WorkMetadataRefreshStatus.Fresh, entry.Status);
        Assert.AreEqual(0, entry.Attempts, "A successful run clears the count.");
    }

    [TestMethod]
    public async Task AWakeDoesNotCutAProviderPauseShort()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var first = await fixture.AddMovieAsync("550", "First");
        var second = await fixture.AddMovieAsync("551", "Second");
        await fixture.Queue.RequestMetadataRefreshAsync(first.Id, interactive: true, CancellationToken.None);
        await fixture.Queue.RequestMetadataRefreshAsync(second.Id, interactive: false, CancellationToken.None);
        var answered = 0;
        fixture.Tmdb = _ =>
        {
            if (Interlocked.Increment(ref answered) > 1)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(4));
            return limited;
        };
        var signal = fixture.Services.GetRequiredService<WorkMetadataRefreshSignal>();
        var service = new WorkMetadataRefreshService(fixture.Services.GetRequiredService<IServiceScopeFactory>(), signal, TimeProvider.System, NullLogger<WorkMetadataRefreshService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            signal.Wake();
            await WaitUntilAsync(() => Volatile.Read(ref answered) >= 1, TimeSpan.FromSeconds(20));
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Requests keep waking the worker while the provider asked for a pause: no further provider call may happen.
            for (var wake = 0; wake < 5; wake++)
            {
                signal.Wake();
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }

            Assert.AreEqual(1, Volatile.Read(ref answered), "A wake ended the provider pause.");
            await WaitUntilAsync(() => Volatile.Read(ref answered) >= 2, TimeSpan.FromSeconds(20));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "The condition was not reached in time.");
            await Task.Delay(50);
        }
    }

    [TestMethod]
    public async Task FreshMetadataIsNotRefetchedWhenAWorkIsOpenedAgain()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var work = await fixture.AddMovieAsync();
        await fixture.Queue.RequestMetadataRefreshAsync(work.Id, interactive: false, CancellationToken.None);
        await fixture.RunSpoolAsync();

        await fixture.Queue.RequestMetadataRefreshAsync(work.Id, interactive: true, CancellationToken.None);
        await fixture.RunSpoolAsync();

        Assert.AreEqual(1, fixture.TmdbRequests.Count);
        Assert.AreEqual(WorkMetadataRefreshStatus.Fresh, (await fixture.RefreshEntryAsync(work.Id)).Status);
    }

    [TestMethod]
    public async Task OwnerCorrectionsSurviveARefresh()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var work = await fixture.AddMovieAsync();
        await fixture.Queue.RequestMetadataRefreshAsync(work.Id, interactive: false, CancellationToken.None);
        await fixture.RunSpoolAsync();

        // The owner corrected the runtime (field provenance pin), the synopsis (localized manual value) and chose the English poster.
        await new WorkService(fixture.Db).SetManualFieldOverrideAsync(work.Id, WorkMetadataFactFields.Runtime, CancellationToken.None);
        await fixture.Db.Database.ExecuteSqlAsync($"""UPDATE "WorkMetadataFacts" SET "RuntimeMinutes" = 151 WHERE "WorkId" = {work.Id}""");
        await fixture.Db.Database.ExecuteSqlAsync(
            $"""
            UPDATE "WorkLocalizedValues" SET "Value" = 'Owner synopsis', "Origin" = 1, "Source" = 'owner', "IsManualOverride" = TRUE
            WHERE "WorkId" = {work.Id} AND "Field" = {(int)WorkLocalizedField.Overview}
            """);
        await fixture.Db.Database.ExecuteSqlAsync($"""UPDATE "WorkArtwork" SET "IsManualOverride" = TRUE WHERE "WorkId" = {work.Id} AND "Slot" = 0 AND "Language" = 'en'""");

        fixture.Tmdb = _ => WorkMetadataFixture.Json(WorkMetadataFixture.MovieJson(overview: "Provider synopsis", runtime: 140, tagline: "New tagline", bestEnglishPoster: "/poster-en-new.jpg"));
        await fixture.MakeDueAsync();
        await fixture.RunSpoolAsync();

        Assert.AreEqual(151, (await fixture.Store.LoadFactsAsync(work.Id, CancellationToken.None))!.RuntimeMinutes);
        var values = await fixture.Db.Set<WorkLocalizedValue>().AsNoTracking().ToListAsync();
        Assert.AreEqual("Owner synopsis", values.Single(x => x.Field == WorkLocalizedField.Overview).Value);
        Assert.AreEqual("New tagline", values.Single(x => x.Field == WorkLocalizedField.Tagline).Value, "Unprotected fields still refresh.");
        Assert.AreEqual("/poster-en-best.jpg", (await fixture.Db.Set<WorkArtwork>().AsNoTracking().SingleAsync(x => x.Slot == WorkArtworkSlot.Poster && x.Language == "en")).ProviderFilePath);
    }

    [TestMethod]
    public async Task AProviderOutageKeepsTheLastKnownMetadataBacksOffAndThenPausesOnTheOpenCircuit()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var work = await fixture.AddMovieAsync();
        await fixture.Queue.RequestMetadataRefreshAsync(work.Id, interactive: false, CancellationToken.None);
        await fixture.RunSpoolAsync();

        fixture.Tmdb = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        await fixture.MakeDueAsync();
        Assert.AreEqual(new WorkMetadataPass(null, WorkMetadataRefresher.Backoff(1)), await fixture.RunSpoolAsync(), "The worker wakes when the backoff ends, not an interval later.");

        var entry = await fixture.RefreshEntryAsync(work.Id);
        Assert.AreEqual(WorkMetadataRefreshStatus.Queued, entry.Status, "A transient failure is retried.");
        Assert.AreEqual(1, entry.Attempts);
        Assert.AreEqual(fixture.Clock.GetUtcNow().UtcDateTime + WorkMetadataRefresher.Backoff(1), entry.NextAttemptAt);
        StringAssert.Contains(entry.LastError, "503");
        Assert.IsFalse(entry.LastError!.Contains("secret-test-key", StringComparison.Ordinal), "Diagnostics never carry the provider credentials.");
        Assert.AreEqual("An insomniac meets a soap maker.", (await fixture.Db.Set<WorkLocalizedValue>().AsNoTracking().SingleAsync(x => x.Field == WorkLocalizedField.Overview)).Value);
        Assert.AreEqual(4, await fixture.Db.Set<WorkArtwork>().CountAsync(x => x.CacheKey != null));

        // Three failed attempts opened the provider circuit: the next run pauses the spool without counting against the entry.
        await fixture.MakeDueAsync();
        Assert.AreEqual(new WorkMetadataPass(WorkMetadataRefresher.UnavailablePause, null), await fixture.RunSpoolAsync());
        Assert.AreEqual(1, (await fixture.RefreshEntryAsync(work.Id)).Attempts);
    }

    [TestMethod]
    public async Task TransientFailuresBackOffExponentiallyUpToTheCap()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(1), WorkMetadataRefresher.Backoff(1));
        Assert.AreEqual(TimeSpan.FromMinutes(2), WorkMetadataRefresher.Backoff(2));
        Assert.AreEqual(TimeSpan.FromMinutes(8), WorkMetadataRefresher.Backoff(4));
        Assert.AreEqual(WorkMetadataRefresher.MaxBackoff, WorkMetadataRefresher.Backoff(30));

        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var work = await fixture.AddMovieAsync();
        await fixture.Queue.RequestMetadataRefreshAsync(work.Id, interactive: false, CancellationToken.None);
        fixture.Cdn = _ => throw new HttpRequestException("CDN connection reset");

        await fixture.RunSpoolAsync();

        var entry = await fixture.RefreshEntryAsync(work.Id);
        Assert.AreEqual(WorkMetadataRefreshStatus.Queued, entry.Status, "Text is stored; the artwork download is retried.");
        Assert.AreEqual(1, entry.Attempts);
        StringAssert.Contains(entry.LastError, "CDN connection reset");
        Assert.AreEqual(6, await fixture.Db.Set<WorkLocalizedValue>().CountAsync());
        Assert.AreEqual(0, await fixture.Db.Set<WorkArtwork>().CountAsync());

        fixture.Cdn = _ => WorkMetadataFixture.Png();
        fixture.Clock.Advance(WorkMetadataRefresher.Backoff(1));
        await fixture.RunSpoolAsync();

        entry = await fixture.RefreshEntryAsync(work.Id);
        Assert.AreEqual(WorkMetadataRefreshStatus.Fresh, entry.Status);
        Assert.AreEqual(0, entry.Attempts);
        Assert.AreEqual(4, await fixture.Db.Set<WorkArtwork>().CountAsync());
    }

    [TestMethod]
    public async Task AnUnknownTitleIsRecordedAsAPermanentFailureAndReprobedMuchLater()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var work = await fixture.AddMovieAsync(tmdbId: "999999");
        await fixture.Queue.RequestMetadataRefreshAsync(work.Id, interactive: false, CancellationToken.None);

        await fixture.RunSpoolAsync();

        var entry = await fixture.RefreshEntryAsync(work.Id);
        Assert.AreEqual(WorkMetadataRefreshStatus.Failed, entry.Status);
        Assert.AreEqual(fixture.Clock.GetUtcNow().UtcDateTime + WorkMetadataRefresher.PermanentFailureRecheck, entry.NextAttemptAt);
        StringAssert.Contains(entry.LastError, "404");
        Assert.AreEqual(0, await fixture.Db.Set<WorkMetadataFacts>().CountAsync());

        await fixture.Queue.RequestMetadataRefreshAsync(work.Id, interactive: true, CancellationToken.None);
        await fixture.RunSpoolAsync();
        Assert.AreEqual(1, fixture.TmdbRequests.Count, "Opening the Work does not bypass the negative result.");
    }

    [TestMethod]
    public async Task ARateLimitPausesTheSpoolWithoutPenalizingTheEntry()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var work = await fixture.AddMovieAsync();
        await fixture.Queue.RequestMetadataRefreshAsync(work.Id, interactive: false, CancellationToken.None);
        fixture.Tmdb = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        };

        var pause = (await fixture.RunSpoolAsync()).ProviderPause;

        Assert.IsNotNull(pause);
        Assert.IsTrue(pause > TimeSpan.FromSeconds(60), $"The provider's Retry-After is honoured, got {pause}.");
        var entry = await fixture.RefreshEntryAsync(work.Id);
        Assert.AreEqual(0, entry.Attempts);
        Assert.AreEqual(WorkMetadataRefreshStatus.Queued, entry.Status);
    }

    [TestMethod]
    public async Task TheBackfillQueuesDurableWorksByPriorityOnceAndAnOpenedWorkJumpsTheQueue()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var works = new WorkService(fixture.Db);
        var imported = await fixture.AddMovieAsync("1", "Imported");
        await seed.AddVideoAsync(imported, null);
        var requested = await fixture.AddMovieAsync("2", "Requested");
        var library = await works.EnsureWorkByExternalIdentityAsync(WorkMediaType.Series, "tmdb", "3", "Library", null, CancellationToken.None);
        await works.LinkSourceAsync(library.Id, WorkSourceKind.Series, Guid.NewGuid(), CancellationToken.None);
        await works.EnsureWorkByExternalIdentityAsync(WorkMediaType.Anime, "tmdb", "4", "Anime", null, CancellationToken.None);
        await seed.AddWorkAsync(WorkMediaType.Movie, "No identity");

        var now = fixture.Clock.GetUtcNow().UtcDateTime;
        Assert.AreEqual(3, await fixture.Store.EnqueueDurableWorksAsync(WorkMetadataLocales.InstanceDefault, now, CancellationToken.None));
        Assert.AreEqual(0, await fixture.Store.EnqueueDurableWorksAsync(WorkMetadataLocales.InstanceDefault, now, CancellationToken.None), "A rerun adds nothing.");

        var priorities = await fixture.Db.Set<WorkMetadataRefresh>().AsNoTracking().ToDictionaryAsync(x => x.WorkId, x => x.Priority);
        Assert.AreEqual(WorkMetadataRefreshPriority.Imported, priorities[imported.Id]);
        Assert.AreEqual(WorkMetadataRefreshPriority.Requested, priorities[requested.Id]);
        Assert.AreEqual(WorkMetadataRefreshPriority.Library, priorities[library.Id]);

        await fixture.Queue.RequestMetadataRefreshAsync(library.Id, interactive: true, CancellationToken.None);

        var lease = TimeSpan.FromMinutes(10);
        var order = new List<Guid>();
        while (await fixture.Store.ClaimNextDueAsync([WorkMediaType.Movie, WorkMediaType.Series], now, lease, WorkMetadataRefresher.BaseBackoff, WorkMetadataRefresher.MaxBackoff, CancellationToken.None) is { } claim)
        {
            order.Add(claim.WorkId);
        }

        CollectionAssert.AreEqual(new[] { library.Id, requested.Id, imported.Id }, order, "Opened, then requested, then imported; a leased entry is not claimed twice.");
    }

    [TestMethod]
    public async Task TheSpoolOnlyClaimsWorksOfEnabledMediaTypes()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var work = await fixture.AddMovieAsync();
        await fixture.Queue.RequestMetadataRefreshAsync(work.Id, interactive: false, CancellationToken.None);
        var now = fixture.Clock.GetUtcNow().UtcDateTime;

        Assert.IsNull(await fixture.Store.ClaimNextDueAsync([WorkMediaType.Series], now, TimeSpan.FromMinutes(10), WorkMetadataRefresher.BaseBackoff, WorkMetadataRefresher.MaxBackoff, CancellationToken.None));
        Assert.AreEqual(WorkMetadataRefreshStatus.Queued, (await fixture.RefreshEntryAsync(work.Id)).Status);
    }

    [TestMethod]
    public async Task MaterializingATmdbWorkQueuesItAtRequestPriority()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        fixture.Tmdb = _ => WorkMetadataFixture.Json("""{ "title": "Fight Club", "original_title": "Fight Club", "original_language": "en", "release_date": "1999-10-15" }""");
        var tmdb = fixture.Services.GetRequiredService<TmdbDiscoveryProvider>();

        var work = await tmdb.EnsureCanonicalWorkAsync(TmdbDiscoveryMediaType.Movie, "550", CancellationToken.None);

        var entry = await fixture.RefreshEntryAsync(work.Id);
        Assert.AreEqual(WorkMetadataRefreshPriority.Requested, entry.Priority);
        Assert.AreEqual(WorkMetadataLocales.InstanceDefault, entry.Locale);
        Assert.AreEqual(0, await fixture.Db.Set<WorkLocalizedValue>().CountAsync(), "The request itself never fetches durable metadata.");
    }

    [TestMethod]
    public async Task MergingTwoWorksKeepsTheSurvivorsMetadataAndMovesWhatOnlyTheAbsorbedOneHas()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var survivor = await fixture.AddMovieAsync("550", "Fight Club");
        var absorbed = await fixture.AddMovieAsync("551", "Fight Club (dup)");
        var now = fixture.Clock.GetUtcNow().UtcDateTime;
        await fixture.Store.ReplaceLocalizedFieldAsync(survivor.Id, "en", WorkLocalizedField.Overview, ["Survivor synopsis"], "tmdb", "550", 10, now, CancellationToken.None);
        await fixture.Store.ReplaceLocalizedFieldAsync(absorbed.Id, "en", WorkLocalizedField.Overview, ["Absorbed synopsis"], "tmdb", "551", 10, now, CancellationToken.None);
        await fixture.Store.ReplaceLocalizedFieldAsync(absorbed.Id, "en", WorkLocalizedField.Tagline, ["Absorbed tagline"], "tmdb", "551", 10, now, CancellationToken.None);
        await fixture.Store.UpsertFactsAsync(new WorkMetadataFacts { WorkId = absorbed.Id, RuntimeMinutes = 139, UpdatedAt = now }, CancellationToken.None);
        await fixture.Queue.RequestMetadataRefreshAsync(survivor.Id, interactive: false, CancellationToken.None);
        await fixture.Queue.RequestMetadataRefreshAsync(absorbed.Id, interactive: false, CancellationToken.None);

        await new WorkService(fixture.Db).MergeWorksAsync(survivor.Id, absorbed.Id, "owner", CancellationToken.None);

        var values = await fixture.Db.Set<WorkLocalizedValue>().AsNoTracking().ToListAsync();
        Assert.IsTrue(values.All(x => x.WorkId == survivor.Id));
        Assert.AreEqual("Survivor synopsis", values.Single(x => x.Field == WorkLocalizedField.Overview).Value);
        Assert.AreEqual("Absorbed tagline", values.Single(x => x.Field == WorkLocalizedField.Tagline).Value);
        Assert.AreEqual(139, (await fixture.Store.LoadFactsAsync(survivor.Id, CancellationToken.None))!.RuntimeMinutes);
        Assert.AreEqual(1, await fixture.Db.Set<WorkMetadataRefresh>().CountAsync());
        Assert.IsFalse(await fixture.Db.Works.AnyAsync(x => x.Id == absorbed.Id));
    }

    [TestMethod]
    public async Task TheReconcileBackfillsAndSweepsOnlyOrphanedArtworkFiles()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var work = await fixture.AddMovieAsync();
        await fixture.Store.EnqueueDurableWorksAsync(WorkMetadataLocales.InstanceDefault, fixture.Clock.GetUtcNow().UtcDateTime, CancellationToken.None);
        await fixture.RunSpoolAsync();
        var referenced = (await fixture.Db.Set<WorkArtwork>().AsNoTracking().FirstAsync()).CacheKey!;
        var orphan = WorkArtworkCache.CacheKey(work.Id, WorkArtworkSlot.Poster, "fr", "tmdb", "/gone.jpg");
        var orphanPath = fixture.Cache.PathFor(orphan)!;
        Directory.CreateDirectory(Path.GetDirectoryName(orphanPath)!);
        await File.WriteAllBytesAsync(orphanPath, [1]);
        foreach (var file in Directory.EnumerateFiles(fixture.CacheRoot, "*.webp", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, fixture.Clock.GetUtcNow().UtcDateTime.AddHours(-1));
        }

        await WorkMetadataRefreshService.ReconcileAsync(fixture.Services, CancellationToken.None);

        Assert.IsFalse(File.Exists(orphanPath));
        Assert.IsTrue(File.Exists(fixture.Cache.PathFor(referenced)));
    }
}
