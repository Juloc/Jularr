using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    public sealed class WorkCreationData : BaseData
    {
        public WorkCreationData(MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, bool isMediaSubtypeManual, string canonicalTitle, int? releaseYear = null)
        {
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            IsMediaSubtypeManual = isMediaSubtypeManual;
            CanonicalTitle = canonicalTitle;
            ReleaseYear = releaseYear;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        // Scanned files take their library's subtype; providers may still classify them.
        [DataField(typeof(WorkFields), nameof(WorkFields.IsMediaSubtypeManual))]
        public bool IsMediaSubtypeManual { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }
    }

    internal sealed class BookFolderMatchData : BaseData
    {
        public BookFolderMatchData(MediaSubtype? mediaSubtypeId, string canonicalTitle)
        {
            MediaSubtypeId = mediaSubtypeId;
            CanonicalTitle = canonicalTitle;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId), false)]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }
    }

    internal sealed class WorkFolderMatchData : BaseData
    {
        public WorkFolderMatchData(MediaType mediaTypeId, string canonicalTitle, int? releaseYear, string providerIds)
        {
            MediaTypeId = mediaTypeId;
            CanonicalTitle = canonicalTitle;
            ReleaseYear = releaseYear;
            ProviderIds = providerIds;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ExternalIdentities))]
        public string ProviderIds { get; }
    }

    public sealed class WorkUpdateData : BaseData
    {
        public WorkUpdateData(long id, string canonicalTitle, long version, int? releaseYear = null, MediaSubtype? mediaSubtypeId = null)
        {
            Id = id;
            CanonicalTitle = canonicalTitle;
            Version = version;
            ReleaseYear = releaseYear;
            MediaSubtypeId = mediaSubtypeId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Version))]
        public long Version { get; }
    }

    public sealed class WorkData : BaseData
    {
        public WorkData(long id, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, bool isMediaSubtypeManual, string canonicalTitle, int? releaseYear, long version)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            IsMediaSubtypeManual = isMediaSubtypeManual;
            CanonicalTitle = canonicalTitle;
            ReleaseYear = releaseYear;
            Version = version;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; set; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; set; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; set; }

        [DataField(typeof(WorkFields), nameof(WorkFields.IsMediaSubtypeManual))]
        public bool IsMediaSubtypeManual { get; set; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; set; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; set; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Version))]
        public long Version { get; set; }
    }
}
