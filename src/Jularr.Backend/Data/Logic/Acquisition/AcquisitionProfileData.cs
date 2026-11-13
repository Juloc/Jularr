using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class AcquisitionProfileInputData : BaseData
    {
        public AcquisitionProfileInputData(long? id, string name, AcquisitionMediaKind[] mediaKinds, AcquisitionMediaKind[] defaultFor, string[] allowedQualities,
            bool upgradeAllowed, string? upgradeCutoffQuality, int? minimumSizeMb, int? maximumSizeMb, long? version)
        {
            Id = id;
            Name = name;
            MediaKinds = mediaKinds;
            DefaultFor = defaultFor;
            AllowedQualities = allowedQualities;
            UpgradeAllowed = upgradeAllowed;
            UpgradeCutoffQuality = upgradeCutoffQuality;
            MinimumSizeMb = minimumSizeMb;
            MaximumSizeMb = maximumSizeMb;
            Version = version;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id), false)]
        public long? Id { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Name))]
        public string Name { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.MediaKinds))]
        public AcquisitionMediaKind[] MediaKinds { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.DefaultFor))]
        public AcquisitionMediaKind[] DefaultFor { get; }

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

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Version), false)]
        public long? Version { get; }
        public string[] QualityOrder { get; init; } = [];
        public QualityGroup[] QualityGroups { get; init; } = [];
        public FormatScore[] FormatScores { get; init; } = [];
        public int MinimumFormatScore { get; init; }
        public int CutoffFormatScore { get; init; }

        // The languages of the choice, in order; empty takes the languages the request asks for.
        public string[] LanguageOrder { get; init; } = [];

        // The fallback when no file carries an asked language: the original language of the work, English, or nothing.
        public LanguageFallbackType LanguageFallback { get; init; } = LanguageFallbackType.Any;
        public int MinimumUpgradeFormatScore { get; init; } = 1;
        public ReleaseMonitoring? Monitoring { get; init; }

        // The points of a release group rated good or bad.
        public int GoodGroupScore { get; init; } = AcquisitionProfileDefinition.DefaultGoodGroupScore;
        public int BadGroupScore { get; init; } = AcquisitionProfileDefinition.DefaultBadGroupScore;

        // The simple switches: bad sources (CAM, telesync, screener, 3D, upscales) are never taken; x265 is liked or avoided; the smaller or the larger file goes first.
        public bool BlockBadSources { get; init; } = true;
        public CodecPreference CodecPreference { get; init; }
        public SizePreference SizePreference { get; init; }
    }

    internal sealed class AcquisitionProfileRowData : BaseData
    {
        public AcquisitionProfileRowData(long? id, string name, string definition, long? version)
        {
            Id = id;
            Name = name;
            Definition = definition;
            Version = version;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id), false)]
        public long? Id { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Name))]
        public string Name { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Definition))]
        public string Definition { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Version), false)]
        public long? Version { get; }
    }

    internal sealed class AcquisitionProfileStateData : BaseData
    {
        public AcquisitionProfileStateData(long id, AcquisitionMediaKind[] mediaKinds, AcquisitionMediaKind[] defaultFor, string definition, long version)
        {
            Id = id;
            MediaKinds = mediaKinds;
            DefaultFor = defaultFor;
            Definition = definition;
            Version = version;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.MediaKinds))]
        public AcquisitionMediaKind[] MediaKinds { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.DefaultFor))]
        public AcquisitionMediaKind[] DefaultFor { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Definition))]
        public string Definition { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Version))]
        public long Version { get; }
    }

    internal sealed class AcquisitionProfileKindsData : BaseData
    {
        public AcquisitionProfileKindsData(long id, AcquisitionMediaKind[] mediaKinds, AcquisitionMediaKind[] defaultFor)
        {
            Id = id;
            MediaKinds = mediaKinds;
            DefaultFor = defaultFor;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.MediaKinds))]
        public AcquisitionMediaKind[] MediaKinds { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.DefaultFor))]
        public AcquisitionMediaKind[] DefaultFor { get; }
    }

    internal sealed class AcquisitionProfileUseData : BaseData
    {
        public AcquisitionProfileUseData(bool isInUse)
        {
            IsInUse = isInUse;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.IsInUse))]
        public bool IsInUse { get; }
    }

    internal sealed class AcquisitionProfileDefinitionData : BaseData
    {
        public AcquisitionProfileDefinitionData(long id, string definition, long version)
        {
            Id = id;
            Definition = definition;
            Version = version;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Definition))]
        public string Definition { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Version))]
        public long Version { get; }
    }

    // What keeps a profile from being deleted: titles that chose it and open requests for it.
    internal sealed class AcquisitionProfileBlockersData : BaseData
    {
        public AcquisitionProfileBlockersData(bool hasTitles, bool hasRequests)
        {
            HasTitles = hasTitles;
            HasRequests = hasRequests;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.IsInUse))]
        public bool HasTitles { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.IsInUse))]
        public bool HasRequests { get; }
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
