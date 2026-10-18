using System.Collections.Concurrent;
using System.Text.Json;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Jularr.Backend.Service.Core
{
    // Shared infrastructure for public ranked feeds. Never persist user/profile overlays.
    // Source-specific owners must choose retention within their provider's license.
    internal sealed class DiscoveryFeedSnapshots(SqlDatabase database, TimeProvider clock, IMemoryCache memory, ILogger<DiscoveryFeedSnapshots> logger)
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(12);

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.Ordinal);

        public async Task<IReadOnlyList<T>> ReadAsync<T>(
            string key, string providerKey, TimeSpan freshFor, TimeSpan retainFor,
            Func<CancellationToken, Task<IReadOnlyList<T>>> fetch, CancellationToken ct)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
            ArgumentNullException.ThrowIfNull(fetch);
            if (freshFor <= TimeSpan.Zero || retainFor <= freshFor)
            {
                throw new ArgumentOutOfRangeException(nameof(retainFor));
            }

            // Only pre-migration installations use this short in-memory compatibility cache.
            // Persisted provider data is always checked against its legal retention deadline.
            if (memory.TryGetValue("discovery.compat." + key, out IReadOnlyList<T>? legacy))
            {
                return legacy!;
            }

            var snapshot = await LoadAsync<T>(key, providerKey, ct);
            if (snapshot is { } cached)
            {
                if (cached.FreshUntil <= clock.GetUtcNow().UtcDateTime)
                {
                    StartBackgroundRefresh(key, providerKey, freshFor, retainFor, fetch);
                }

                // A stale snapshot is still a valid answer until its legal retention deadline.
                return cached.Items;
            }

            var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                // A concurrent request may have completed the first fill while we waited.
                if (memory.TryGetValue("discovery.compat." + key, out IReadOnlyList<T>? legacy))
                {
                    return legacyAfterGate!;
                }
                snapshot = await LoadAsync<T>(key, providerKey, ct);
                if (snapshot is not null)
                {
                    return snapshot.Value.Items;
                }

                if (_retryAfter.TryGetValue(key, out var retryAt) && retryAt > clock.GetUtcNow())
                {
                    return [];
                }

                // One initial provider call per key; a cancelled viewer does not abort a
                // shared fill for other viewers, but cannot wait indefinitely for it.
                using var timeout = new CancellationTokenSource(FetchTimeout);
                try
                {
                    return await FetchAndSaveAsync(key, providerKey, freshFor, retainFor, fetch, timeout.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _retryAfter[key] = clock.GetUtcNow() + RetryDelay;
                    logger.LogWarning(exception, "Initial discovery feed {FeedKey} could not be filled.", key);
                    return [];
                }
            }
            finally
            {
                gate.Release();
            }
        }

        private void StartBackgroundRefresh<T>(string key, string providerKey, TimeSpan freshFor, TimeSpan retainFor,
            Func<CancellationToken, Task<IReadOnlyList<T>>> fetch)
        {
            var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            if (_retryAfter.TryGetValue(key, out var retryAt) && retryAt > clock.GetUtcNow())
            {
                return;
            }
            if (!gate.Wait(0))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    using var timeout = new CancellationTokenSource(FetchTimeout);
                    await FetchAndSaveAsync(key, providerKey, freshFor, retainFor, fetch, timeout.Token);
                }
                catch (Exception exception)
                {
                    _retryAfter[key] = clock.GetUtcNow() + RetryDelay;
                    logger.LogWarning(exception, "Discovery feed {FeedKey} refresh failed; retaining last successful data.", key);
                }
                finally
                {
                    gate.Release();
                }
            });
        }

        private async Task<IReadOnlyList<T>> FetchAndSaveAsync<T>(string key, string providerKey, TimeSpan freshFor,
            TimeSpan retainFor, Func<CancellationToken, Task<IReadOnlyList<T>>> fetch, CancellationToken ct)
        {
            // A provider that ignores cancellation must not hold the single-flight gate forever.
            var items = await fetch(ct).WaitAsync(FetchTimeout, ct);
            if (items.Count == 0)
            {
                // An empty or partial provider response must never wipe last-known-good.
                _retryAfter[key] = clock.GetUtcNow() + RetryDelay;
                return items;
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var payload = JsonSerializer.Serialize(items, Json);
            var candidate = new DiscoverySnapshotData(key, providerKey, payload, now, now + freshFor, now + retainFor);
            try
            {
                await using var sql = new SqlContext(database.DataSource);
                await sql.BeginAsync(SqlAccessMode.ReadWrite, cancellationToken: ct);
                await new LogicContext(sql).SaveDiscoverySnapshotAsync(candidate, ct);
                await sql.CommitAsync(ct);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                // Existing installations remain functional until their manual migration,
                // without triggering a remote call for each overlapping request.
                memory.Set("discovery.compat." + key, items,
                    new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(3), Size = Math.Max(1, items.Count) });
                logger.LogDebug("Discovery snapshots schema is not installed yet.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Cache storage must not stop ordinary authorized discovery.
                logger.LogWarning(exception, "Could not persist discovery feed {FeedKey}.", key);
            }

            _retryAfter.TryRemove(key, out _);
            return items;
        }

        private async Task<Snapshot<T>?> LoadAsync<T>(string key, string providerKey, CancellationToken ct)
        {
            try
            {
                await using var sql = new SqlContext(database.DataSource);
                await sql.BeginAsync(SqlAccessMode.ReadOnly, cancellationToken: ct);
                var row = await new LogicContext(sql).ReadDiscoverySnapshotAsync(new DiscoverySnapshotKeyData(key), ct);
                await sql.CommitAsync(ct);

                if (row is null || !string.Equals(providerKey, row.ProviderKey, StringComparison.Ordinal))
                {
                    return null;
                }

                var items = JsonSerializer.Deserialize<T[]>(row.Payload, Json);
                return items is { Length: > 0 } ? new Snapshot<T>(items, row.FreshUntil) : null;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                return null;
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Stored discovery feed {FeedKey} has an invalid payload.", key);
                return null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not read discovery feed {FeedKey}; using source fallback.", key);
                return null;
            }
        }

        private readonly record struct Snapshot<T>(IReadOnlyList<T> Items, DateTime FreshUntil);
    }
}
