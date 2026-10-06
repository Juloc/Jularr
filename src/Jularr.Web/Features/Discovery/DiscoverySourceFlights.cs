using Jularr.Web.Features.Providers;

namespace Jularr.Web.Features.Discovery;

/// <summary>The provider call of one source. It resolves its providers from the scope it is given, never from the request that started it.</summary>
public delegate Task<IReadOnlyList<DiscoveryItem>> DiscoverySourceFetch(IServiceProvider services, CancellationToken cancellationToken);

/// <summary>One provider call that is running, finished or remembered as failed. <see cref="Completion"/> never faults.</summary>
public sealed class DiscoverySourceFlight(Task<DiscoverySourceOutcome> completion, DateTimeOffset startedAt, DateTimeOffset? retriedAt = null)
{
    public Task<DiscoverySourceOutcome> Completion { get; } = completion;

    public DateTimeOffset StartedAt { get; } = startedAt;

    /// <summary>When a viewer's retry started this call, or null for a call that started on its own.</summary>
    public DateTimeOffset? RetriedAt { get; } = retriedAt;

    public bool IsSettled => Completion.IsCompleted;

    public DiscoverySourceOutcome? Outcome => IsSettled ? Completion.Result : null;
}

public sealed record DiscoverySourceOutcome(DiscoverySourceState State, IReadOnlyList<DiscoveryItem> Items, DateTimeOffset ExpiresAt);

/// <summary>
/// The one owner of Discover provider calls: a call is started once per source, mode, genre, text and locale and shared by every request that needs it
/// (single flight), runs in its own service scope with a hard timeout so it can finish after the request that started it, and is remembered while it is
/// fresh. A failure is remembered only briefly and never as an empty answer. A viewer's retry replaces a settled failure, never a running or a healthy call,
/// and at most once per key within <see cref="RetryInterval"/>. The number of remembered calls and of calls running per provider is bounded.
/// Runtime state only: nothing is persisted, so a restart starts from provider answers again.
/// </summary>
public sealed class DiscoverySourceFlights(IServiceScopeFactory scopes, TimeProvider clock, IHostApplicationLifetime lifetime, ILogger<DiscoverySourceFlights> logger)
{
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan FailureMemory = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);
    public const int MaximumFlights = 256;
    public const int MaximumCallsPerProvider = 3;

    private readonly Lock gate = new();
    private readonly Dictionary<string, DiscoverySourceFlight> flights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> providerSlots = new(StringComparer.Ordinal);

    /// <param name="key">Everything the answer depends on; two callers with the same key share one provider call.</param>
    /// <param name="freshFor">How long a successful answer is reused.</param>
    /// <param name="retry">The viewer asked to try again: honoured only for a settled failure that was not retried within <see cref="RetryInterval"/>.</param>
    public DiscoverySourceFlight Start(DiscoverySource source, string key, TimeSpan freshFor, bool retry, DiscoverySourceFetch fetch)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (flights.TryGetValue(key, out var existing) && !CanReplace(existing, retry, now))
            {
                return existing;
            }

            if (existing is null && !MakeRoom(now))
            {
                return Refused(now);
            }

            // The call is created here, in the context of the caller that starts it, so its culture is that of the request it belongs to.
            var flight = new DiscoverySourceFlight(Task.Run(() => RunAsync(source, key, freshFor, fetch)), now, retry ? now : null);
            flights[key] = flight;
            return flight;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            flights.Clear();
        }
    }

    /// <summary>A remembered call is replaced when it is old, or when it failed and the viewer asked again (not more often than <see cref="RetryInterval"/>).</summary>
    private bool CanReplace(DiscoverySourceFlight flight, bool retry, DateTimeOffset now)
    {
        if (flight.Outcome is not { } outcome)
        {
            return false;
        }

        if (outcome.ExpiresAt <= now)
        {
            return true;
        }

        var retriedRecently = flight.RetriedAt is { } at && now - at < RetryInterval;
        return retry && outcome.State != DiscoverySourceState.Ready && !retriedRecently;
    }

    /// <summary>Makes room for a new key: old answers go first, then the oldest settled one. When every remembered call is still running there is no room.</summary>
    private bool MakeRoom(DateTimeOffset now)
    {
        if (flights.Count < MaximumFlights)
        {
            return true;
        }

        foreach (var entry in flights.Where(entry => entry.Value.Outcome is { } outcome && outcome.ExpiresAt <= now).ToArray())
        {
            flights.Remove(entry.Key);
        }

        if (flights.Count >= MaximumFlights && flights.Where(entry => entry.Value.IsSettled).OrderBy(entry => entry.Value.StartedAt).Select(entry => entry.Key).FirstOrDefault() is { } oldest)
        {
            flights.Remove(oldest);
        }

        return flights.Count < MaximumFlights;
    }

    /// <summary>A call that is not started because too many are running: the source answers busy until there is room again.</summary>
    private DiscoverySourceFlight Refused(DateTimeOffset now) => new(Task.FromResult(Failed(DiscoverySourceState.Busy)), now);

    private async Task<DiscoverySourceOutcome> RunAsync(DiscoverySource source, string key, TimeSpan freshFor, DiscoverySourceFetch fetch)
    {
        var slot = SlotOf(source);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            timeout.CancelAfter(CallTimeout);

            // The hard timeout covers waiting for a free slot and a provider that ignores its token: the call ends and the others go on.
            var items = await RunInSlotAsync(slot, source, fetch, timeout.Token).WaitAsync(CallTimeout, clock, timeout.Token);
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

    private async Task<IReadOnlyList<DiscoveryItem>> RunInSlotAsync(SemaphoreSlim slot, DiscoverySource source, DiscoverySourceFetch fetch, CancellationToken cancellationToken)
    {
        await slot.WaitAsync(cancellationToken);
        try
        {
            using var scope = scopes.CreateScope();
            return await fetch(scope.ServiceProvider, cancellationToken);
        }
        finally
        {
            slot.Release();
        }
    }

    /// <summary>The provider a source belongs to: both AniList sources share one budget of concurrent calls, so a landing never opens a call per row at one provider.</summary>
    private SemaphoreSlim SlotOf(DiscoverySource source)
    {
        var provider = source is DiscoverySource.Anime or DiscoverySource.Reading ? "anilist" : DiscoverySources.Name(source);
        lock (gate)
        {
            if (!providerSlots.TryGetValue(provider, out var slot))
            {
                providerSlots[provider] = slot = new SemaphoreSlim(MaximumCallsPerProvider);
            }

            return slot;
        }
    }

    private DiscoverySourceOutcome Failed(DiscoverySourceState state) => new(state, [], clock.GetUtcNow() + FailureMemory);
}
