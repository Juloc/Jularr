using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class ReadAcquisitionProfilesParametersV1 : BaseParameters
    {
        public ReadAcquisitionProfilesParametersV1(long? wantedItemId = null)
        {
            WantedItemId = wantedItemId;
        }

        // Only the profiles that fit this target's type and subtype.
        [DataField(typeof(Fields.Acquisition.WantedItemFields), nameof(Fields.Acquisition.WantedItemFields.Id), false)]
        public long? WantedItemId { get; }
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
            int? maximumSizeMb, string[] mustContain, string[] mustNotContain, long? version = null, MediaSubtype? mediaSubtypeId = null, int delayMinutes = 0,
            string[]? fallbackQualities = null, int? fallbackAfterHours = null)
        {
            DelayMinutes = delayMinutes;
            FallbackQualities = fallbackQualities ?? [];
            FallbackAfterHours = fallbackAfterHours;
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

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Name))]
        public string Name { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

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

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.DelayMinutes))]
        public int DelayMinutes { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.FallbackQualities))]
        public string[] FallbackQualities { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.FallbackAfterHours))]
        public int? FallbackAfterHours { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Version), false)]
        public long? Version { get; }
    }

    public sealed class AcquisitionProfileDetailsV1 : BaseResponse
    {
        public AcquisitionProfileDetailsV1(long id, string name, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, bool isDefault, string[] allowedQualities, bool upgradeAllowed, string? upgradeCutoffQuality,
            int? minimumSizeMb, int? maximumSizeMb, string[] mustContain, string[] mustNotContain, int wantedCount, long version, int delayMinutes = 0, string[]? fallbackQualities = null,
            int? fallbackAfterHours = null)
        {
            DelayMinutes = delayMinutes;
            FallbackQualities = fallbackQualities ?? [];
            FallbackAfterHours = fallbackAfterHours;
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
            WantedCount = wantedCount;
            Version = version;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Name))]
        public string Name { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

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

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.DelayMinutes))]
        public int DelayMinutes { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.FallbackQualities))]
        public string[] FallbackQualities { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.FallbackAfterHours))]
        public int? FallbackAfterHours { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.WantedCount))]
        public int WantedCount { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Version))]
        public long Version { get; }
    }

    // A release rule; Require and Reject decide, Prefer and Avoid score, Info only names a match.
    public sealed class ReleaseRuleV1 : BaseResponse
    {
        public ReleaseRuleV1(int position, string name, ReleaseRuleField field, ReleaseRuleMatch match, string value, ReleaseRuleEffect effect, int score)
        {
            Position = position;
            Name = name;
            Field = field;
            Match = match;
            Value = value;
            Effect = effect;
            Score = score;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.RulePosition))]
        public int Position { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.RuleName))]
        public string Name { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.RuleField))]
        public ReleaseRuleField Field { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.RuleMatch))]
        public ReleaseRuleMatch Match { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.RuleValue))]
        public string Value { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.RuleEffect))]
        public ReleaseRuleEffect Effect { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.RuleScore))]
        public int Score { get; }
    }

    public sealed class SaveReleaseRuleDataV1 : BaseData
    {
        public SaveReleaseRuleDataV1(string name, ReleaseRuleField field, ReleaseRuleMatch match, string value, ReleaseRuleEffect effect, int score, long version)
        {
            Name = name;
            Field = field;
            Match = match;
            Value = value;
            Effect = effect;
            Score = score;
            Version = version;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.RuleName))]
        public string Name { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.RuleField))]
        public ReleaseRuleField Field { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.RuleMatch))]
        public ReleaseRuleMatch Match { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.RuleValue))]
        public string Value { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.RuleEffect))]
        public ReleaseRuleEffect Effect { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.RuleScore))]
        public int Score { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Version))]
        public long Version { get; }
    }

    public sealed class ReleaseRulePositionParametersV1 : BaseParameters
    {
        public ReleaseRulePositionParametersV1(long acquisitionProfileId, int position)
        {
            AcquisitionProfileId = acquisitionProfileId;
            Position = position;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long AcquisitionProfileId { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.RulePosition))]
        public int Position { get; }
    }

    public sealed class ReleaseRuleParametersV1 : BaseParameters
    {
        public ReleaseRuleParametersV1(long acquisitionProfileId, int position, long version)
        {
            AcquisitionProfileId = acquisitionProfileId;
            Position = position;
            Version = version;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long AcquisitionProfileId { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.RulePosition))]
        public int Position { get; }

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
