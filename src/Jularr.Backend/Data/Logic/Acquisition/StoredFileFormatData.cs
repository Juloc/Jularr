using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class StoredFileFormatEvaluationData : BaseData
    {
        public StoredFileFormatEvaluationData(long storedFileId, long customFormatId, long formatVersion, string name, string conditions, string evidence)
        {
            StoredFileId = storedFileId;
            CustomFormatId = customFormatId;
            FormatVersion = formatVersion;
            Name = name;
            Conditions = conditions;
            Evidence = evidence;
        }
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }
        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Id))]
        public long CustomFormatId { get; }
        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Version))]
        public long FormatVersion { get; }
        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Name))]
        public string Name { get; }
        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Conditions))]
        public string Conditions { get; }
        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Rows))]
        public string Evidence { get; }
    }

    internal sealed class StoredFileLanguageEvidenceData : BaseData
    {
        public StoredFileLanguageEvidenceData(long storedFileId, string evidence)
        {
            StoredFileId = storedFileId;
            Evidence = evidence;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Payload))]
        public string Evidence { get; }
    }

    internal sealed class StoredFileEvidenceBatchData : BaseData
    {
        public StoredFileEvidenceBatchData(string rows) { Rows = rows; }
        [DataField(typeof(FormatScoreFields), nameof(FormatScoreFields.ImportRows))]
        public string Rows { get; }
    }

    internal sealed record StoredReleaseEvidence(string ReleaseName, long SizeBytes, string[] IndexerFlags, bool Movie, string? OriginalLanguage, int? Year, MediaType MediaTypeId);
    internal sealed record StoredFileFormatMatch(long StoredFileId, long CustomFormatId, long FormatVersion, string Evidence, bool Matches, bool IsValid);
}
