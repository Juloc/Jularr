using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Localization.V1;
using Microsoft.Extensions.Caching.Memory;

namespace Jularr.Backend.Service.Public.Localization.V1
{
    // Passive cache only: SQL, fallback and service access remain in the read services.
    public sealed class UiLocalizationCache : IDisposable
    {
        private readonly TimeProvider _time;
        private readonly MemoryCache _entries = new(new MemoryCacheOptions { SizeLimit = 4 * 1024 * 1024 });
        private readonly SemaphoreSlim _loadLock = new(1, 1);

        public UiLocalizationCache(TimeProvider time)
        {
            _time = time;
        }

        internal async Task<T> GetOrLoadAsync<T>(long uiLocaleId, Func<Task<T>> load, CancellationToken cancellationToken)
            where T : BaseData, IServiceOutput
        {
            await _loadLock.WaitAsync(cancellationToken);
            try
            {
                var key = (typeof(T), uiLocaleId);
                if (_entries.TryGetValue(key, out Entry<T>? entry)
                    && entry is not null && entry.ExpiresAt > _time.GetUtcNow())
                {
                    return entry.Value;
                }

                var result = await load();
                var size = result switch
                {
                    ReadUiTextsResultV1 texts => texts.Texts.Sum(text => (long)(text.Key.Length + text.Value.Length) * 2 + 96),
                    BaseDataList<UiLocaleDetailsV1> locales => locales.Sum(locale => (long)(locale.Locale.Length + locale.Name.Length) * 2 + 64),
                    _ => throw new InvalidOperationException("Unsupported localization cache result.")
                };
                _entries.Set(key, new Entry<T>(result, _time.GetUtcNow().AddSeconds(30)),
                    new MemoryCacheEntryOptions { Size = Math.Max(1, size), SlidingExpiration = TimeSpan.FromMinutes(10) });
                return result;
            }
            finally
            {
                _loadLock.Release();
            }
        }

        public void Dispose()
        {
            _entries.Dispose();
            _loadLock.Dispose();
        }

        private sealed record Entry<T>(T Value, DateTimeOffset ExpiresAt);
    }
}
