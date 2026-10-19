using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Backend.Service.System.Provider.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Workers
{
    internal sealed class AcquisitionWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<AcquisitionWorker> logger, AcquisitionSearchSignal signal)
        : PeriodicWorker(scopes, time, logger, TimeSpan.FromSeconds(15))
    {
        private static readonly TimeSpan SearchInterval = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan PlexInterval = TimeSpan.FromMinutes(30);
        private DateTimeOffset _nextSearch = DateTimeOffset.MinValue;
        private DateTimeOffset _nextPlexSync = DateTimeOffset.MinValue;

        protected override async Task RunOnceAsync(CancellationToken ct)
        {
            // Downloads are tracked often; metadata and due targets are refreshed in small passes.
            await RunAsync<MonitorDownloadsV1>(ct);
            await RunAsync<RefreshStoredFileFormatsV1>(ct);
            var periodicSearchDue = Time.GetUtcNow() >= _nextSearch;
            if (signal.TakeRequest() || periodicSearchDue)
            {
                await RunAsync<RefreshWantedMetadataV1>(ct);
                await RunAsync<SearchDueWantedV1>(ct);
                if (periodicSearchDue) _nextSearch = Time.GetUtcNow() + SearchInterval;
            }

            // Plex availability follows the server every half hour.
            if (Time.GetUtcNow() >= _nextPlexSync)
            {
                await RunAsync<SyncPlexLibraryV1>(ct);
                _nextPlexSync = Time.GetUtcNow() + PlexInterval;
            }
        }

        protected override async Task<bool> WaitForNextRunAsync(PeriodicTimer timer, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tick = timer.WaitForNextTickAsync(linked.Token).AsTask();
            var request = signal.WaitForRequestAsync(linked.Token).AsTask();
            var completed = await Task.WhenAny(tick, request);
            await linked.CancelAsync();
            try
            {
                return await completed;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            finally
            {
                try
                {
                    await Task.WhenAll(tick, request);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        private Task RunAsync<TService>(CancellationToken ct) where TService : SystemService<AcquisitionCycleParametersV1, NoData> =>
            RunAsync(typeof(TService).Name, (services, token) => services.GetRequiredService<TService>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default, token), ct);
    }
}
