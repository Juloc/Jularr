using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    // Editor values; regex and score rules stay in the stored definition.
    internal sealed class AcquisitionProfileInputData
    {
        public AcquisitionProfileInputData(long? id, string name, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, bool isDefault, string[] allowedQualities, bool upgradeAllowed, string? upgradeCutoffQuality,
            int? minimumSizeMb, int? maximumSizeMb, string[] mustContain, string[] mustNotContain, long? version)
        {
            Id = id;
            Name = name;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            IsDefault = isDefault;
            AllowedQualities = allowedQualities;
            UpgradeAllowed = upgradeAllowed;
            UpgradeCutoffQuality = upgradeCutoffQuality;
            MinimumSizeMb = minimumSizeMb;
            MaximumSizeMb = maximumSizeMb;
            MustContain = mustContain;
            MustNotContain = mustNotContain;
            Version = version;
        }

        public long? Id { get; }
        public string Name { get; }
        public MediaType MediaTypeId { get; }
        public MediaSubtype? MediaSubtypeId { get; }
        public bool IsDefault { get; }
        public string[] AllowedQualities { get; }
        public bool UpgradeAllowed { get; }
        public string? UpgradeCutoffQuality { get; }
        public int? MinimumSizeMb { get; }
        public int? MaximumSizeMb { get; }
        public string[] MustContain { get; }
        public string[] MustNotContain { get; }
        public long? Version { get; }
    }

    internal sealed class AcquisitionProfileRowData : BaseData
    {
        public AcquisitionProfileRowData(long? id, string name, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, bool isDefault, string definition, long? version)
        {
            Id = id;
            Name = name;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            IsDefault = isDefault;
            Definition = definition;
            Version = version;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id), false)]
        public long? Id { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Name))]
        public string Name { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.IsDefault))]
        public bool IsDefault { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Definition))]
        public string Definition { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Version), false)]
        public long? Version { get; }
    }

    internal sealed class AcquisitionProfileStateData : BaseData
    {
        public AcquisitionProfileStateData(long id, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, bool isDefault, string definition, long version)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            IsDefault = isDefault;
            Definition = definition;
            Version = version;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.IsDefault))]
        public bool IsDefault { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Definition))]
        public string Definition { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Version))]
        public long Version { get; }
    }

    internal sealed class AcquisitionProfileKeyData : BaseData
    {
        public AcquisitionProfileKeyData(long id, long? version = null)
        {
            Id = id;
            Version = version;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Version), false)]
        public long? Version { get; }
    }

    internal sealed class AcquisitionReleaseMarkData : BaseData
    {
        public AcquisitionReleaseMarkData(long wantedItemId, long acquisitionReleaseId, AcquisitionReleaseStatusType acquisitionReleaseStatusTypeId)
        {
            WantedItemId = wantedItemId;
            AcquisitionReleaseId = acquisitionReleaseId;
            AcquisitionReleaseStatusTypeId = acquisitionReleaseStatusTypeId;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Id))]
        public long AcquisitionReleaseId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.AcquisitionReleaseStatusTypeId))]
        public AcquisitionReleaseStatusType AcquisitionReleaseStatusTypeId { get; }
    }
}
