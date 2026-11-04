using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class CustomFormatImportData : BaseData
    {
        public CustomFormatImportData(int sourceId, string name, bool includeWhenRenaming, FormatCondition[] conditions)
        {
            SourceId = sourceId;
            Name = name;
            IncludeWhenRenaming = includeWhenRenaming;
            Conditions = conditions;
        }

        [DataField(typeof(FormatScoreFields), nameof(FormatScoreFields.SourceId))]
        public int SourceId { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Name))]
        public string Name { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.IncludeWhenRenaming))]
        public bool IncludeWhenRenaming { get; }

        public FormatCondition[] Conditions { get; }
    }

    internal sealed class CustomFormatBatchData : BaseData
    {
        public CustomFormatBatchData(string rows)
        {
            Rows = rows;
        }

        [DataField(typeof(FormatScoreFields), nameof(FormatScoreFields.ImportRows))]
        public string Rows { get; }
    }

    internal sealed class ImportedCustomFormatKeyData : BaseData
    {
        public ImportedCustomFormatKeyData(int sourceId, long id, long version)
        {
            SourceId = sourceId;
            Id = id;
            Version = version;
        }

        [DataField(typeof(FormatScoreFields), nameof(FormatScoreFields.SourceId))]
        public int SourceId { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Id))]
        public long Id { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Version))]
        public long Version { get; }
    }
}
