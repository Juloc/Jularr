using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Localization;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class AcquisitionRequestCreationData : BaseData
    {
        public AcquisitionRequestCreationData(long workId, long requestedByProfileId, long uiLocaleId)
        {
            WorkId = workId;
            RequestedByProfileId = requestedByProfileId;
            UiLocaleId = uiLocaleId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long RequestedByProfileId { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long UiLocaleId { get; }
    }

    internal sealed class AcquisitionRequestData : BaseData
    {
        public AcquisitionRequestData(long id, long workId)
        {
            Id = id;
            WorkId = workId;
        }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }
    }

    internal sealed class AcquisitionRequestUpdateData : BaseData
    {
        public AcquisitionRequestUpdateData(long id, AcquisitionRequestStatusType acquisitionRequestStatusTypeId, long version, long actorAccountId)
        {
            Id = id;
            AcquisitionRequestStatusTypeId = acquisitionRequestStatusTypeId;
            Version = version;
            ActorAccountId = actorAccountId;
        }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.AcquisitionRequestStatusTypeId))]
        public AcquisitionRequestStatusType AcquisitionRequestStatusTypeId { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.Version))]
        public long Version { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long ActorAccountId { get; }
    }

    internal sealed class WantedItemUpdateData : BaseData
    {
        public WantedItemUpdateData(long id, bool isPaused, long version)
        {
            Id = id;
            IsPaused = isPaused;
            Version = version;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long Id { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.IsPaused))]
        public bool IsPaused { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Version))]
        public long Version { get; }
    }
}
