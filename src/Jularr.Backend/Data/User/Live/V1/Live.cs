using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.User.Live.V1
{
    public sealed class ReadLiveParametersV1 : BaseParameters
    {
    }

    // How far the profile's own request is: its phase, and while it downloads the percent and the time left. No release, indexer, client or error.
    public sealed class LiveRequestV1 : BaseResponse
    {
        public LiveRequestV1(long id, long workId, AcquisitionRequestStatusType acquisitionRequestStatusTypeId, int? progressPercent, int? etaSeconds)
        {
            Id = id;
            WorkId = workId;
            AcquisitionRequestStatusTypeId = acquisitionRequestStatusTypeId;
            ProgressPercent = progressPercent;
            EtaSeconds = etaSeconds;
        }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        // Requested, searching, downloading, importing or completed; the downloading phase already counts the client's unpacking as importing.
        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.AcquisitionRequestStatusTypeId))]
        public AcquisitionRequestStatusType AcquisitionRequestStatusTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.ProgressPercent))]
        public int? ProgressPercent { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.EtaSeconds))]
        public int? EtaSeconds { get; }
    }
}
