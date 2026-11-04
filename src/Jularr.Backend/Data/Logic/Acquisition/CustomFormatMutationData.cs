using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class CustomFormatRowData : BaseData
    {
        public CustomFormatRowData(long id, string name, bool includeWhenRenaming, string conditions, long version)
        {
            Id = id;
            Name = name;
            IncludeWhenRenaming = includeWhenRenaming;
            Conditions = conditions;
            Version = version;
        }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Id))]
        public long Id { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Name))]
        public string Name { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.IncludeWhenRenaming))]
        public bool IncludeWhenRenaming { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Conditions))]
        public string Conditions { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Version))]
        public long Version { get; }
    }
    internal sealed class CustomFormatWriteData : BaseData
    {
        public CustomFormatWriteData(long? id, string name, bool includeWhenRenaming, string conditions, long? version)
        {
            Id = id;
            Name = name;
            IncludeWhenRenaming = includeWhenRenaming;
            Conditions = conditions;
            Version = version;
        }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Id), false)]
        public long? Id { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Name))]
        public string Name { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.IncludeWhenRenaming))]
        public bool IncludeWhenRenaming { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Conditions))]
        public string Conditions { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Version), false)]
        public long? Version { get; }
    }
    internal sealed class CustomFormatKeyData : BaseData
    {
        public CustomFormatKeyData(long id, long version)
        {
            Id = id;
            Version = version;
        }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Id))]
        public long Id { get; }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Version))]
        public long Version { get; }
    }
}
