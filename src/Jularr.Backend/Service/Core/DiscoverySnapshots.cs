using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;

namespace Jularr.Backend.Service.Core
{
    // Public provider payloads only. Work/profile state is applied after this cache on every read.
    internal sealed class DiscoverySnapshots : IDisposable
    {
        private readonly SemaphoreSlim[] _keys = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
        private readonly SemaphoreSlim _providers = new(4);

        public async Task<T> ReadAsync<T>(LogicContext context, DiscoverySnapshotRequest request, Func<CancellationToken, Task<T>> fetch, CancellationToken ct, bool forceRefresh = false)
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Serialize())));
            var parameters = new DiscoverySnapshotKeyData(key);
            var cached = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadDiscoverySnapshotAsync(parameters, token), ct);
            if (cached is not null && !forceRefresh)
            {
                // Expiry schedules a refresh; it never removes a usable response or blocks the reader.
                return JsonSerializer.Deserialize<T>(cached.Payload)!;
            }

            var gate = _keys[Convert.ToByte(key[..2], 16) % _keys.Length];
            await gate.WaitAsync(ct);
            try
            {
                cached = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadDiscoverySnapshotAsync(parameters, token), ct);
                if (cached is not null && !forceRefresh)
                {
                    return JsonSerializer.Deserialize<T>(cached.Payload)!;
                }

                await _providers.WaitAsync(ct);
                T result;
                try
                {
                    result = await fetch(ct);
                }
                finally
                {
                    _providers.Release();
                }

                await SaveAsync(context, key, request, result, ct);
                return result;
            }
            finally
            {
                gate.Release();
            }
        }

        public static async Task SaveAsync<T>(LogicContext context, string key, DiscoverySnapshotRequest request, T result, CancellationToken ct)
        {
            var payload = JsonSerializer.Serialize(result);
            var requestJson = request.Serialize();
            // Large provider responses remain usable without consuming the cache budget.
            if (Encoding.UTF8.GetByteCount(payload) <= 1_048_576 && Encoding.UTF8.GetByteCount(requestJson) <= 4096)
            {
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    var policy = await ctx.ReadDiscoveryCachePolicyAsync(token);
                    return await ctx.SaveDiscoverySnapshotAsync(new DiscoverySnapshotWriteData(key, requestJson, payload, request.FreshHours(policy),
                        policy.DiscoveryCacheEntries, policy.DiscoveryCacheMegabytes), token);
                }, ct);
            }
        }

        public void Dispose()
        {
            foreach (var gate in _keys)
            {
                gate.Dispose();
            }
            _providers.Dispose();
        }
    }
}
