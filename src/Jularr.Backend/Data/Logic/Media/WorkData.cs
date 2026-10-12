using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    public sealed class WorkCreationData : BaseData
    {
        public WorkCreationData(MediaType mediaTypeId, string canonicalTitle, int? releaseYear = null)
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

    public sealed class WorkUpdateData : BaseData
    {
        public WorkUpdateData(long id, string canonicalTitle, long version, int? releaseYear = null)
        {
            Id = id;
            CanonicalTitle = canonicalTitle;
            Version = version;
            ReleaseYear = releaseYear;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Version))]
        public long Version { get; }
    }

    public sealed class WorkData : BaseData
    {
        public WorkData(long id, MediaType mediaTypeId, string canonicalTitle, int? releaseYear, long version)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            CanonicalTitle = canonicalTitle;
            ReleaseYear = releaseYear;
            Version = version;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; set; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; set; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; set; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; set; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Version))]
        public long Version { get; set; }
    }
}
