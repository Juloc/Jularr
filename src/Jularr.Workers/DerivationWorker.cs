using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Service.System.Reading.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    // The reading text of PDFs one after another in the background: reading copies of digital PDFs, and the pages of scans a few at a time. A person opening a book comes first, so without work
    // or with one waiting it looks again shortly; the recognition rests between its slices.
    internal sealed class DerivationWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<DerivationWorker> logger) : PeriodicWorker(scopes, time, logger, TimeSpan.FromMinutes(1))
    {
        private static readonly TimeSpan Pause = TimeSpan.FromSeconds(2);

        protected override async Task RunOnceAsync(CancellationToken ct)
        {
            var busy = true;
            while (busy && !ct.IsCancellationRequested)
            {
                busy = false;
                await RunAsync("Reading copies", async (services, token) =>
                    busy |= (await services.GetRequiredService<DeriveReadingEbooksV1>().ExecuteAsync<UpdateResult>(new ReadingDerivationParametersV1(), default, token)).Success, ct);
                await RunAsync("Text recognition", async (services, token) =>
                    busy |= (await services.GetRequiredService<RecognizeReadingV1>().ExecuteAsync<UpdateResult>(new ReadingRecognitionParametersV1(), default, token)).Success, ct);
                if (busy)
                {
                    await Task.Delay(Pause, Time, ct);
                }
            }
        }
    }
}
