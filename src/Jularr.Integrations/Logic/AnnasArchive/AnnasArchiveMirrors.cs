using Jularr.Integrations.Data.AnnasArchive;

namespace Jularr.Integrations.Logic.AnnasArchive
{
    // Search health contains only approved origins, never credentials or page contents.
    internal sealed class AnnasArchiveMirrors(TimeProvider time)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<Uri, Health> _health = [];

        public IReadOnlyList<Uri> Available(IReadOnlyList<Uri> mirrors)
        {
            lock (_gate)
            {
                return mirrors.Where(site => !_health.TryGetValue(site, out var health) || health.RetryAt <= time.GetUtcNow())
                    .OrderByDescending(site => _health.GetValueOrDefault(site)?.SucceededAt).ToArray();
            }
        }

        public AnnasArchiveFailure LastFailure(Uri site)
        {
            lock (_gate) return _health.GetValueOrDefault(site)?.Failure ?? AnnasArchiveFailure.Unavailable;
        }

        public void Record(Uri site, AnnasArchiveFailure? failure)
        {
            lock (_gate)
            {
                if (!_health.ContainsKey(site) && _health.Count >= 64)
                {
                    _health.Remove(_health.MinBy(item => item.Value.ObservedAt).Key);
                }

                var now = time.GetUtcNow();
                var previous = _health.GetValueOrDefault(site);
                var delay = failure == AnnasArchiveFailure.InvalidResponse ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(1);
                _health[site] = new Health(now, failure is null ? now : previous?.SucceededAt,
                    failure is null ? now : now.Add(delay), failure ?? AnnasArchiveFailure.Unavailable);
            }
        }

        private sealed record Health(DateTimeOffset ObservedAt, DateTimeOffset? SucceededAt, DateTimeOffset RetryAt, AnnasArchiveFailure Failure);
    }
}
