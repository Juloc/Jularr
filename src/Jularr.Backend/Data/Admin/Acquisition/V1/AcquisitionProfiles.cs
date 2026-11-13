using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Acquisition;
using System.Text.Json.Serialization;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class ReadAcquisitionProfilesParametersV1 : BaseParameters
    {
        public ReadAcquisitionProfilesParametersV1(long? wantedItemId = null, AcquisitionMediaKind? mediaKind = null)
        {
            WantedItemId = wantedItemId;
            MediaKind = mediaKind;
        }

        // Only the profiles that apply to the target's Work, or to one media type.
        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id), false)]
        public long? WantedItemId { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.MediaKind), false)]
        public AcquisitionMediaKind? MediaKind { get; }
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
    public sealed class CustomFormatScoreV1 : BaseData
    {
        public CustomFormatScoreV1(long formatId, int score)
        {
            FormatId = formatId;
            Score = score;
        }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Id))]
        public long FormatId { get; }

        [DataField(typeof(FormatScoreFields), nameof(FormatScoreFields.Score))]
        public int Score { get; }
    }
    public sealed class QualityGroupV1 : BaseData
    {
        public QualityGroupV1(long id, string name, string[] qualities)
        {
            Id = id;
            Name = name;
            Qualities = qualities;
        }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Id))]
        public long Id { get; }

        [JsonIgnore]
        public string Key => ProfileQualities.Group(Id);

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.GroupName))]
        public string Name { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.AllowedQualities))]
        public string[] Qualities { get; }
    }
    public sealed class SaveAcquisitionProfileDataV1 : BaseData
    {
        public SaveAcquisitionProfileDataV1(string name, AcquisitionMediaKind[] mediaKinds, AcquisitionMediaKind[] defaultFor, string[] allowedQualities, bool upgradeAllowed,
            string? upgradeCutoffQuality, int? minimumSizeMb, int? maximumSizeMb, long? version = null, string[]? qualityOrder = null,
            BaseDataList<QualityGroupV1>? qualityGroups = null, BaseDataList<CustomFormatScoreV1>? formatScores = null,
            int minimumFormatScore = 0, int cutoffFormatScore = 0, int minimumUpgradeFormatScore = 1, string[]? languageOrder = null,
            LanguageFallbackType languageFallback = LanguageFallbackType.Any, int goodGroupScore = AcquisitionProfileDefinition.DefaultGoodGroupScore,
            int badGroupScore = AcquisitionProfileDefinition.DefaultBadGroupScore, bool blockBadSources = true, CodecPreference codecPreference = CodecPreference.Any,
            SizePreference sizePreference = SizePreference.Any)
        {
            BlockBadSources = blockBadSources;
            CodecPreference = codecPreference;
            SizePreference = sizePreference;
            Name = name;
            MediaKinds = mediaKinds;
            DefaultFor = defaultFor;
            AllowedQualities = allowedQualities;
            UpgradeAllowed = upgradeAllowed;
            UpgradeCutoffQuality = upgradeCutoffQuality;
            MinimumSizeMb = minimumSizeMb;
            MaximumSizeMb = maximumSizeMb;
            Version = version;
            QualityOrder = qualityOrder ?? [];
            QualityGroups = qualityGroups ?? new([]);
            FormatScores = formatScores ?? new([]);
            MinimumFormatScore = minimumFormatScore;
            CutoffFormatScore = cutoffFormatScore;
            MinimumUpgradeFormatScore = minimumUpgradeFormatScore;
            LanguageOrder = languageOrder ?? [];
            LanguageFallback = languageFallback;
            GoodGroupScore = goodGroupScore;
            BadGroupScore = badGroupScore;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Name))]
        public string Name { get; }

        // The media types the profile applies to and those it is the default of.
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

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.AllowedQualities))]
        public string[] QualityOrder { get; }

        public BaseDataList<QualityGroupV1> QualityGroups { get; }

        public BaseDataList<CustomFormatScoreV1> FormatScores { get; }

        [DataField(typeof(FormatScoreFields), nameof(FormatScoreFields.Score))]
        public int MinimumFormatScore { get; }

        [DataField(typeof(FormatScoreFields), nameof(FormatScoreFields.Score))]
        public int CutoffFormatScore { get; }

        [DataField(typeof(FormatScoreFields), nameof(FormatScoreFields.MinimumUpgrade))]
        public int MinimumUpgradeFormatScore { get; }

        // The languages of the choice, in order; empty takes the languages the request asks for.
        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.LanguageOrder), false)]
        public string[] LanguageOrder { get; }

        // The fallback when no file carries an asked language: the original language of the work, English, or nothing.
        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.LanguageFallback), false)]
        public LanguageFallbackType LanguageFallback { get; }

        // The points of a release group rated good or bad.
        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.GoodScore))]
        public int GoodGroupScore { get; }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.BadScore))]
        public int BadGroupScore { get; }

        // The simple switches: bad sources never taken, x265 liked or avoided, the smaller or the larger file first.
        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.BlockBadSources), false)]
        public bool BlockBadSources { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.CodecPreference), false)]
        public CodecPreference CodecPreference { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.SizePreference), false)]
        public SizePreference SizePreference { get; }

        public override void Validate()
        {
            base.Validate();
            if (FormatScores.Count > 1000 || QualityGroups.Count > 32) throw new DataValidationException(nameof(FormatScores), "out_of_range");
            FormatScores.Validate(nameof(FormatScores));
            QualityGroups.Validate(nameof(QualityGroups));
            if (FormatScores.Select(item => item.FormatId).Distinct().Count() != FormatScores.Count) throw new DataValidationException(nameof(FormatScores), "duplicate_value");
        }

    }
    public sealed class AcquisitionProfileDetailsV1 : BaseResponse
    {
        public AcquisitionProfileDetailsV1(long id, string name, AcquisitionMediaKind[] mediaKinds, AcquisitionMediaKind[] defaultFor, string[] allowedQualities, bool upgradeAllowed,
            string? upgradeCutoffQuality, int? minimumSizeMb, int? maximumSizeMb, string[]? qualityOrder, BaseDataList<QualityGroupV1>? qualityGroups,
            BaseDataList<CustomFormatScoreV1>? formatScores, int minimumFormatScore, int cutoffFormatScore, int minimumUpgradeFormatScore,
            int wantedCount, long version, string[] monitoredTypes, string[] monitoredLanguages, int? minimumPages,
            string[]? languageOrder = null, LanguageFallbackType languageFallback = LanguageFallbackType.Any, int goodGroupScore = AcquisitionProfileDefinition.DefaultGoodGroupScore,
            int badGroupScore = AcquisitionProfileDefinition.DefaultBadGroupScore, int ruleCount = 0, int openRequestCount = 0, bool blockBadSources = true,
            CodecPreference codecPreference = CodecPreference.Any, SizePreference sizePreference = SizePreference.Any)
        {
            BlockBadSources = blockBadSources;
            CodecPreference = codecPreference;
            SizePreference = sizePreference;
            RuleCount = ruleCount;
            OpenRequestCount = openRequestCount;
            Id = id;
            Name = name;
            MediaKinds = mediaKinds;
            DefaultFor = defaultFor;
            AllowedQualities = allowedQualities;
            UpgradeAllowed = upgradeAllowed;
            UpgradeCutoffQuality = upgradeCutoffQuality;
            MinimumSizeMb = minimumSizeMb;
            MaximumSizeMb = maximumSizeMb;
            QualityOrder = qualityOrder ?? [];
            QualityGroups = qualityGroups ?? new([]);
            FormatScores = formatScores ?? new([]);
            MinimumFormatScore = minimumFormatScore;
            CutoffFormatScore = cutoffFormatScore;
            MinimumUpgradeFormatScore = minimumUpgradeFormatScore;
            WantedCount = wantedCount;
            Version = version;
            MonitoredTypes = monitoredTypes;
            MonitoredLanguages = monitoredLanguages;
            MinimumPages = minimumPages;
            LanguageOrder = languageOrder ?? [];
            LanguageFallback = languageFallback;
            GoodGroupScore = goodGroupScore;
            BadGroupScore = badGroupScore;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Name))]
        public string Name { get; }

        // The media types the profile applies to and those it is the default of.
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

        // The quality list the profile's media types share.
        [JsonIgnore]
        public QualityFamily QualityFamily => AcquisitionMediaKinds.Family(MediaKinds.Length > 0 ? MediaKinds[0] : AcquisitionMediaKind.Movie);

        // Whether the profile applies to a Work of this type and subtype.
        public bool AppliesTo(MediaType mediaType, MediaSubtype? subtype) => MediaKinds.Contains(AcquisitionMediaKinds.Of(mediaType, subtype));

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.AllowedQualities))]
        public string[] QualityOrder { get; }

        public BaseDataList<QualityGroupV1> QualityGroups { get; }

        public BaseDataList<CustomFormatScoreV1> FormatScores { get; }

        [DataField(typeof(FormatScoreFields), nameof(FormatScoreFields.Score))]
        public int MinimumFormatScore { get; }

        [DataField(typeof(FormatScoreFields), nameof(FormatScoreFields.Score))]
        public int CutoffFormatScore { get; }

        [DataField(typeof(FormatScoreFields), nameof(FormatScoreFields.MinimumUpgrade))]
        public int MinimumUpgradeFormatScore { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.WantedCount))]
        public int WantedCount { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Version))]
        public long Version { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.MonitoredValues))]
        public string[] MonitoredTypes { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.MonitoredValues))]
        public string[] MonitoredLanguages { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.MinimumPages))]
        public int? MinimumPages { get; }

        // The languages of the choice, in order; empty takes the languages the request asks for.
        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.LanguageOrder), false)]
        public string[] LanguageOrder { get; }

        // The fallback when no file carries an asked language: the original language of the work, English, or nothing.
        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.LanguageFallback), false)]
        public LanguageFallbackType LanguageFallback { get; }

        // The points of a release group rated good or bad.
        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.GoodScore))]
        public int GoodGroupScore { get; }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.BadScore))]
        public int BadGroupScore { get; }

        // The simple switches: bad sources never taken, x265 liked or avoided, the smaller or the larger file first.
        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.BlockBadSources), false)]
        public bool BlockBadSources { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.CodecPreference), false)]
        public CodecPreference CodecPreference { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.SizePreference), false)]
        public SizePreference SizePreference { get; }

        // The request rules that offer the profile (deleting it takes it out of them) and the open requests that chose it (they keep it from being deleted).
        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.WantedCount))]
        public int RuleCount { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.WantedCount))]
        public int OpenRequestCount { get; }
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
