using Jularr.Web.Features.Discovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>The source flights of Discover with the fake providers a test registers and a clock the test moves.</summary>
internal static class DiscoveryTestSupport
{
    public static DiscoverySourceFlights Flights(TimeProvider? clock = null, IHostApplicationLifetime? lifetime = null, params object[] providers)
    {
        var services = new ServiceCollection();
        foreach (var provider in providers)
        {
            services.AddSingleton(provider.GetType(), provider);
        }

        return new DiscoverySourceFlights(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            clock ?? TimeProvider.System,
            lifetime ?? new IdleLifetime(),
            NullLogger<DiscoverySourceFlights>.Instance);
    }

    public sealed class IdleLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    public sealed class StoppableLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => stopping.Cancel();
    }

    /// <summary>A clock the test moves. Timers and delays that use it fire only when the test advances it past their due time, so nothing waits in real time.</summary>
    public sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        private DateTimeOffset now = now;

        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime);
            lock (timers)
            {
                timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan by)
        {
            now += by;
            ManualTimer[] due;
            lock (timers)
            {
                due = [.. timers.Where(timer => timer.DueAt is { } at && at <= now)];
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(MovableClock clock, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            public DateTimeOffset? DueAt { get; private set; } = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime;

            public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
            {
                DueAt = newDueTime == Timeout.InfiniteTimeSpan ? null : clock.now + newDueTime;
                return true;
            }

            public void Fire()
            {
                DueAt = null;
                callback(state);
            }

            public void Dispose() => DueAt = null;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
