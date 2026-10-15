using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Localization;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class AcquisitionRequestCreationData : BaseData
    {
        public AcquisitionRequestCreationData(long workId, long requestedByProfileId, long requestedByAccountId, long uiLocaleId, long? gamePlatformId, long? acquisitionProfileId, bool isOwner)
        {
            WorkId = workId;
            RequestedByProfileId = requestedByProfileId;
            RequestedByAccountId = requestedByAccountId;
            UiLocaleId = uiLocaleId;
            GamePlatformId = gamePlatformId;
            AcquisitionProfileId = acquisitionProfileId;
            IsOwner = isOwner;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long RequestedByProfileId { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long RequestedByAccountId { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long UiLocaleId { get; }

        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.Id), false)]
        public long? GamePlatformId { get; }

        // A quality the requester's rule offers; without one the defaults apply.
        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id), false)]
        public long? AcquisitionProfileId { get; }

        // The session's role: a device paired to the Owner requests like a normal user.
        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.IsOwner))]
        public bool IsOwner { get; }
    }

    internal sealed class AcquisitionRequestData : BaseData
    {
        public AcquisitionRequestData(long id, long workId, long? gamePlatformId)
        {
            Id = id;
            WorkId = workId;
            GamePlatformId = gamePlatformId;
        }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.Id), false)]
        public long? GamePlatformId { get; }
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
        public WantedItemUpdateData(long id, bool isPaused, long? acquisitionProfileId, long? libraryRootId, bool? monitorsNewUnits, long version)
        {
            MonitorsNewUnits = monitorsNewUnits;
            Id = id;
            IsPaused = isPaused;
            AcquisitionProfileId = acquisitionProfileId;
            LibraryRootId = libraryRootId;
            Version = version;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long Id { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.IsPaused))]
        public bool IsPaused { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id), false)]
        public long? AcquisitionProfileId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id), false)]
        public long? LibraryRootId { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.MonitorsNewUnits), false)]
        public bool? MonitorsNewUnits { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Version))]
        public long Version { get; }
    }

    // One unit's, or a season's, monitoring for a wanted Work.
    internal sealed class WantedUnitMonitoringData : BaseData
    {
        public WantedUnitMonitoringData(long wantedItemId, long workUnitId, bool isMonitored)
        {
            WantedItemId = wantedItemId;
            WorkUnitId = workUnitId;
            IsMonitored = isMonitored;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitId))]
        public long WorkUnitId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.IsMonitored))]
        public bool IsMonitored { get; }
    }

    internal sealed class RequestPlatformCheckData : BaseData
    {
        public RequestPlatformCheckData(bool isGame, bool isPlatformListed)
        {
            IsGame = isGame;
            IsPlatformListed = isPlatformListed;
        }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.IsGame))]
        public bool IsGame { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.IsPlatformListed))]
        public bool IsPlatformListed { get; }
    }
}
