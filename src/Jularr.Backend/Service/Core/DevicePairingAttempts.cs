using Jularr.Backend.Data.Common;

namespace Jularr.Backend.Service.Core
{
    // Wrong pairing codes per account: five in ten minutes, then the account waits. An attempt is taken before the code is
    // checked and given back when it was right, so parallel requests cannot pass the limit together.
    internal sealed class DevicePairingAttempts(TimeProvider time)
    {
        private const int Limit = 5;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
        private readonly Dictionary<long, Queue<DateTimeOffset>> _attempts = [];

        public void Take(long accountId)
        {
            lock (_attempts)
            {
                var attempts = Recent(accountId);
                if (attempts.Count >= Limit)
                {
                    throw new ServiceUnavailableException("device_pairing_attempts");
                }

                attempts.Enqueue(time.GetUtcNow());
            }
        }

        public void GiveBack(long accountId)
        {
            lock (_attempts)
            {
                var attempts = Recent(accountId);
                if (attempts.Count > 0)
                {
                    // The newest attempt was this one; the queue keeps its order by time.
                    var kept = attempts.Take(attempts.Count - 1).ToArray();
                    attempts.Clear();
                    foreach (var attempt in kept)
                    {
                        attempts.Enqueue(attempt);
                    }
                }

                if (attempts.Count == 0)
                {
                    _attempts.Remove(accountId);
                }
            }
        }

        private Queue<DateTimeOffset> Recent(long accountId)
        {
            if (!_attempts.TryGetValue(accountId, out var attempts))
            {
                attempts = new Queue<DateTimeOffset>();
                _attempts[accountId] = attempts;
            }

            while (attempts.Count > 0 && attempts.Peek() < time.GetUtcNow() - Window)
            {
                attempts.Dequeue();
            }

            return attempts;
        }
    }
}
