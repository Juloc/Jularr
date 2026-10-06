using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

/// <summary>
/// The one owner of Discover provider calls (#595): one call per source shared by every request that needs it, kept while fresh, a failure that is
/// never an empty answer and that a viewer's retry bypasses, and a call that can finish after the request that started it.
/// </summary>
[TestClass]
public sealed class DiscoverySourceFlightsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<DiscoveryItem> Titles(params string[] titles) =>
        [.. titles.Select(title => new DiscoveryItem($"anilist:anime:{title}", "anime", "anilist", title, title, null, null, null, null, null, null, null, null, null, null, [], false, null, "/d", false))];

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

        var first = flights.Start("anime|trending", TimeSpan.FromMinutes(3), false, Fetch);
        var second = flights.Start("anime|trending", TimeSpan.FromMinutes(3), false, Fetch);
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
        Task<IReadOnlyList<DiscoveryItem>> Fetch(IServiceProvider services, CancellationToken cancellationToken) =>
            Task.FromResult(Titles($"call-{Interlocked.Increment(ref calls)}"));

        await flights.Start("books|trending", TimeSpan.FromMinutes(3), false, Fetch).Completion;
        clock.Advance(TimeSpan.FromMinutes(2));
        var fresh = await flights.Start("books|trending", TimeSpan.FromMinutes(3), false, Fetch).Completion;
        clock.Advance(TimeSpan.FromMinutes(2));
        var old = await flights.Start("books|trending", TimeSpan.FromMinutes(3), false, Fetch).Completion;

        Assert.AreEqual("call-1", fresh.Items[0].Title);
        Assert.AreEqual("call-2", old.Items[0].Title);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task AFailureIsNeverAnEmptyAnswerIsRememberedOnlyBrieflyAndAViewersRetryBypassesIt()
    {
        var clock = new DiscoveryTestSupport.MovableClock(Start);
        var calls = 0;
        var flights = DiscoveryTestSupport.Flights(clock);
        Task<IReadOnlyList<DiscoveryItem>> Fetch(IServiceProvider services, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref calls) == 1 ? throw new HttpRequestException("down") : Task.FromResult(Titles("Frieren"));

        var failed = await flights.Start("tv|search|frieren", TimeSpan.FromMinutes(3), false, Fetch).Completion;
        var remembered = await flights.Start("tv|search|frieren", TimeSpan.FromMinutes(3), false, Fetch).Completion;
        var retried = await flights.Start("tv|search|frieren", TimeSpan.FromMinutes(3), true, Fetch).Completion;
        var callsWithTheRetry = calls;
        var again = await flights.Start("tv|search|frieren", TimeSpan.FromMinutes(3), false, Fetch).Completion;

        Assert.AreEqual(DiscoverySourceState.Unavailable, failed.State);
        Assert.AreEqual(0, failed.Items.Count, "A failed source has no titles; it is not an answer of nothing.");
        Assert.AreSame(failed, remembered, "A second request within the memory does not call the provider again.");
        Assert.AreEqual(DiscoverySourceState.Ready, retried.State);
        Assert.AreEqual(2, callsWithTheRetry);
        Assert.AreSame(retried, again, "The recovered answer replaces the failure.");

        var other = DiscoveryTestSupport.Flights(clock);
        calls = 0;
        await other.Start("movies|trending", TimeSpan.FromMinutes(3), false, Fetch).Completion;
        clock.Advance(DiscoverySourceFlights.FailureMemory + TimeSpan.FromSeconds(1));
        var afterTheMemory = await other.Start("movies|trending", TimeSpan.FromMinutes(3), false, Fetch).Completion;
        Assert.AreEqual(DiscoverySourceState.Ready, afterTheMemory.State, "A failure is forgotten after a short while.");
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task ARateLimitedOrUnavailableProviderIsBusyAndAnyOtherFailureIsUnavailable()
    {
        var flights = DiscoveryTestSupport.Flights();

        var limited = await flights.Start("a", TimeSpan.FromMinutes(1), false, (_, _) => throw new ProviderRateLimitedException("tmdb", TimeSpan.FromSeconds(30))).Completion;
        var open = await flights.Start("b", TimeSpan.FromMinutes(1), false, (_, _) => throw new ProviderUnavailableException("tmdb")).Completion;
        var broken = await flights.Start("c", TimeSpan.FromMinutes(1), false, (_, _) => throw new InvalidOperationException("bug")).Completion;
        var malformed = await flights.Start("d", TimeSpan.FromMinutes(1), false, (_, _) => throw new System.Text.Json.JsonException("not json")).Completion;

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

        var finished = await flights.Start("scoped", TimeSpan.FromMinutes(1), false, (provider, _) =>
        {
            Assert.AreSame(scoped, provider.GetRequiredService<ScopedProbe>());
            return Task.FromResult(Titles("x"));
        }).Completion;
        var waiting = flights.Start("waiting", TimeSpan.FromMinutes(1), false, async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Titles();
        });
        lifetime.StopApplication();
        var stopped = await waiting.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(DiscoverySourceState.Ready, finished.State);
        Assert.IsTrue(scoped.Disposed, "The scope of a call is disposed when the call ends.");
        Assert.AreEqual(DiscoverySourceState.Unavailable, stopped.State);
    }

    private sealed class ScopedProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
