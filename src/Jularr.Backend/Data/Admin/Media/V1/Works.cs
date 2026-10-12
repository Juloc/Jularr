using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    public sealed class CreateWorkParametersV1 : BaseParameters
    {
    }

    public sealed class ReadWorksParametersV1 : BaseParameters
    {
        public ReadWorksParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class WorkParametersV1 : BaseParameters
    {
        public WorkParametersV1(long workId)
        {
            WorkId = workId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }
    }

    public sealed class WorkDetailsV1 : BaseResponse
    {
        public WorkDetailsV1(long id, MediaType mediaTypeId, string canonicalTitle, int? releaseYear, long version)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            CanonicalTitle = canonicalTitle;
            ReleaseYear = releaseYear;
            Version = version;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Version))]
        public long Version { get; }
    }

    public sealed class CreateWorkDataV1 : BaseData
    {
        public CreateWorkDataV1(MediaType mediaTypeId, string canonicalTitle, int? releaseYear = null)
        {
            MediaTypeId = mediaTypeId;
            CanonicalTitle = canonicalTitle;
            ReleaseYear = releaseYear;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }
    }

    public sealed class UpdateWorkDataV1 : BaseData
    {
        public UpdateWorkDataV1(string canonicalTitle, long version, int? releaseYear = null)
        {
            CanonicalTitle = canonicalTitle;
            Version = version;
            ReleaseYear = releaseYear;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Version))]
        public long Version { get; }
    }
}
