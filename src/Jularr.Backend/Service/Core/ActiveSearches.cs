namespace Jularr.Backend.Service.Core
{
    // What a search looks for while it runs: a target as a whole, one season of it or one unit. Searches leave no trace in the database until they end,
    // so the live stream asks this list. A search is in the list from its first request to its last answer, whoever started it (a person, the schedule, a queued run).
    public sealed record ActiveSearch(long WantedItemId, int? ParentNumber, int? UnitNumber);

    public sealed class ActiveSearches(LiveChanges changes)
    {
        private readonly object _gate = new();
        private readonly List<ActiveSearch> _searches = [];

        // The search counts until the lease is disposed; the open streams hear of both ends.
        public IDisposable Begin(long wantedItemId, int? parentNumber, int? unitNumber)
        {
            var search = new ActiveSearch(wantedItemId, parentNumber, unitNumber);
            lock (_gate)
            {
                _searches.Add(search);
            }

            changes.Notify();
            return new Lease(this, search);
        }

        // Every search that runs now, each once however many run at the same place.
        public IReadOnlyList<ActiveSearch> Snapshot()
        {
            lock (_gate)
            {
                return [.. _searches.Distinct()];
            }
        }

        private void End(ActiveSearch search)
        {
            lock (_gate)
            {
                _searches.Remove(search);
            }

            changes.Notify();
        }

        private sealed class Lease(ActiveSearches owner, ActiveSearch search) : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    owner.End(search);
                }
            }
        }
    }
}
