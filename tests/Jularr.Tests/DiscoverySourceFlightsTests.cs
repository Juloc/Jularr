using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

/// <summary>
/// The one owner of Discover provider calls (#595): one call per source shared by every request that needs it, kept while fresh, a failure that is
/// never an empty answer, a viewer's retry that can replace only a settled failure (and not more often than a bounded rate), a bounded number of
/// remembered and of concurrent calls, and a call that can finish after the request that started it.
/// </summary>
[TestClass]
public sealed class DiscoverySourceFlightsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(3);

    private static IReadOnlyList<DiscoveryItem> Titles(params string[] titles) =>
        [.. titles.Select(title => new DiscoveryItem($"anilist:anime:{title}", "anime", "anilist", title, title, null, null, null, null, null, null, null, null, null, null, [], false, null, "/d", false))];

    private static DiscoverySourceFlight Begin(DiscoverySourceFlights flights, string key, DiscoverySourceFetch fetch, bool retry = false, DiscoverySource source = DiscoverySource.Anime) =>
        flights.Start(source, key, Fresh, retry, fetch);

    [TestMethod]
    public async Task RequestsForTheSameSourceShareOneProviderCall()
    {
        var calls = 0;
        var release = new TaskCompletionSource();
        var flights = DiscoveryTestSupport.Flights();

        async Task<IReadOnlyList<DiscoveryItem>> Fetch(IServiceProvider services, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return Titles("Frieren");
        }

        var first = Begin(flights, "anime|trending", Fetch);
        var second = Begin(flights, "anime|trending", Fetch);
        Assert.IsFalse(first.IsSettled, "The call is still running when the second request arrives.");
        release.SetResult();

        Assert.AreSame(first, second);
        Assert.AreEqual(1, (await second.Completion).Items.Count);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task AnAnswerIsReusedWhileItIsFreshAndFetchedAgainOnceItIsOld()
    {
        var clock = new DiscoveryTestSupport.MovableClock(Start);
        var calls = 0;
        var flights = DiscoveryTestSupport.Flights(clock);
        Task<IReadOnlyList<DiscoveryItem>> Fetch(IServiceProvider services, CancellationToken cancellationToken) => Task.FromResult(Titles($"call-{Interlocked.Increment(ref calls)}"));

        await Begin(flights, "books|trending", Fetch).Completion;
        clock.Advance(TimeSpan.FromMinutes(2));
        var fresh = await Begin(flights, "books|trending", Fetch).Completion;
        clock.Advance(TimeSpan.FromMinutes(2));
        var old = await Begin(flights, "books|trending", Fetch).Completion;

        Assert.AreEqual("call-1", fresh.Items[0].Title);
        Assert.AreEqual("call-2", old.Items[0].Title);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task AFailureIsNeverAnEmptyAnswerAndIsForgottenAfterAShortWhile()
    {
        var clock = new DiscoveryTestSupport.MovableClock(Start);
        var calls = 0;
        var flights = DiscoveryTestSupport.Flights(clock);
        Task<IReadOnlyList<DiscoveryItem>> Fetch(IServiceProvider services, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref calls) == 1 ? throw new HttpRequestException("down") : Task.FromResult(Titles("Frieren"));

        var failed = await Begin(flights, "movies|trending", Fetch).Completion;
        var remembered = await Begin(flights, "movies|trending", Fetch).Completion;
        clock.Advance(DiscoverySourceFlights.FailureMemory + TimeSpan.FromSeconds(1));
        var afterTheMemory = await Begin(flights, "movies|trending", Fetch).Completion;

        Assert.AreEqual(DiscoverySourceState.Unavailable, failed.State);
        Assert.AreEqual(0, failed.Items.Count, "A failed source has no titles; it is not an answer of nothing.");
        Assert.AreSame(failed, remembered, "A second request within the memory does not call the provider again.");
        Assert.AreEqual(DiscoverySourceState.Ready, afterTheMemory.State);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task AViewersRetryReplacesOnlyASettledFailureAndNotMoreOftenThanItsInterval()
    {
        var clock = new DiscoveryTestSupport.MovableClock(Start);
        var calls = 0;
        var healthy = false;
        var flights = DiscoveryTestSupport.Flights(clock);
        Task<IReadOnlyList<DiscoveryItem>> Fetch(IServiceProvider services, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref calls) > 0 && !healthy ? throw new HttpRequestException("down") : Task.FromResult(Titles("Frieren"));

        await Begin(flights, "tv|trending", Fetch).Completion;
        var first = await Begin(flights, "tv|trending", Fetch, retry: true).Completion;
        var spam = await Begin(flights, "tv|trending", Fetch, retry: true).Completion;
        var callsAfterSpam = calls;
        clock.Advance(DiscoverySourceFlights.RetryInterval + TimeSpan.FromSeconds(1));
        healthy = true;
        var later = await Begin(flights, "tv|trending", Fetch, retry: true).Completion;

        Assert.AreEqual(DiscoverySourceState.Unavailable, first.State);
        Assert.AreSame(first, spam, "A second retry within the interval changes nothing.");
        Assert.AreEqual(2, callsAfterSpam, "The first call and one retry, however often the viewer pressed the button.");
        Assert.AreEqual(DiscoverySourceState.Ready, later.State);
    }

    [TestMethod]
    public async Task ARetryNeverReplacesAHealthyAnswerOrACallThatIsStillRunning()
    {
        var calls = 0;
        var release = new TaskCompletionSource();
        var flights = DiscoveryTestSupport.Flights();
        async Task<IReadOnlyList<DiscoveryItem>> Slow(IServiceProvider services, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return Titles("Frieren");
        }

        var running = Begin(flights, "anime|top", Slow);
        var retriedWhileRunning = Begin(flights, "anime|top", Slow, retry: true);
        release.SetResult();
        var healthy = await running.Completion;
        var retriedWhenHealthy = Begin(flights, "anime|top", Slow, retry: true);

        Assert.AreSame(running, retriedWhileRunning);
        Assert.AreSame(running, retriedWhenHealthy);
        Assert.AreEqual(DiscoverySourceState.Ready, healthy.State);
        Assert.AreEqual(1, calls, "Pressing retry on rows that are fine or on the way costs the providers nothing.");
    }

    [TestMethod]
    public async Task ARateLimitedOrUnavailableProviderIsBusyAndAnyOtherFailureIsUnavailable()
    {
        var flights = DiscoveryTestSupport.Flights();

        var limited = await Begin(flights, "a", (_, _) => throw new ProviderRateLimitedException("tmdb", TimeSpan.FromSeconds(30))).Completion;
        var open = await Begin(flights, "b", (_, _) => throw new ProviderUnavailableException("tmdb")).Completion;
        var broken = await Begin(flights, "c", (_, _) => throw new InvalidOperationException("bug")).Completion;
        var malformed = await Begin(flights, "d", (_, _) => throw new System.Text.Json.JsonException("not json")).Completion;

        Assert.AreEqual(DiscoverySourceState.Busy, limited.State);
        Assert.AreEqual(DiscoverySourceState.Busy, open.State);
        Assert.AreEqual(DiscoverySourceState.Unavailable, broken.State);
        Assert.AreEqual(DiscoverySourceState.Unavailable, malformed.State, "Whatever a provider throws, the page keeps the other sources.");
    }

    [TestMethod]
    public async Task ACallRunsInItsOwnScopeThatEndsWithItAndStopsWhenTheHostStops()
    {
        var scoped = new ScopedProbe();
        var services = new ServiceCollection().AddScoped(_ => scoped).BuildServiceProvider();
        var lifetime = new DiscoveryTestSupport.StoppableLifetime();
        var flights = new DiscoverySourceFlights(
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            lifetime,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DiscoverySourceFlights>.Instance);

        var finished = await Begin(flights, "scoped", (provider, _) =>
        {
            Assert.AreSame(scoped, provider.GetRequiredService<ScopedProbe>());
            return Task.FromResult(Titles("x"));
        }).Completion;
        var waiting = Begin(flights, "waiting", async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Titles();
        });
        lifetime.StopApplication();
        var stopped = await waiting.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual(DiscoverySourceState.Ready, finished.State);
        Assert.IsTrue(scoped.Disposed, "The scope of a call is disposed when the call ends.");
        Assert.AreEqual(DiscoverySourceState.Unavailable, stopped.State);
    }

    [TestMethod]
    public async Task AProviderThatIgnoresItsTokenEndsAtTheHardTimeoutAndTheOthersGoOn()
    {
        var clock = new DiscoveryTestSupport.MovableClock(Start);
        var flights = DiscoveryTestSupport.Flights(clock);
        var stuck = Begin(flights, "stuck", (_, _) => new TaskCompletionSource<IReadOnlyList<DiscoveryItem>>().Task);
        var fine = await Begin(flights, "fine", (_, _) => Task.FromResult(Titles("ok"))).Completion;

        Assert.IsFalse(stuck.IsSettled);
        clock.Advance(DiscoverySourceFlights.CallTimeout + TimeSpan.FromSeconds(1));
        var timedOut = await stuck.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual(DiscoverySourceState.Unavailable, timedOut.State, "A call that never answers ends as a failure, so its section can offer a retry.");
        Assert.AreEqual(DiscoverySourceState.Ready, fine.State);
    }

    [TestMethod]
    public async Task OnlyAFewCallsPerProviderRunAtOnceAndTheRestWaitForASlot()
    {
        var flights = DiscoveryTestSupport.Flights();
        var running = 0;
        var peak = 0;
        var gate = new TaskCompletionSource();
        async Task<IReadOnlyList<DiscoveryItem>> Fetch(IServiceProvider services, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            do
            {
                seen = peak;
            }
            while (now > seen && Interlocked.CompareExchange(ref peak, now, seen) != seen);
            await gate.Task;
            Interlocked.Decrement(ref running);
            return Titles("x");
        }

        var all = Enumerable.Range(0, 8).Select(index => Begin(flights, $"anime|row-{index}", Fetch, source: index % 2 == 0 ? DiscoverySource.Anime : DiscoverySource.Reading)).ToArray();
        while (Volatile.Read(ref running) < DiscoverySourceFlights.MaximumCallsPerProvider)
        {
            await Task.Yield();
        }

        gate.SetResult();
        await Task.WhenAll(all.Select(flight => flight.Completion));

        Assert.AreEqual(DiscoverySourceFlights.MaximumCallsPerProvider, peak, "Both AniList sources share one budget of concurrent calls.");
    }

    [TestMethod]
    public async Task TheNumberOfRememberedCallsIsBoundedByEvictingTheOldestAndWhenAllAreRunningAnAnswerOfBusy()
    {
        var clock = new DiscoveryTestSupport.MovableClock(Start);
        var flights = DiscoveryTestSupport.Flights(clock);
        for (var index = 0; index < DiscoverySourceFlights.MaximumFlights; index++)
        {
            await Begin(flights, $"movies|search|{index}", (_, _) => Task.FromResult(Titles("x")), source: DiscoverySource.Movies).Completion;
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        var calls = 0;
        var evicting = await Begin(flights, "movies|search|new", (_, _) => { calls++; return Task.FromResult(Titles("y")); }, source: DiscoverySource.Movies).Completion;
        var oldestAgain = await Begin(flights, "movies|search|0", (_, _) => { calls++; return Task.FromResult(Titles("z")); }, source: DiscoverySource.Movies).Completion;

        Assert.AreEqual(DiscoverySourceState.Ready, evicting.State);
        Assert.AreEqual(2, calls, "The oldest answer made room and had to be fetched again.");
        Assert.AreEqual(DiscoverySourceState.Ready, oldestAgain.State);

        var running = new DiscoverySourceFlights(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            clock,
            new DiscoveryTestSupport.IdleLifetime(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DiscoverySourceFlights>.Instance);
        var never = new TaskCompletionSource<IReadOnlyList<DiscoveryItem>>().Task;
        for (var index = 0; index < DiscoverySourceFlights.MaximumFlights; index++)
        {
            running.Start(DiscoverySource.Books, $"books|{index}", Fresh, false, (_, _) => never);
        }

        var refused = await running.Start(DiscoverySource.Books, "books|one-too-many", Fresh, false, (_, _) => never).Completion;

        Assert.AreEqual(DiscoverySourceState.Busy, refused.State, "With every slot taken by a running call, a new key is refused instead of growing without bound.");
    }

    private sealed class ScopedProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
