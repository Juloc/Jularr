using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class ReleaseProfileWriteData : BaseData
    {
        public ReleaseProfileWriteData(long? id, MediaType? mediaTypeId, string name, bool isEnabled, string[] required, string[] ignored, long? acquisitionConnectionId, long[] tagIds, long? version)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            Name = name;
            IsEnabled = isEnabled;
            Required = required;
            Ignored = ignored;
            AcquisitionConnectionId = acquisitionConnectionId;
            TagIds = tagIds;
            Version = version;
        }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Id), false)]
        public long? Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId), false)]
        public MediaType? MediaTypeId { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Name))]
        public string Name { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Terms))]
        public string[] Required { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Terms))]
        public string[] Ignored { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id), false)]
        public long? AcquisitionConnectionId { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.TagIds))]
        public long[] TagIds { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Version), false)]
        public long? Version { get; }
    }
    internal sealed class ReleaseProfileRowData : BaseData
    {
        public ReleaseProfileRowData(long id, MediaType? mediaTypeId, string name, bool isEnabled, string[] required, string[] ignored, long? acquisitionConnectionId, long[] tagIds, long version)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            Name = name;
            IsEnabled = isEnabled;
            Required = required;
            Ignored = ignored;
            AcquisitionConnectionId = acquisitionConnectionId;
            TagIds = tagIds;
            Version = version;
        }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId), false)]
        public MediaType? MediaTypeId { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Name))]
        public string Name { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Terms))]
        public string[] Required { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Terms))]
        public string[] Ignored { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id), false)]
        public long? AcquisitionConnectionId { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.TagIds))]
        public long[] TagIds { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Version))]
        public long Version { get; }
    }
    internal sealed class ReleaseProfileKeyData : BaseData
    {
        public ReleaseProfileKeyData(long id, long version)
        {
            Id = id;
            Version = version;
        }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Version))]
        public long Version { get; }
    }
    internal sealed class AcquisitionTagWriteData : BaseData
    {
        public AcquisitionTagWriteData(long? id, string label, long? version)
        {
            Id = id;
            Label = label;
            Version = version;
        }

        [DataField(typeof(AcquisitionTagFields), nameof(AcquisitionTagFields.Id), false)]
        public long? Id { get; }

        [DataField(typeof(AcquisitionTagFields), nameof(AcquisitionTagFields.Label))]
        public string Label { get; }

        [DataField(typeof(AcquisitionTagFields), nameof(AcquisitionTagFields.Version), false)]
        public long? Version { get; }
    }
    internal sealed class AcquisitionTagRowData : BaseData
    {
        public AcquisitionTagRowData(long id, string label, long version)
        {
            Id = id;
            Label = label;
            Version = version;
        }

        [DataField(typeof(AcquisitionTagFields), nameof(AcquisitionTagFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionTagFields), nameof(AcquisitionTagFields.Label))]
        public string Label { get; }

        [DataField(typeof(AcquisitionTagFields), nameof(AcquisitionTagFields.Version))]
        public long Version { get; }
    }
    internal sealed class AcquisitionTagKeyData : BaseData
    {
        public AcquisitionTagKeyData(long id, long version)
        {
            Id = id;
            Version = version;
        }

        [DataField(typeof(AcquisitionTagFields), nameof(AcquisitionTagFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionTagFields), nameof(AcquisitionTagFields.Version))]
        public long Version { get; }
    }
}
