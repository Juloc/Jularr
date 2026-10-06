using System.Collections.Concurrent;

namespace Jularr.Web.Features.Providers;

/// <summary>
/// An in-memory, keyed response cache with "stale-while-unavailable": a cached
/// value is served fresh within its lifetime, and if a refresh fails while the
/// provider is unavailable the last good value is served instead of surfacing the
/// error. Runtime state only (never persisted); registered as a singleton and
/// shared by providers that go through the framework. Callers namespace their keys
/// (for example <c>"anilist:schedule:2026-09"</c>) to avoid collisions.
/// </summary>
public sealed class ProviderResponseCache(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly TimeProvider clock = clock;

    /// <summary>
    /// Returns the cached value when it is still fresh; otherwise refreshes through
    /// <paramref name="fetch"/>. If the refresh fails (the provider is unavailable) and a
    /// previously cached value exists, that stale value is returned; with no cached value
    /// the failure propagates. <see cref="OperationCanceledException"/> from a requested
    /// cancellation always propagates and never serves stale.
    /// </summary>
    public async Task<T> GetOrFetchAsync<T>(
        string key,
        TimeSpan freshFor,
        Func<CancellationToken, Task<T>> fetch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(fetch);

        var cached = entries.TryGetValue(key, out var existing) ? existing : null;
        if (cached is not null && clock.GetUtcNow() - cached.StoredAtUtc < freshFor)
        {
            return (T)cached.Value;
        }

        try
        {
            var value = await fetch(cancellationToken);
            Set(key, value!);
            return value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch when (cached is not null)
        {
            // Stale-while-unavailable: the provider could not be refreshed, so serve
            // the last good value rather than failing the caller.
            return (T)cached.Value;
        }
    }

    /// <summary>The cached value when present and still within <paramref name="freshFor"/>.</summary>
    public bool TryGetFresh<T>(string key, TimeSpan freshFor, out T value)
    {
        if (entries.TryGetValue(key, out var entry) &&
            clock.GetUtcNow() - entry.StoredAtUtc < freshFor &&
            entry.Value is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    public void Set<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        entries[key] = new Entry(value, clock.GetUtcNow());
    }

    /// <summary>Forgets every entry whose key starts with <paramref name="prefix"/>, so a provider whose configuration changed is asked again instead of answered from the old one.</summary>
    public void RemoveByPrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        foreach (var key in entries.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            entries.TryRemove(key, out _);
        }
    }

    private sealed record Entry(object Value, DateTimeOffset StoredAtUtc);
}
