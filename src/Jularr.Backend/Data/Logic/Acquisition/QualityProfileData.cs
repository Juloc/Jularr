using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    // Which indexers an Acquisition Profile may search and which it prefers, by the canonical entry id of the Indexer settings (the profile never copies an indexer's configuration).
    internal sealed class AcquisitionSourcePolicy
    {
        public AcquisitionSourcePolicy(long[] allowedEntryIds, long[] preferredEntryIds)
        {
            AllowedEntryIds = allowedEntryIds;
            PreferredEntryIds = preferredEntryIds;
        }

        public long[] AllowedEntryIds { get; }
        public long[] PreferredEntryIds { get; }
        public static AcquisitionSourcePolicy Unrestricted { get; } = new([], []);

        // Indexer entries that are searched only when the other sources of the search returned no release.
        public long[] FallbackOnlyEntryIds { get; init; } = [];

        public bool IsRestricted => AllowedEntryIds.Length > 0;

        public bool IsDefault => AllowedEntryIds.Length == 0 && PreferredEntryIds.Length == 0 && FallbackOnlyEntryIds.Length == 0;
    }

    internal sealed class QualityProfile
    {
        public QualityProfile(string id, string name, string[] allowedQualities, string[] qualityOrder, bool upgradeAllowed, string? upgradeCutoffQuality, long? minimumSizeBytes, long? maximumSizeBytes)
        {
            Id = id;
            Name = name;
            AllowedQualities = allowedQualities;
            QualityOrder = qualityOrder;
            UpgradeAllowed = upgradeAllowed;
            UpgradeCutoffQuality = upgradeCutoffQuality;
            MinimumSizeBytes = minimumSizeBytes;
            MaximumSizeBytes = maximumSizeBytes;
        }

        public string Id { get; }
        public string Name { get; }
        public string[] AllowedQualities { get; }
        public string[] QualityOrder { get; }
        public bool UpgradeAllowed { get; }
        public string? UpgradeCutoffQuality { get; }
        public long? MinimumSizeBytes { get; }
        public long? MaximumSizeBytes { get; }
        public int UpgradeMinimumQualitySteps { get; init; } = 1;
        public bool AllowAmbiguousIdentity { get; init; }
        public string[] LanguageOrder { get; init; } = [];
        public bool IsMovie { get; init; }
        public bool IsBook { get; init; }
        public IReadOnlyList<(CustomFormat Format, int Score)> Formats { get; init; } = [];
        public int MinimumFormatScore { get; init; }
        public int CutoffFormatScore { get; init; }
        public int MinimumUpgradeFormatScore { get; init; } = 1;
        public IReadOnlyList<ReleaseTerms> ReleaseProfiles { get; init; } = [];
        public IReadOnlyDictionary<string, QualitySize> Sizes { get; init; } = new Dictionary<string, QualitySize>();
        public int? RuntimeMinutes { get; init; }
        public QualityGroup[] QualityGroups { get; init; } = [];
        public string? OriginalLanguage { get; init; }

        // The fallback when no file carries an asked language: the original language of the work, English, or nothing.
        public LanguageFallbackType LanguageFallback { get; init; } = LanguageFallbackType.Any;
        public int? Year { get; init; }
        public AcquisitionSourcePolicy SourcePolicy { get; init; } = AcquisitionSourcePolicy.Unrestricted;

        public string? Item(string? quality) => quality is null ? null
            : QualityGroups.FirstOrDefault(group => group.Qualities.Contains(quality, StringComparer.OrdinalIgnoreCase))?.Key ?? quality;

    }

    // A release profile's terms, for one indexer or all.
    internal sealed record ReleaseTerms(string[] Required, string[] Ignored, long? IndexerId);

    // A quality's size limits in megabytes per minute.
    internal sealed record QualitySize(string Quality, decimal Minimum, decimal? Preferred, decimal? Maximum);

    // Qualities that rank as one, as Sonarr's profile groups them.
    internal sealed record QualityGroup(long Id, string Name, string[] Qualities)
    {
        [JsonIgnore]
        public string Key => ProfileQualities.Group(Id);
    }

    internal sealed class ReleaseCandidate
    {
        public ReleaseCandidate(ReleaseInfo release, long? sizeBytes = null, string? indexer = null, string? sourceId = null)
        {
            Release = release;
            SizeBytes = sizeBytes;
            Indexer = indexer;
            SourceId = sourceId;
        }

        public ReleaseInfo Release { get; }
        public long? SizeBytes { get; }
        public string? Indexer { get; }
        public string? SourceId { get; }

        // The indexer entry the release came from.
        public long? IndexerId { get; init; }

        // The flags the indexer gives the release.
        public IReadOnlyList<string> IndexerFlags { get; init; } = [];
        public IReadOnlyList<string>? Languages { get; init; }
        public decimal? RuntimeMinutes { get; init; }
    }

    internal sealed class ReleaseScoreResult
    {
        public ReleaseScoreResult(ReleaseCandidate candidate, bool accepted, int score, string qualityKey, int qualityRank, IReadOnlyList<ReleaseReason> rejectionReasons, IReadOnlyList<ReleaseReason> scoreReasons)
        {
            Candidate = candidate;
            Accepted = accepted;
            Score = score;
            QualityKey = qualityKey;
            QualityRank = qualityRank;
            RejectionReasons = rejectionReasons;
            ScoreReasons = scoreReasons;
        }

        public ReleaseCandidate Candidate { get; }
        public bool Accepted { get; }
        public int Score { get; }
        public string QualityKey { get; }
        public int QualityRank { get; }
        public IReadOnlyList<ReleaseReason> RejectionReasons { get; }
        public IReadOnlyList<ReleaseReason> ScoreReasons { get; }

        // Rules that matched with the Info effect.
        public IReadOnlyList<string> InfoReasons { get; init; } = [];

        // The sum of the matching custom formats' scores.
        public int FormatScore { get; init; }
    }

    internal sealed class UpgradeAssessment
    {
        public UpgradeAssessment(UpgradeState state, ReleaseReason reason)
        {
            State = state;
            Reason = reason;
        }

        public UpgradeState State { get; }
        public ReleaseReason Reason { get; }

        public bool IsUpgradable => State == UpgradeState.Upgradable;
    }

    internal sealed class AcquisitionProfileDefinition
    {
        public AcquisitionProfileDefinition(string[] allowedQualities, string[] qualityOrder, bool upgradeAllowed, string? upgradeCutoffQuality, long? minimumSizeBytes, long? maximumSizeBytes)
        {
            AllowedQualities = allowedQualities;
            QualityOrder = qualityOrder;
            UpgradeAllowed = upgradeAllowed;
            UpgradeCutoffQuality = upgradeCutoffQuality;
            MinimumSizeBytes = minimumSizeBytes;
            MaximumSizeBytes = maximumSizeBytes;
        }

        public string[] AllowedQualities { get; }
        public string[] QualityOrder { get; }
        public bool UpgradeAllowed { get; }
        public string? UpgradeCutoffQuality { get; }
        public long? MinimumSizeBytes { get; }
        public long? MaximumSizeBytes { get; }
        public ReleaseMonitoring? Monitoring { get; init; }
        public FormatScore[] FormatScores { get; init; } = [];
        public QualityGroup[] QualityGroups { get; init; } = [];
        public int MinimumFormatScore { get; init; }
        public int CutoffFormatScore { get; init; }
        public int MinimumUpgradeFormatScore { get; init; } = 1;

        // The languages of the choice, in order; empty takes the languages the request asks for.
        public string[] LanguageOrder { get; init; } = [];

        // The fallback when no file carries an asked language: the original language of the work, English, or nothing.
        public LanguageFallbackType LanguageFallback { get; init; } = LanguageFallbackType.Any;

    }

    // An artist's release types and statuses, an author's languages and least pages; empty lists allow everything.
    internal sealed class ReleaseMonitoring
    {
        public ReleaseMonitoring(string[] primaryTypes, string[] secondaryTypes, string[] releaseStatuses, string[] languages, int? minimumPages)
        {
            PrimaryTypes = primaryTypes;
            SecondaryTypes = secondaryTypes;
            ReleaseStatuses = releaseStatuses;
            Languages = languages;
            MinimumPages = minimumPages;
        }

        public string[] PrimaryTypes { get; }
        public string[] SecondaryTypes { get; }
        public string[] ReleaseStatuses { get; }
        public string[] Languages { get; }
        public int? MinimumPages { get; }

        public bool AllowsType(string? type) => type is null || PrimaryTypes.Length == 0 || PrimaryTypes.Contains(type, StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class TakenOverProfile
    {
        public TakenOverProfile(AcquisitionProfileInputData input, IReadOnlyList<CustomFormatImportData> formats, IReadOnlyList<string> skippedFormats)
        {
            Input = input;
            Formats = formats;
            SkippedFormats = skippedFormats;
        }

        public AcquisitionProfileInputData Input { get; }
        public IReadOnlyList<CustomFormatImportData> Formats { get; }
        public IReadOnlyList<string> SkippedFormats { get; }
    }

    // What became of a taken-over profile, and the Jularr profile its titles use.
    internal sealed record TakenOverProfileOutcome(string OutcomeKey, long? ProfileId);

    // A profile's name and media type; a takeover does not create the same twice.
    internal sealed class AcquisitionProfileNameData : BaseData
    {
        public AcquisitionProfileNameData(long id, string name, MediaType mediaTypeId)
        {
            Id = id;
            Name = name;
            MediaTypeId = mediaTypeId;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Name))]
        public string Name { get; }

        [DataField(typeof(Fields.Media.WorkFields), nameof(Fields.Media.WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }
    }

    internal sealed class TakeoverLibraryData : BaseData
    {
        public TakeoverLibraryData(long id, string name, MediaType mediaTypeId, string relativePath, long storageMountId)
        {
            Id = id;
            Name = name;
            MediaTypeId = mediaTypeId;
            RelativePath = relativePath;
            StorageMountId = storageMountId;
        }

        [DataField(typeof(Fields.Library.LibraryRootFields), nameof(Fields.Library.LibraryRootFields.Id))]
        public long Id { get; }

        [DataField(typeof(Fields.Library.LibraryRootFields), nameof(Fields.Library.LibraryRootFields.Name))]
        public string Name { get; }

        [DataField(typeof(Fields.Library.LibraryRootFields), nameof(Fields.Library.LibraryRootFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(Fields.Library.LibraryRootFields), nameof(Fields.Library.LibraryRootFields.RelativePath))]
        public string RelativePath { get; }
        [DataField(typeof(Fields.Storage.StorageMountFields), nameof(Fields.Storage.StorageMountFields.Id))]
        public long StorageMountId { get; }
    }
}
