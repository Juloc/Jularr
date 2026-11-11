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

        // The same rule for every wait on another machine (an HTTP call, the NAS, FFmpeg reading it): the code names the kind, the action what it reaches.
        public static void Check(bool strict, ILogger logger, string code, string action)
        {
            if (TransactionMarker.OpenTransactionOwner is not { } service)
            {
                return;
            }

            if (strict)
            {
                throw new InvalidOperationException($"{code}: {service} {action} while its SQL transaction is open.");
            }

            if (Reported.TryAdd(code + " " + service, true))
            {
                logger.LogWarning("{Service} {Action} while its SQL transaction is open ({Code}).", service, action, code);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(strict, logger, "external_call_in_transaction", "calls " + (request.RequestUri?.Host ?? "a server"));
            return base.SendAsync(request, cancellationToken);
        }
    }
}
