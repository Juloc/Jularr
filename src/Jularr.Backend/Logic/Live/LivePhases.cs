using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Logic.Live;

namespace Jularr.Backend.Logic.Live
{
    // The one place that turns requests, targets, downloads and imports into the phase and progress every page shows.
    internal static class LivePhases
    {
        // The downloads still running at one target or for one request; none when nothing runs.
        public static LiveProgressData? Combine(IEnumerable<LiveDownloadFacts> downloads)
        {
            var active = downloads.Where(download => download.OperationStatusTypeId is OperationStatusType.Queued or OperationStatusType.Running).ToArray();
            if (active.Length == 0)
            {
                return null;
            }

            // Bytes count when every client reports them, else the clients' own percent.
            long? size = active.All(download => download.SizeBytes is not null) ? active.Sum(download => download.SizeBytes!.Value) : null;
            long? remaining = size is not null && active.All(download => download.RemainingBytes is not null) ? active.Sum(download => download.RemainingBytes!.Value) : null;
            int? percent;
            if (size > 0 && remaining is not null)
            {
                percent = (int)Math.Clamp((size.Value - remaining.Value) * 100 / size.Value, 0, 99);
            }
            else
            {
                var reported = active.Where(download => download.ProgressPercent is not null).Select(download => download.ProgressPercent!.Value).ToArray();
                percent = reported.Length == 0 ? null : (int)Math.Round(reported.Average());
            }

            // The client's own pace: what is left over the time it still needs.
            var paced = active.Select(download => Pace(download.RemainingBytes, download.EtaSeconds)).OfType<long>().ToArray();
            long? speed = paced.Length == 0 ? null : paced.Sum();
            int? eta = active.Any(download => download.EtaSeconds is not null) ? active.Max(download => download.EtaSeconds ?? 0) : null;

            return new LiveProgressData(active.Length, percent, size, remaining, eta, speed,
                active.All(download => download.DownloadStateTypeId == DownloadStateType.PostProcessing),
                active.Any(download => download.IsWaiting),
                active.All(download => download.DownloadStateTypeId == DownloadStateType.Paused));
        }

        // The client's own pace: bytes still to come over the time it still needs.
        public static long? Pace(long? remainingBytes, int? etaSeconds) => remainingBytes is { } remaining && etaSeconds is > 0 ? remaining / etaSeconds.Value : null;

        // A request follows its target forward; once the client only unpacks it is importing, and only a download has a percent and a time left.
        public static LiveRequestPhase OfRequest(AcquisitionRequestStatusType status, LiveProgressData? downloads)
        {
            if (status != AcquisitionRequestStatusType.Downloading || downloads is null)
            {
                return new LiveRequestPhase(status, null, null, null);
            }

            return downloads.IsImporting
                ? new LiveRequestPhase(AcquisitionRequestStatusType.Importing, null, null, null)
                : new LiveRequestPhase(status, downloads.ProgressPercent, downloads.EtaSeconds, downloads.BytesPerSecond);
        }

        // A target downloads, or imports once only unpacking is left.
        public static AcquisitionRequestStatusType OfTarget(LiveProgressData downloads) => downloads.IsImporting ? AcquisitionRequestStatusType.Importing : AcquisitionRequestStatusType.Downloading;
    }
}
