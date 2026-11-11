using System.Collections.Concurrent;
using Jularr.Backend.Infrastructure.Sql;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.Core
{
    // No external call while the calling service operation holds a SQL transaction open: in development and tests it fails at once, in production it is
    // logged once per service and goes ahead.
    internal sealed class ExternalCallGuard(bool strict, ILogger logger) : DelegatingHandler
    {
        private static readonly ConcurrentDictionary<string, bool> Reported = new(StringComparer.Ordinal);

        public static bool IsStrict(IHostEnvironment? environment) => environment is null || environment.IsDevelopment() || environment.IsEnvironment("Test");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (TransactionMarker.OpenTransactionOwner is { } service)
            {
                var host = request.RequestUri?.Host ?? "a server";
                if (strict)
                {
                    throw new InvalidOperationException($"external_call_in_transaction: {service} calls {host} while its SQL transaction is open.");
                }

                if (Reported.TryAdd(service, true))
                {
                    logger.LogWarning("{Service} calls {Host} while its SQL transaction is open (external_call_in_transaction).", service, host);
                }
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
