namespace Jularr.Backend.Service.Core
{
    // Persisted cancellation survives a restart; this signal also interrupts the current source request.
    internal sealed class SearchRunCancellation
    {
        private readonly Dictionary<long, CancellationTokenSource> _running = new();

        public CancellationTokenSource Register(long operationId, CancellationToken stoppingToken)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_running)
            {
                if (!_running.TryAdd(operationId, source))
                {
                    source.Dispose();
                    throw new InvalidOperationException("The search is already running.");
                }
            }

            return source;
        }

        public void Cancel(long operationId)
        {
            lock (_running)
            {
                if (_running.TryGetValue(operationId, out var source)) source.Cancel();
            }
        }

        public void Remove(long operationId)
        {
            lock (_running)
            {
                if (_running.Remove(operationId, out var source)) source.Dispose();
            }
        }
    }
}
