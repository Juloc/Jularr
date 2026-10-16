using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Service.System.Reading.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // Scanned books one after another; without work it looks again shortly, since a reader is waiting.
    internal sealed class RecognitionWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<RecognitionWorker> logger) : PeriodicWorker(scopes, time, logger, TimeSpan.FromMinutes(1))
    {
        protected override async Task RunOnceAsync(CancellationToken ct)
        {
            var busy = true;
            while (busy && !ct.IsCancellationRequested)
            {
                busy = false;
                await RunAsync("Text recognition", async (services, token) =>
                    busy = (await services.GetRequiredService<RecognizeReadingV1>().ExecuteAsync<UpdateResult>(new ReadingRecognitionParametersV1(), default, token)).Success, ct);
            }
        }
    }
}
