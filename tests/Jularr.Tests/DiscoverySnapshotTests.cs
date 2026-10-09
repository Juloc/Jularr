using Jularr.Web.Data;
using Jularr.Web.Features.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// The local discovery snapshot: the last successful answer of a browse source survives a restart and a provider outage, is bounded, never keeps a search and
/// never replaces a newer answer, so the first response after a restart already has titles (stale-while-revalidate).
/// </summary>
[TestClass]
public sealed class DiscoverySnapshotTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<DiscoveryItem> Titles(params string[] titles) =>
        [.. titles.Select(title => new DiscoveryItem($"tmdb:movie:{title}", "movie", "tmdb", title, title, null, "Overview", "https://image.tmdb.org/t/p/w500/x.jpg", null, null, 2026, null, null, null, null, ["Drama"], false, null, "/d", false, Rating: 7.5, BackdropUrl: "https://image.tmdb.org/t/p/w1280/y.jpg"))];

    private static async Task<(DbContextOptions<AppDbContext> Options, DiscoverySourceFlights Flights)> CreateAsync(DiscoveryTestSupport.MovableClock clock)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source=discovery-snapshot-{Guid.NewGuid():N}.db;Foreign Keys=True").Options;
        await using (var migrate = new AppDbContext(options))
        {
            await DatabaseMigrationBridge.UpgradeAsync(migrate);
        }

        return (options, FlightsOn(options, clock));
    }

    private static DiscoverySourceFlights FlightsOn(DbContextOptions<AppDbContext> options, DiscoveryTestSupport.MovableClock clock)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(options));
        services.AddScoped<DiscoverySnapshotStore>();
        return new DiscoverySourceFlights(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), clock, new DiscoveryTestSupport.IdleLifetime(), NullLogger<DiscoverySourceFlights>.Instance);
    }

    private static async Task<int> RowsAsync(DbContextOptions<AppDbContext> options)
    {
        await using var db = new AppDbContext(options);
        return await db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "DiscoverySnapshots" """).SingleAsync();
    }

    private static async Task WaitForRowsAsync(DbContextOptions<AppDbContext> options, int rows)
    {
        for (var attempt = 0; attempt < 100 && await RowsAsync(options) < rows; attempt++)
        {
            await Task.Delay(50);
        }
    }

    [TestMethod]
    public async Task AnAnswerSurvivesARestartAndIsServedAtOnceWhileOneCallRenewsIt()
    {
        var clock = new DiscoveryTestSupport.MovableClock(Start);
        var (options, first) = await CreateAsync(clock);
        await first.Start(DiscoverySource.Movies, "movies|Movie|Trending", TimeSpan.FromMinutes(3), false, (_, _) => Task.FromResult(Titles("Dune")), persist: true).Completion;
        await WaitForRowsAsync(options, 1);

        // A restart: a new process has no remembered answer, only the snapshot.
        clock.Advance(TimeSpan.FromHours(2));
        var restarted = FlightsOn(options, clock);
        var calls = 0;
        await restarted.HydrateAsync(CancellationToken.None);
        var stale = restarted.Start(DiscoverySource.Movies, "movies|Movie|Trending", TimeSpan.FromMinutes(3), false, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Titles("Dune", "Arrival"));
        }, persist: true);

        Assert.IsTrue(stale.IsSettled, "The titles are there on the first response, without a provider call.");
        Assert.AreEqual("Dune", stale.Outcome!.Items.Single().Title);
        Assert.AreEqual("https://image.tmdb.org/t/p/w1280/y.jpg", stale.Outcome.Items.Single().BackdropUrl, "The whole item survives the snapshot.");
        await stale.Pending;
        Assert.AreEqual(2, stale.Outcome!.Items.Count, "The renewal replaced the stale answer.");
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task ASnapshotThatTheProviderCannotRenewIsStillShown()
    {
        var clock = new DiscoveryTestSupport.MovableClock(Start);
        var (options, first) = await CreateAsync(clock);
        await first.Start(DiscoverySource.Movies, "movies|Movie|Top", TimeSpan.FromMinutes(3), false, (_, _) => Task.FromResult(Titles("Dune")), persist: true).Completion;
        await WaitForRowsAsync(options, 1);

        clock.Advance(TimeSpan.FromDays(1));
        var restarted = FlightsOn(options, clock);
        await restarted.HydrateAsync(CancellationToken.None);
        var stale = restarted.Start(DiscoverySource.Movies, "movies|Movie|Top", TimeSpan.FromMinutes(3), false, (_, _) => throw new HttpRequestException("TMDB is down"), persist: true);
        await stale.Pending;

        Assert.AreEqual(DiscoverySourceState.Ready, stale.Outcome!.State);
        Assert.AreEqual("Dune", stale.Outcome.Items.Single().Title, "A provider outage after a restart still leaves the last good titles.");
    }

    [TestMethod]
    public async Task ASearchIsNeverPersistedAndTheStoreKeepsOnlyTheNewestRows()
    {
        var clock = new DiscoveryTestSupport.MovableClock(Start);
        var (options, flights) = await CreateAsync(clock);

        await flights.Start(DiscoverySource.Movies, "movies|Movie|Search|dune", TimeSpan.FromSeconds(45), false, (_, _) => Task.FromResult(Titles("Dune")), persist: false).Completion;
        await Task.Delay(300);
        Assert.AreEqual(0, await RowsAsync(options), "What a person typed is not kept.");

        await using var db = new AppDbContext(options);
        var store = new DiscoverySnapshotStore(db);
        for (var index = 0; index < DiscoverySnapshotStore.MaximumRows + 5; index++)
        {
            await store.SaveAsync($"key-{index:D3}", Titles($"Title {index}"), Start.AddMinutes(index), CancellationToken.None);
        }

        await store.PruneAsync(Start.AddMinutes(DiscoverySnapshotStore.MaximumRows + 5), CancellationToken.None);
        var kept = await store.LoadAsync(Start.AddMinutes(DiscoverySnapshotStore.MaximumRows + 5), CancellationToken.None);

        Assert.AreEqual(DiscoverySnapshotStore.MaximumRows, kept.Count);
        Assert.IsFalse(kept.Any(snapshot => snapshot.Key == "key-000"), "The oldest rows go first.");
        Assert.IsTrue(kept.Any(snapshot => snapshot.Key == $"key-{DiscoverySnapshotStore.MaximumRows + 4:D3}"));
    }

    [TestMethod]
    public async Task AnAnswerOlderThanTheMaximumAgeIsNotLoadedAndASnapshotNeverReplacesANewerAnswer()
    {
        var clock = new DiscoveryTestSupport.MovableClock(Start);
        var (options, _) = await CreateAsync(clock);
        await using (var db = new AppDbContext(options))
        {
            var store = new DiscoverySnapshotStore(db);
            await store.SaveAsync("movies|ancient", Titles("Old"), Start.AddDays(-30), CancellationToken.None);
            await store.SaveAsync("movies|recent", Titles("Recent"), Start.AddDays(-1), CancellationToken.None);
        }

        var restarted = FlightsOn(options, clock);
        var fresh = restarted.Start(DiscoverySource.Movies, "movies|recent", TimeSpan.FromMinutes(3), false, (_, _) => Task.FromResult(Titles("Newer")), persist: false);
        await fresh.Completion;
        await restarted.HydrateAsync(CancellationToken.None);
        var same = restarted.Start(DiscoverySource.Movies, "movies|recent", TimeSpan.FromMinutes(3), false, (_, _) => Task.FromResult(Titles("Never")), persist: false);

        Assert.AreSame(fresh, same, "The snapshot added nothing over the answer that was already remembered.");
        Assert.AreEqual("Newer", same.Outcome!.Items.Single().Title);
        Assert.IsFalse((await new DiscoverySnapshotStore(new AppDbContext(options)).LoadAsync(Start, CancellationToken.None)).Any(snapshot => snapshot.Key == "movies|ancient"));
    }
}
