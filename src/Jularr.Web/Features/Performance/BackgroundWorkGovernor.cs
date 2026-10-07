using System.Diagnostics;

namespace Jularr.Web.Features.Performance;

/// <summary>
/// The workload classes of the resource policy, highest priority first. Interactive requests, Admin actions and playback are not a class:
/// they are never queued, they are what the classes below yield to (<see cref="InteractiveLoad"/>).
/// </summary>
public enum BackgroundWorkClass
{
    /// <summary>Post-processing of finished downloads: never deferred for interactive load, only bounded.</summary>
    Import = 0,

    /// <summary>Metadata, calendar, tracking and search work that calls external providers.</summary>
    ProviderRefresh = 1,

    /// <summary>Library scans and reconciliation: one walk of the storage at a time.</summary>
    Scan = 2,

    /// <summary>Best-effort generation such as artwork.</summary>
    Maintenance = 3
}

/// <summary>How many requests are being served right now. The governor lets background work wait while this is high.</summary>
public sealed class InteractiveLoad
{
    private int active;
    private int peak;

    public int Active => Volatile.Read(ref active);

    public int Peak => Volatile.Read(ref peak);

    /// <summary>Counts one request until the returned scope is disposed.</summary>
    public IDisposable Begin()
    {
        var now = Interlocked.Increment(ref active);
        int seen;
        while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
        {
        }

        return new Scope(this);
    }

    private sealed class Scope(InteractiveLoad owner) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner.active);
            }
        }
    }
}

/// <summary>One class's current budget and what it has done since the process started.</summary>
public sealed record BackgroundWorkClassState(BackgroundWorkClass WorkClass, int Limit, int Running, int Waiting, long Started, long Deferred, double DeferredMilliseconds);

/// <summary>
/// The first slice of the resource governor (#857): every governed background unit runs in its class's bounded slots, and every class below
/// <see cref="BackgroundWorkClass.Import"/> first lets interactive requests through. The deferral is bounded: under constant load the work
/// still starts after <see cref="MaximumDeferral"/>, so interactive pressure delays background work but can never starve it. Each unit is timed
/// (queue wait and run time) into <see cref="ApplicationPerformanceTelemetry"/> under its stable name.
/// </summary>
public sealed class BackgroundWorkGovernor
{
    /// <summary>Requests in flight from which background work below Import yields.</summary>
    public const int InteractiveBusyThreshold = 3;

    public static readonly TimeSpan DeferralStep = TimeSpan.FromMilliseconds(250);

    private static readonly BackgroundWorkClass[] Classes = Enum.GetValues<BackgroundWorkClass>();

    private readonly InteractiveLoad load;
    private readonly ApplicationPerformanceTelemetry telemetry;
    private readonly TimeProvider clock;
    private readonly Dictionary<BackgroundWorkClass, Slot> slots;

    public BackgroundWorkGovernor(InteractiveLoad load, ApplicationPerformanceTelemetry telemetry, TimeProvider clock)
    {
        this.load = load;
        this.telemetry = telemetry;
        this.clock = clock;
        slots = Classes.ToDictionary(workClass => workClass, workClass => new Slot(LimitOf(workClass)));
    }

    public static int LimitOf(BackgroundWorkClass workClass) => workClass switch
    {
        BackgroundWorkClass.Import => 2,
        BackgroundWorkClass.ProviderRefresh => 2,
        _ => 1
    };

    public static TimeSpan MaximumDeferral(BackgroundWorkClass workClass) => workClass switch
    {
        BackgroundWorkClass.Import => TimeSpan.Zero,
        BackgroundWorkClass.ProviderRefresh => TimeSpan.FromSeconds(10),
        BackgroundWorkClass.Scan => TimeSpan.FromSeconds(15),
        _ => TimeSpan.FromSeconds(30)
    };

    public IReadOnlyList<BackgroundWorkClassState> States() =>
        [.. Classes.Select(workClass => slots[workClass].State(workClass))];

    public async Task RunAsync(BackgroundWorkClass workClass, string operation, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        await RunAsync(workClass, operation, async token =>
        {
            await work(token);
            return true;
        }, cancellationToken);
    }

    public async Task<T> RunAsync<T>(BackgroundWorkClass workClass, string operation, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var slot = slots[workClass];
        var queued = Stopwatch.GetTimestamp();
        slot.Waiting(1);
        try
        {
            await YieldToInteractiveAsync(workClass, slot, cancellationToken);
            await slot.Gate.WaitAsync(cancellationToken);
        }
        finally
        {
            slot.Waiting(-1);
        }

        var wait = Stopwatch.GetElapsedTime(queued);
        slot.Started();
        var started = Stopwatch.GetTimestamp();
        var outcome = PerformanceOutcome.Succeeded;
        try
        {
            return await work(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            outcome = PerformanceOutcome.Cancelled;
            throw;
        }
        catch
        {
            outcome = PerformanceOutcome.Failed;
            throw;
        }
        finally
        {
            slot.Finished();
            slot.Gate.Release();
            telemetry.Record(PerformanceCategory.Background, operation, Stopwatch.GetElapsedTime(started), outcome, wait);
        }
    }

    private async Task YieldToInteractiveAsync(BackgroundWorkClass workClass, Slot slot, CancellationToken cancellationToken)
    {
        var budget = MaximumDeferral(workClass);
        if (budget <= TimeSpan.Zero || load.Active < InteractiveBusyThreshold)
        {
            return;
        }

        var deferred = TimeSpan.Zero;
        while (deferred < budget && load.Active >= InteractiveBusyThreshold)
        {
            await Task.Delay(DeferralStep, clock, cancellationToken);
            deferred += DeferralStep;
        }

        slot.Deferred(deferred);
    }

    private sealed class Slot(int limit)
    {
        private int running;
        private int waiting;
        private long started;
        private long deferred;
        private long deferredTicks;

        public SemaphoreSlim Gate { get; } = new(limit, limit);

        public void Waiting(int delta) => Interlocked.Add(ref waiting, delta);

        public void Started()
        {
            Interlocked.Increment(ref running);
            Interlocked.Increment(ref started);
        }

        public void Finished() => Interlocked.Decrement(ref running);

        public void Deferred(TimeSpan time)
        {
            Interlocked.Increment(ref deferred);
            Interlocked.Add(ref deferredTicks, time.Ticks);
        }

        public BackgroundWorkClassState State(BackgroundWorkClass workClass) => new(
            workClass,
            limit,
            Volatile.Read(ref running),
            Volatile.Read(ref waiting),
            Volatile.Read(ref started),
            Volatile.Read(ref deferred),
            TimeSpan.FromTicks(Volatile.Read(ref deferredTicks)).TotalMilliseconds);
    }
}

public static class BackgroundWorkGovernorExtensions
{
    /// <summary>Runs the unit through the governor when one is registered; a host without a governor (a bare unit-test fixture) runs it directly.</summary>
    public static Task RunGovernedAsync(this BackgroundWorkGovernor? governor, BackgroundWorkClass workClass, string operation, Func<CancellationToken, Task> work, CancellationToken cancellationToken) =>
        governor is null ? work(cancellationToken) : governor.RunAsync(workClass, operation, work, cancellationToken);

    public static Task<T> RunGovernedAsync<T>(this BackgroundWorkGovernor? governor, BackgroundWorkClass workClass, string operation, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) =>
        governor is null ? work(cancellationToken) : governor.RunAsync(workClass, operation, work, cancellationToken);
}
