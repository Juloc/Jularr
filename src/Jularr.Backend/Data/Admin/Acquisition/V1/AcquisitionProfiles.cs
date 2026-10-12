using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class ReadAcquisitionProfilesParametersV1 : BaseParameters
    {
    }

    public sealed class CreateAcquisitionProfileParametersV1 : BaseParameters
    {
    }

    public sealed class AcquisitionProfileParametersV1 : BaseParameters
    {
        public AcquisitionProfileParametersV1(long acquisitionProfileId)
        {
            AcquisitionProfileId = acquisitionProfileId;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long AcquisitionProfileId { get; }
    }

    public sealed class DeleteAcquisitionProfileParametersV1 : BaseParameters
    {
        public DeleteAcquisitionProfileParametersV1(long acquisitionProfileId, long version)
        {
            AcquisitionProfileId = acquisitionProfileId;
            Version = version;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long AcquisitionProfileId { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Version))]
        public long Version { get; }
    }

    public sealed class SaveAcquisitionProfileDataV1 : BaseData
    {
        public SaveAcquisitionProfileDataV1(string name, MediaType mediaTypeId, bool isDefault, string[] allowedQualities, bool upgradeAllowed, string? upgradeCutoffQuality, int? minimumSizeMb,
            int? maximumSizeMb, string[] mustContain, string[] mustNotContain, long? version = null)
        {
            Name = name;
            MediaTypeId = mediaTypeId;
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

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Name))]
        public string Name { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.IsDefault))]
        public bool IsDefault { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.AllowedQualities))]
        public string[] AllowedQualities { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.UpgradeAllowed))]
        public bool UpgradeAllowed { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.UpgradeCutoffQuality))]
        public string? UpgradeCutoffQuality { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.MinimumSizeMb))]
        public int? MinimumSizeMb { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.MaximumSizeMb))]
        public int? MaximumSizeMb { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.MustContain))]
        public string[] MustContain { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.MustNotContain))]
        public string[] MustNotContain { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Version), false)]
        public long? Version { get; }
    }

    public sealed class AcquisitionProfileDetailsV1 : BaseResponse
    {
        public AcquisitionProfileDetailsV1(long id, string name, MediaType mediaTypeId, bool isDefault, string[] allowedQualities, bool upgradeAllowed, string? upgradeCutoffQuality,
            int? minimumSizeMb, int? maximumSizeMb, string[] mustContain, string[] mustNotContain, int wantedCount, long version)
        {
            Id = id;
            Name = name;
            MediaTypeId = mediaTypeId;
            IsDefault = isDefault;
            AllowedQualities = allowedQualities;
            UpgradeAllowed = upgradeAllowed;
            UpgradeCutoffQuality = upgradeCutoffQuality;
            MinimumSizeMb = minimumSizeMb;
            MaximumSizeMb = maximumSizeMb;
            MustContain = mustContain;
            MustNotContain = mustNotContain;
            WantedCount = wantedCount;
            Version = version;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Name))]
        public string Name { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.IsDefault))]
        public bool IsDefault { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.AllowedQualities))]
        public string[] AllowedQualities { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.UpgradeAllowed))]
        public bool UpgradeAllowed { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.UpgradeCutoffQuality))]
        public string? UpgradeCutoffQuality { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.MinimumSizeMb))]
        public int? MinimumSizeMb { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.MaximumSizeMb))]
        public int? MaximumSizeMb { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.MustContain))]
        public string[] MustContain { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.MustNotContain))]
        public string[] MustNotContain { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.WantedCount))]
        public int WantedCount { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Version))]
        public long Version { get; }
    }

    public sealed class CreateAcquisitionProfileResultV1 : BaseResponse
    {
        public CreateAcquisitionProfileResultV1(long id)
        {
            Id = id;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long Id { get; }
    }
}
