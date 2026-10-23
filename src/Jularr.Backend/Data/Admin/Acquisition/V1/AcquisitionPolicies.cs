using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class ReadAcquisitionPolicyParametersV1 : BaseParameters
    {
        public ReadAcquisitionPolicyParametersV1(MediaType mediaTypeId, int page = 1, int pageSize = 25)
        {
            MediaTypeId = mediaTypeId;
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        public int Page { get; }

        public int PageSize { get; }
    }
    public sealed class SaveAcquisitionPolicyParametersV1 : BaseParameters
    {
    }
    public sealed class ReadAcquisitionTagsParametersV1 : BaseParameters
    {
        public ReadAcquisitionTagsParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }
    public sealed class SaveReleaseProfileDataV1 : BaseData
    {
        public SaveReleaseProfileDataV1(long? id, MediaType? mediaTypeId, string name, bool isEnabled, string[] required, string[] ignored, long? acquisitionConnectionId, long[] tagIds, long? version)
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
    public sealed class ReleaseProfileDetailsV1 : BaseResponse
    {
        public ReleaseProfileDetailsV1(long id, MediaType? mediaTypeId, string name, bool isEnabled, string[] required, string[] ignored, long? acquisitionConnectionId, long[] tagIds, long version)
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
    public sealed class ReleaseProfileParametersV1 : BaseParameters
    {
        public ReleaseProfileParametersV1(long releaseProfileId)
        {
            ReleaseProfileId = releaseProfileId;
        }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Id))]
        public long ReleaseProfileId { get; }
    }
    public sealed class DeleteReleaseProfileParametersV1 : BaseParameters
    {
        public DeleteReleaseProfileParametersV1(long releaseProfileId, long version)
        {
            ReleaseProfileId = releaseProfileId;
            Version = version;
        }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Id))]
        public long ReleaseProfileId { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Version))]
        public long Version { get; }
    }
    public sealed class SaveAcquisitionTagDataV1 : BaseData
    {
        public SaveAcquisitionTagDataV1(long? id, string label, long? version)
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
    public sealed class AcquisitionTagDetailsV1 : BaseResponse
    {
        public AcquisitionTagDetailsV1(long id, string label, long version)
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
    public sealed class AcquisitionTagParametersV1 : BaseParameters
    {
        public AcquisitionTagParametersV1(long acquisitionTagId)
        {
            AcquisitionTagId = acquisitionTagId;
        }

        [DataField(typeof(AcquisitionTagFields), nameof(AcquisitionTagFields.Id))]
        public long AcquisitionTagId { get; }
    }
    public sealed class DeleteAcquisitionTagParametersV1 : BaseParameters
    {
        public DeleteAcquisitionTagParametersV1(long acquisitionTagId, long version)
        {
            AcquisitionTagId = acquisitionTagId;
            Version = version;
        }

        [DataField(typeof(AcquisitionTagFields), nameof(AcquisitionTagFields.Id))]
        public long AcquisitionTagId { get; }

        [DataField(typeof(AcquisitionTagFields), nameof(AcquisitionTagFields.Version))]
        public long Version { get; }
    }
    public sealed class SavedAcquisitionPolicyV1 : BaseResponse
    {
        public SavedAcquisitionPolicyV1(long id, long version)
        {
            Id = id;
            Version = version;
        }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Version))]
        public long Version { get; }
    }
}
