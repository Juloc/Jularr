using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    public sealed class WorkCreationData : BaseData
    {
        public WorkCreationData(MediaType mediaTypeId, string canonicalTitle)
        {
            MediaTypeId = mediaTypeId;
            CanonicalTitle = canonicalTitle;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }
    }

    public sealed class WorkUpdateData : BaseData
    {
        public WorkUpdateData(long id, string canonicalTitle, long version)
        {
            Id = id;
            CanonicalTitle = canonicalTitle;
            Version = version;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Version))]
        public long Version { get; }
    }

    public sealed class WorkData : BaseData
    {
        public WorkData(long id, MediaType mediaTypeId, string canonicalTitle, long version)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            CanonicalTitle = canonicalTitle;
            Version = version;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; set; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; set; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; set; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Version))]
        public long Version { get; set; }
    }
}
