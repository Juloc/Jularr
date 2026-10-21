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
    internal sealed class ProviderObservationHandler(ProviderObservations observations, string provider) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                observations.Record(provider, false);
                throw;
            }

            observations.Record(provider, (int)response.StatusCode < 500 && response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden));
            return response;
        }
    }
}
