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

    public sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
