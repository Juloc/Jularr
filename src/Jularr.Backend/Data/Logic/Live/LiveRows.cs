using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Operation;

namespace Jularr.Backend.Data.Logic.Live
{
    // A request as stored.
    internal sealed record LiveRequestRow(long Id, long WorkId, AcquisitionRequestStatusType AcquisitionRequestStatusTypeId);

    // What a download client reported about one running download.
    internal sealed record LiveDownloadFacts(OperationStatusType OperationStatusTypeId, DownloadStateType? DownloadStateTypeId, bool IsWaiting, int? ProgressPercent, long? SizeBytes, long? RemainingBytes, int? EtaSeconds);

    // The downloads of one target or request taken together.
    internal sealed record LiveProgressData(int Count, int? ProgressPercent, long? SizeBytes, long? RemainingBytes, int? EtaSeconds, long? BytesPerSecond, bool IsImporting, bool IsWaiting, bool IsPaused);

    // Where a request stands: its phase and, while it downloads, how far.
    internal sealed record LiveRequestPhase(AcquisitionRequestStatusType Phase, int? ProgressPercent, int? EtaSeconds, long? BytesPerSecond);

    // One download of a request, as the profile's read finds it.
    internal sealed record LiveRequestDownloadRow(long AcquisitionRequestId, OperationStatusType OperationStatusTypeId, DownloadStateType? DownloadStateTypeId, bool IsWaiting, int? ProgressPercent, long? SizeBytes,
        long? RemainingBytes, int? EtaSeconds);

    // One operation of any kind with the target and requests it works for; one row per request, none or one target.
    internal sealed record LiveOperationRow(long Id, OperationType OperationTypeId, OperationStatusType OperationStatusTypeId, DownloadStateType? DownloadStateTypeId, bool IsWaiting, int? ProgressPercent, long? SizeBytes,
        long? RemainingBytes, int? EtaSeconds, string? ReasonKey, long? WantedItemId, long? WorkId, string? ReleaseTitle, string? IndexerName, string? ClientName, long? AcquisitionRequestId);
}
