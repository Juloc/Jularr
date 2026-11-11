using System.Collections.Concurrent;
using System.Net;
using Jularr.Backend.Data.Logic.Instance;

namespace Jularr.Backend.Service.Core
{
    // How each metadata provider last answered the requests Jularr made for its own work. The health check reads this instead of calling the providers again.
    internal sealed class ProviderObservations(TimeProvider time)
    {
        private readonly ConcurrentDictionary<string, ProviderObservation> _last = new(StringComparer.Ordinal);

        public void Record(string provider, bool succeeded) => _last[provider] = new ProviderObservation(succeeded, time.GetUtcNow().UtcDateTime);

        public ProviderObservation? Last(string provider) => _last.TryGetValue(provider, out var observation) ? observation : null;
    }

    // Watches a provider's client: no answer, a server fault or a refused key is a failure, anything else shows the provider works. A caller that gives up says nothing about it.
    // The client's timeout cancels the same token as the caller does, so a request still unanswered when the client's time is up counts as no answer
    // (its timer starts just before this handler, hence the small margin).
    internal sealed class ProviderObservationHandler(ProviderObservations observations, string provider, TimeSpan timeout, TimeProvider time) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var started = time.GetTimestamp();
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException
                || exception is OperationCanceledException && (!cancellationToken.IsCancellationRequested || time.GetElapsedTime(started) >= timeout - timeout / 20))
            {
                observations.Record(provider, false);
                throw;
            }

            observations.Record(provider, (int)response.StatusCode < 500 && response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden));
            return response;
        }
    }
}
