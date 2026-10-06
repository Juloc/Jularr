using System.Collections.Concurrent;

namespace Jularr.Web.Features.Providers;

/// <summary>Coarse health of one provider integration, for admin diagnostics.</summary>
public enum ProviderHealthStatus
{
    /// <summary>No call has been observed yet.</summary>
    Unknown,

    /// <summary>The last observed call succeeded.</summary>
    Healthy,

    /// <summary>Recent failures, but not enough to trip the circuit.</summary>
    Degraded,

    /// <summary>The circuit is open; calls are being short-circuited until it recovers.</summary>
    Unavailable,

    /// <summary>The provider answered but refused the configured credential; a success or a new credential clears it.</summary>
    AuthenticationFailed
}

/// <summary>A point-in-time snapshot of one provider's health, safe to expose to admin views.</summary>
public sealed record ProviderHealthSnapshot(
    string Key,
    ProviderHealthStatus Status,
    string? LastError,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    int ConsecutiveFailures,
    DateTimeOffset? CircuitOpenUntil);

/// <summary>
/// Per-provider health and a simple circuit breaker, held in memory (runtime
/// state — never persisted). After <see cref="failureThreshold"/> consecutive
/// failures a provider's circuit opens for <see cref="circuitCooldown"/>, during
/// which <see cref="IsAvailable"/> reports false so <see cref="ProviderExecutor"/>
/// can skip doomed calls; a single success closes it again. Registered as a
/// singleton and surfaced for admin through <see cref="Snapshot"/>.
/// </summary>
public sealed class ProviderHealthTracker
{
    public const int DefaultFailureThreshold = 3;
    public static readonly TimeSpan DefaultCircuitCooldown = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Entry> entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider clock;
    private readonly int failureThreshold;
    private readonly TimeSpan circuitCooldown;

    public ProviderHealthTracker(
        TimeProvider clock,
        int failureThreshold = DefaultFailureThreshold,
        TimeSpan? circuitCooldown = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (failureThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(failureThreshold));
        }

        this.clock = clock;
        this.failureThreshold = failureThreshold;
        this.circuitCooldown = circuitCooldown ?? DefaultCircuitCooldown;
    }

    public void RecordSuccess(string key)
    {
        var entry = EntryFor(key);
        lock (entry.Sync)
        {
            entry.Status = ProviderHealthStatus.Healthy;
            entry.LastError = null;
            entry.LastSuccessUtc = clock.GetUtcNow();
            entry.ConsecutiveFailures = 0;
            entry.CircuitOpenUntil = null;
        }
    }

    public void RecordFailure(string key, string error)
    {
        var entry = EntryFor(key);
        lock (entry.Sync)
        {
            var now = clock.GetUtcNow();
            entry.LastError = error;
            entry.LastFailureUtc = now;
            entry.ConsecutiveFailures++;
            if (entry.ConsecutiveFailures >= failureThreshold)
            {
                entry.Status = ProviderHealthStatus.Unavailable;
                entry.CircuitOpenUntil = now + circuitCooldown;
            }
            else
            {
                entry.Status = ProviderHealthStatus.Degraded;
            }
        }
    }

    /// <summary>
    /// The provider answered but refused the credential. This is not an outage: it neither counts toward the circuit nor
    /// opens it, and the next successful call clears it.
    /// </summary>
    public void RecordAuthenticationFailure(string key)
    {
        var entry = EntryFor(key);
        lock (entry.Sync)
        {
            entry.Status = ProviderHealthStatus.AuthenticationFailed;
            entry.LastError = "Authentication failed.";
            entry.LastFailureUtc = clock.GetUtcNow();
        }
    }

    /// <summary>Forgets everything observed about a provider, so a changed configuration is judged by its own calls and not by the old one.</summary>
    public void Reset(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Provider key is required.", nameof(key));
        }

        entries.TryRemove(key, out _);
    }

    /// <summary>False only while the circuit is open (recently tripped); true otherwise.</summary>
    public bool IsAvailable(string key)
    {
        if (!entries.TryGetValue(key, out var entry))
        {
            return true;
        }

        lock (entry.Sync)
        {
            return entry.CircuitOpenUntil is not { } until || until <= clock.GetUtcNow();
        }
    }

    public ProviderHealthSnapshot Get(string key)
    {
        if (!entries.TryGetValue(key, out var entry))
        {
            return new ProviderHealthSnapshot(
                key, ProviderHealthStatus.Unknown, null, null, null, 0, null);
        }

        lock (entry.Sync)
        {
            return entry.ToSnapshot(key);
        }
    }

    /// <summary>Every provider that has been observed, ordered by key.</summary>
    public IReadOnlyList<ProviderHealthSnapshot> Snapshot() =>
        entries
            .Select(pair =>
            {
                lock (pair.Value.Sync)
                {
                    return pair.Value.ToSnapshot(pair.Key);
                }
            })
            .OrderBy(snapshot => snapshot.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private Entry EntryFor(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Provider key is required.", nameof(key));
        }

        return entries.GetOrAdd(key, _ => new Entry());
    }

    private sealed class Entry
    {
        public Lock Sync { get; } = new();

        public ProviderHealthStatus Status { get; set; } = ProviderHealthStatus.Unknown;

        public string? LastError { get; set; }

        public DateTimeOffset? LastSuccessUtc { get; set; }

        public DateTimeOffset? LastFailureUtc { get; set; }

        public int ConsecutiveFailures { get; set; }

        public DateTimeOffset? CircuitOpenUntil { get; set; }

        public ProviderHealthSnapshot ToSnapshot(string key) =>
            new(key, Status, LastError, LastSuccessUtc, LastFailureUtc, ConsecutiveFailures, CircuitOpenUntil);
    }
}
