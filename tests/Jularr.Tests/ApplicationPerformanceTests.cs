using Jularr.Web.Features.Admin;
using Jularr.Web.Features.Performance;
using Jularr.Web.Features.PlaybackSessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>Telemetry, governor, request and provider measurement of the resource policy (#857, #860).</summary>
[TestClass]
public sealed class ApplicationPerformanceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void RowsCountFailuresAndTakeThePercentileFromTheSlowTail()
    {
        var telemetry = new ApplicationPerformanceTelemetry(new ManualTimeProvider(Start));
        for (var index = 0; index < 19; index++)
        {
            telemetry.Record(PerformanceCategory.Route, "GET /books/{id}", TimeSpan.FromMilliseconds(10), PerformanceOutcome.Succeeded);
        }

        telemetry.Record(PerformanceCategory.Route, "GET /books/{id}", TimeSpan.FromSeconds(3), PerformanceOutcome.Failed);

        var row = telemetry.Snapshot().Single();
        Assert.AreEqual(20, row.Count);
        Assert.AreEqual(1, row.Failed);
        Assert.AreEqual(3000, row.MaxMilliseconds, 0.1);
        Assert.AreEqual(25, row.P95Milliseconds, 0.1, "Nineteen of twenty calls are inside the first bucket, so the 95th percentile is its upper edge.");
        Assert.AreEqual(3190, row.TotalMilliseconds, 0.1);
    }

    [TestMethod]
    public void OperationsLeaveTheWindowAfterAnHourAndTheBusiestComeFirst()
    {
        var clock = new ManualTimeProvider(Start);
        var telemetry = new ApplicationPerformanceTelemetry(clock);
        telemetry.Record(PerformanceCategory.Background, "Old.Work", TimeSpan.FromSeconds(1), PerformanceOutcome.Succeeded);
        clock.Advance(ApplicationPerformanceTelemetry.Window + TimeSpan.FromMinutes(5));
        telemetry.Record(PerformanceCategory.Background, "Small", TimeSpan.FromMilliseconds(5), PerformanceOutcome.Succeeded);
        telemetry.Record(PerformanceCategory.Background, "Large", TimeSpan.FromSeconds(2), PerformanceOutcome.Succeeded, TimeSpan.FromSeconds(1));

        var rows = telemetry.Snapshot();

        CollectionAssert.AreEqual(new[] { "Large", "Small" }, rows.Select(row => row.Key).ToArray());
        Assert.AreEqual(1000, rows[0].MeanQueueWaitMilliseconds, 0.1);
    }

    [TestMethod]
    public void KeysBeyondTheCapShareOneOverflowRow()
    {
        var telemetry = new ApplicationPerformanceTelemetry(new ManualTimeProvider(Start));
        for (var index = 0; index < ApplicationPerformanceTelemetry.MaxKeysPerCategory + 50; index++)
        {
            telemetry.Record(PerformanceCategory.Route, $"GET /unique/{index}", TimeSpan.FromMilliseconds(1), PerformanceOutcome.Succeeded);
        }

        var rows = telemetry.Snapshot();

        Assert.AreEqual(ApplicationPerformanceTelemetry.MaxKeysPerCategory + 1, rows.Count);
        Assert.AreEqual(50, rows.Single(row => row.Key == ApplicationPerformanceTelemetry.OverflowKey).Count);
    }

    [TestMethod]
    public async Task ARoutedRequestIsTimedUnderItsTemplateAndCountedWhileItRuns()
    {
        var telemetry = new ApplicationPerformanceTelemetry(TimeProvider.System);
        var load = new InteractiveLoad();
        var inFlightDuring = -1;
        var middleware = new ApplicationPerformanceMiddleware(context =>
        {
            inFlightDuring = load.Active;
            return Task.CompletedTask;
        }, telemetry, load);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/books/837194";
        context.SetEndpoint(Endpoint("/books/{id}"));

        await middleware.InvokeAsync(context);

        Assert.AreEqual(1, inFlightDuring);
        Assert.AreEqual(0, load.Active);
        Assert.AreEqual(1, load.Peak);
        var row = telemetry.Snapshot().Single();
        Assert.AreEqual("GET /books/{id}", row.Key, "The concrete id never becomes a key.");
        Assert.AreEqual(PerformanceCategory.Route, row.Category);
    }

    [TestMethod]
    public async Task ARefusedCallIsCountedUnderItsRouteAndAnUnroutedOneSharesOneKey()
    {
        var telemetry = new ApplicationPerformanceTelemetry(TimeProvider.System);
        var middleware = new ApplicationPerformanceMiddleware(context =>
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return Task.CompletedTask;
        }, telemetry, new InteractiveLoad());
        var routed = new DefaultHttpContext();
        routed.Request.Method = "POST";
        routed.SetEndpoint(Endpoint("/api/search"));
        var unrouted = new DefaultHttpContext();
        unrouted.Request.Method = "GET";
        unrouted.Request.Path = "/wp-login.php";

        await middleware.InvokeAsync(routed);
        await middleware.InvokeAsync(unrouted);

        Assert.AreEqual(1, telemetry.RateLimited()["/api/search"]);
        CollectionAssert.AreEquivalent(new[] { "POST /api/search", "GET unmatched" }, telemetry.Snapshot().Select(row => row.Key).ToArray());
    }

    [TestMethod]
    public async Task ALongLivedHubConnectionIsNeitherTimedNorCounted()
    {
        var telemetry = new ApplicationPerformanceTelemetry(TimeProvider.System);
        var load = new InteractiveLoad();
        var inFlightDuring = -1;
        var middleware = new ApplicationPerformanceMiddleware(context =>
        {
            inFlightDuring = load.Active;
            return Task.CompletedTask;
        }, telemetry, load);
        var context = new DefaultHttpContext();
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/hubs/playback"), 0, new EndpointMetadataCollection(new HubMetadata(typeof(PlaybackSessionHub))), "hub"));

        await middleware.InvokeAsync(context);

        Assert.AreEqual(0, inFlightDuring);
        Assert.AreEqual(0, telemetry.Snapshot().Count);
    }

    [TestMethod]
    public async Task EveryHttpClientIsMeasuredUnderItsRegisteredNameAndNeverItsUrl()
    {
        var telemetry = new ApplicationPerformanceTelemetry(TimeProvider.System);
        var services = new ServiceCollection();
        services.AddSingleton(telemetry);
        services.AddSingleton<IHttpMessageHandlerBuilderFilter, ProviderTelemetryHandlerFilter>();
        services.AddHttpClient("fake-provider").ConfigurePrimaryHttpMessageHandler(() => new StubHandler(System.Net.HttpStatusCode.InternalServerError));
        await using var provider = services.BuildServiceProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("fake-provider");
        using var response = await client.GetAsync("https://secret-host.example/api?apikey=abc");

        var row = telemetry.Snapshot().Single();
        Assert.AreEqual(PerformanceCategory.Provider, row.Category);
        Assert.AreEqual("fake-provider", row.Key);
        Assert.AreEqual(1, row.Failed, "A server error is a failed provider call.");
    }

    [TestMethod]
    public async Task AClassNeverRunsMoreUnitsThanItsLimitAndTheRestWaitsInTheQueue()
    {
        var telemetry = new ApplicationPerformanceTelemetry(TimeProvider.System);
        var governor = new BackgroundWorkGovernor(new InteractiveLoad(), telemetry, TimeProvider.System);
        var release = new TaskCompletionSource();
        var running = 0;
        var maximum = 0;
        var started = new TaskCompletionSource();

        async Task Unit(CancellationToken token)
        {
            var now = Interlocked.Increment(ref running);
            maximum = Math.Max(maximum, now);
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            Interlocked.Decrement(ref running);
        }

        var first = governor.RunAsync(BackgroundWorkClass.Scan, "Library.Scan", Unit, CancellationToken.None);
        await started.Task;
        var second = governor.RunAsync(BackgroundWorkClass.Scan, "Library.Scan", Unit, CancellationToken.None);
        await Eventually(() => governor.States().Single(state => state.WorkClass == BackgroundWorkClass.Scan).Waiting == 1);

        Assert.AreEqual(1, running);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.AreEqual(1, maximum);
        var row = telemetry.Snapshot().Single();
        Assert.AreEqual(2, row.Count);
        Assert.AreEqual(PerformanceCategory.Background, row.Category);
        Assert.IsTrue(row.MeanQueueWaitMilliseconds > 0, "The second unit's wait is recorded apart from its run time.");
    }

    [TestMethod]
    public async Task BackgroundWorkYieldsToBusyRequestsButNeverForeverAndImportNeverYields()
    {
        var clock = new TimerTimeProvider(Start);
        var load = new InteractiveLoad();
        var governor = new BackgroundWorkGovernor(load, new ApplicationPerformanceTelemetry(clock), clock);
        var requests = Enumerable.Range(0, BackgroundWorkGovernor.InteractiveBusyThreshold).Select(_ => load.Begin()).ToList();
        var providerStarted = false;
        var importStarted = false;

        await governor.RunAsync(BackgroundWorkClass.Import, "Import.Execute", _ =>
        {
            importStarted = true;
            return Task.CompletedTask;
        }, CancellationToken.None);
        var provider = governor.RunAsync(BackgroundWorkClass.ProviderRefresh, "Metadata.Refresh", _ =>
        {
            providerStarted = true;
            return Task.CompletedTask;
        }, CancellationToken.None);
        await Eventually(() => clock.PendingTimers == 1);

        Assert.IsTrue(importStarted, "Post-processing is bounded but never held back for requests.");
        Assert.IsFalse(providerStarted, "Provider refresh waits while the requests are busy.");

        await AdvanceUntilCompleted(clock, provider);

        Assert.IsTrue(providerStarted, "Deferral is bounded: constant load delays background work but cannot starve it.");
        var state = governor.States().Single(item => item.WorkClass == BackgroundWorkClass.ProviderRefresh);
        Assert.AreEqual(1, state.Deferred);
        requests.ForEach(request => request.Dispose());
    }

    [TestMethod]
    public async Task WorkStartsAtOnceWhenTheRequestsHaveGoneQuietAndAFailureIsRecordedAndRethrown()
    {
        var clock = new TimerTimeProvider(Start);
        var load = new InteractiveLoad();
        var telemetry = new ApplicationPerformanceTelemetry(clock);
        var governor = new BackgroundWorkGovernor(load, telemetry, clock);
        var requests = Enumerable.Range(0, BackgroundWorkGovernor.InteractiveBusyThreshold).Select(_ => load.Begin()).ToList();
        var work = governor.RunAsync(BackgroundWorkClass.Scan, "Library.Scan", _ => throw new InvalidOperationException("scan failed"), CancellationToken.None);
        await Eventually(() => clock.PendingTimers == 1);

        requests.ForEach(request => request.Dispose());
        await AdvanceUntilCompleted(clock, work, ignoreFailure: true);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await work);
        Assert.AreEqual(1, telemetry.Snapshot().Single().Failed);
        Assert.AreEqual(0, governor.States().Single(state => state.WorkClass == BackgroundWorkClass.Scan).Running, "The slot is released after a failure.");
    }

    [TestMethod]
    public async Task TheStackSamplerRestsBetweenLooksAndWakesForTheFirstOne()
    {
        var clock = new TimerTimeProvider(Start);
        var source = new CountingSource();
        var sampler = new StackResourceTelemetrySampler(source, clock, NullLogger<StackResourceTelemetrySampler>.Instance);
        await sampler.StartAsync(CancellationToken.None);
        try
        {
            await Eventually(() => source.Reads == 1);
            await Eventually(() => clock.PendingTimers == 1);

            clock.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(100);
            Assert.AreEqual(1, source.Reads, "Nobody is looking, so there is no sample every few seconds.");

            clock.Advance(StackResourceTelemetrySampler.IdleInterval);
            await Eventually(() => source.Reads == 2);
            await Eventually(() => clock.PendingTimers == 1);

            var snapshot = sampler.GetSnapshot();
            await Eventually(() => source.Reads == 3);
            Assert.IsTrue(snapshot.History.Count >= 1, "The first look finds a baseline already there.");

            await Eventually(() => clock.PendingTimers == 1);
            clock.Advance(StackResourceTelemetrySampler.Interval);
            await Eventually(() => source.Reads == 4);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    [TestMethod]
    public void RuntimeRatesAreDifferencesBetweenTwoSamples()
    {
        var earlier = new RuntimeHealthSample(0, 0, 1_000_000, 1, 0, 0, TimeSpan.FromMilliseconds(100), 8, 0, 0, TimeSpan.FromSeconds(1));
        var later = earlier with { AllocatedBytes = 11_000_000, GcPause = TimeSpan.FromMilliseconds(600), Gen2Collections = 2, ProcessCpuTime = TimeSpan.FromSeconds(6) };

        var rates = RuntimeHealthRates.Between(earlier, later, TimeSpan.FromSeconds(10));

        Assert.AreEqual(1_000_000, rates.AllocatedBytesPerSecond, 1);
        Assert.AreEqual(5, rates.GcPausePercent, 0.01);
        Assert.AreEqual(12, rates.Gen2PerMinute, 0.01);
        Assert.AreEqual(50, rates.CpuPercentOfOneCore, 0.01);
    }

    /// <summary>Moves the clock one deferral step at a time, letting the awaiting code register its next timer in between.</summary>
    private static async Task AdvanceUntilCompleted(TimerTimeProvider clock, Task task, bool ignoreFailure = false)
    {
        for (var step = 0; step < 200 && !task.IsCompleted; step++)
        {
            clock.Advance(BackgroundWorkGovernor.DeferralStep);
            await Task.Delay(5);
        }

        Assert.IsTrue(task.IsCompleted, "The deferred work started within its bounded deferral.");
        if (!ignoreFailure)
        {
            await task;
        }
    }

    private static RouteEndpoint Endpoint(string pattern) => new(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0, EndpointMetadataCollection.Empty, pattern);

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition did not hold in time.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class StubHandler(System.Net.HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class CountingSource : IStackResourceSource
    {
        private int reads;

        public int Reads => Volatile.Read(ref reads);

        public Task<CgroupResourceUsage?> ReadJularrAsync(CancellationToken cancellationToken) => Task.FromResult<CgroupResourceUsage?>(new CgroupResourceUsage(Interlocked.Increment(ref reads) * 1_000L, 100));

        public Task<CgroupResourceUsage?> ReadPostgreSqlAsync(CancellationToken cancellationToken) => Task.FromResult<CgroupResourceUsage?>(new CgroupResourceUsage(1_000, 200));
    }
}

/// <summary>A clock whose timers fire only when the test advances it, so waits of minutes run instantly and deterministically.</summary>
internal sealed class TimerTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly List<ManualTimer> timers = [];
    private readonly Lock gate = new();
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public override long GetTimestamp() => now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public int PendingTimers
    {
        get
        {
            lock (gate)
            {
                return timers.Count(timer => timer.IsPending);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, dueTime);
        lock (gate)
        {
            timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan by)
    {
        var target = now + by;
        while (true)
        {
            ManualTimer? next;
            lock (gate)
            {
                next = timers.Where(timer => timer.IsPending && timer.DueAt <= target).OrderBy(timer => timer.DueAt).FirstOrDefault();
            }

            if (next is null)
            {
                break;
            }

            now = next.DueAt;
            next.Fire();
        }

        now = target;
    }

    private sealed class ManualTimer(TimerTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        public DateTimeOffset DueAt { get; private set; } = owner.now + dueTime;

        public bool IsPending { get; private set; } = dueTime != Timeout.InfiniteTimeSpan;

        public void Fire()
        {
            IsPending = false;
            callback(state);
        }

        public bool Change(TimeSpan newDueTime, TimeSpan period)
        {
            DueAt = owner.now + newDueTime;
            IsPending = newDueTime != Timeout.InfiniteTimeSpan;
            return true;
        }

        public void Dispose() => IsPending = false;

        public ValueTask DisposeAsync()
        {
            IsPending = false;
            return ValueTask.CompletedTask;
        }
    }
}
