using Jularr.Backend.Data.Common;
using System.Collections.Concurrent;

namespace Jularr.Backend.Service.Core
{
    // Wrong pairing codes per account: five in ten minutes, then the account waits.
    internal sealed class DevicePairingAttempts(TimeProvider time)
    {
        private const int Limit = 5;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
        private readonly ConcurrentDictionary<long, Queue<DateTimeOffset>> _failures = new();

        public void RequireAllowed(long accountId)
        {
            if (Recent(accountId) >= Limit)
            {
                throw new ServiceUnavailableException("device_pairing_attempts");
            }
        }

        public void Fail(long accountId)
        {
            var failures = _failures.GetOrAdd(accountId, _ => new Queue<DateTimeOffset>());
            lock (failures)
            {
                failures.Enqueue(time.GetUtcNow());
            }
        }

        private int Recent(long accountId)
        {
            if (!_failures.TryGetValue(accountId, out var failures))
            {
                return 0;
            }

            lock (failures)
            {
                while (failures.Count > 0 && failures.Peek() < time.GetUtcNow() - Window)
                {
                    failures.Dequeue();
                }

                return failures.Count;
            }
        }
    }
}
