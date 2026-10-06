using System.Collections.Concurrent;
using Jularr.Web.Features.Providers;

namespace Jularr.Web.Features.Discovery;

/// <summary>One provider call that is running, finished or remembered as failed. <see cref="Completion"/> never faults.</summary>
public sealed class DiscoverySourceFlight(Task<DiscoverySourceOutcome> completion)
{
    public Task<DiscoverySourceOutcome> Completion { get; } = completion;

    public bool IsSettled => Completion.IsCompleted;
}

public sealed record DiscoverySourceOutcome(DiscoverySourceState State, IReadOnlyList<DiscoveryItem> Items, DateTimeOffset ExpiresAt);

/// <summary>
/// The one owner of Discover provider calls: a call is started once per source, mode, genre, text and locale and shared by every request
/// that needs it (single flight), runs in its own service scope with a hard timeout so it can finish after the request that started it, and
/// is remembered while it is fresh. A failure is remembered only briefly and never as an empty answer, and a viewer's retry bypasses it.
/// Runtime state only: nothing is persisted, so a restart starts from provider answers again.
/// </summary>
public sealed class DiscoverySourceFlights(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    IHostApplicationLifetime lifetime,
    ILogger<DiscoverySourceFlights> logger)
{
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan FailureMemory = TimeSpan.FromSeconds(10);
    private const int MaximumFlights = 256;

    private readonly ConcurrentDictionary<string, Lazy<DiscoverySourceFlight>> flights = new(StringComparer.Ordinal);

    /// <param name="key">Everything the answer depends on; two callers with the same key share one provider call.</param>
    /// <param name="freshFor">How long a successful answer is reused.</param>
    /// <param name="refresh">Starts a new call even when a fresh or failed answer is remembered.</param>
    /// <param name="fetch">The provider call. It resolves its providers from the scope it is given, never from the request that started it.</param>
    public DiscoverySourceFlight Start(
        string key,
        TimeSpan freshFor,
        bool refresh,
        Func<IServiceProvider, CancellationToken, Task<IReadOnlyList<DiscoveryItem>>> fetch)
    {
        var created = new Lazy<DiscoverySourceFlight>(() => new DiscoverySourceFlight(Task.Run(() => RunAsync(key, freshFor, fetch))), LazyThreadSafetyMode.ExecutionAndPublication);
        var stored = refresh
            ? flights[key] = created
            : flights.AddOrUpdate(key, created, (_, current) => IsStale(current.Value) ? created : current);
        Prune();
        return stored.Value;
    }

    public void Clear() => flights.Clear();

    private bool IsStale(DiscoverySourceFlight flight) =>
        flight.IsSettled && flight.Completion.Result.ExpiresAt <= clock.GetUtcNow();

    private async Task<DiscoverySourceOutcome> RunAsync(
        string key,
        TimeSpan freshFor,
        Func<IServiceProvider, CancellationToken, Task<IReadOnlyList<DiscoveryItem>>> fetch)
    {
        try
        {
            using var scope = scopes.CreateScope();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            timeout.CancelAfter(CallTimeout);
            var items = await fetch(scope.ServiceProvider, timeout.Token);
            return new DiscoverySourceOutcome(DiscoverySourceState.Ready, items, clock.GetUtcNow() + freshFor);
        }
        catch (ProviderRateLimitedException exception)
        {
            logger.LogWarning(exception, "Discovery source {Key} is rate limited.", key);
            return Failed(DiscoverySourceState.Busy);
        }
        catch (ProviderUnavailableException exception)
        {
            logger.LogWarning(exception, "Discovery source {Key} is unavailable.", key);
            return Failed(DiscoverySourceState.Busy);
        }
        catch (Exception exception)
        {
            // The flight is the fault boundary of a provider: whatever it throws, the page keeps the other sources and the cause is logged once here.
            logger.LogWarning(exception, "Discovery source {Key} failed.", key);
            return Failed(DiscoverySourceState.Unavailable);
        }
    }

    private DiscoverySourceOutcome Failed(DiscoverySourceState state) =>
        new(state, [], clock.GetUtcNow() + FailureMemory);

    private void Prune()
    {
        if (flights.Count <= MaximumFlights)
        {
            return;
        }

        foreach (var entry in flights)
        {
            if (IsStale(entry.Value.Value))
            {
                flights.TryRemove(entry);
            }
        }
    }
}
