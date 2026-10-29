using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.User.Media.V1
{
    // A title related to a Work, as the Work sees it: a Work the profile may open.
    public sealed class WorkRelationV1 : BaseResponse
    {
        public WorkRelationV1(long? workRelationId, WorkRelationType workRelationTypeId, string sourceKey, long relatedWorkId, MediaType mediaTypeId,
            MediaSubtype? mediaSubtypeId, string title, int? releaseYear, string? posterUrl, MediaAvailabilityState? availabilityStateId = null, int availableUnits = 0, int monitoredUnits = 0)
        {
            WorkRelationId = workRelationId;
            WorkRelationTypeId = workRelationTypeId;
            SourceKey = sourceKey;
            RelatedWorkId = relatedWorkId;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            Title = title;
            ReleaseYear = releaseYear;
            PosterUrl = posterUrl;
            AvailabilityStateId = availabilityStateId;
            AvailableUnits = availableUnits;
            MonitoredUnits = monitoredUnits;
        }

        // The stored relation, which a person may remove; none for a title of the same series.
        [DataField(typeof(WorkRelationFields), nameof(WorkRelationFields.Id), false)]
        public long? WorkRelationId { get; }

        [DataField(typeof(WorkRelationFields), nameof(WorkRelationFields.WorkRelationTypeId))]
        public WorkRelationType WorkRelationTypeId { get; }

        // Who made the relation: the provider's key or manual.
        [DataField(typeof(WorkRelationFields), nameof(WorkRelationFields.SourceKey))]
        public string SourceKey { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long RelatedWorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ImageUrl))]
        public string? PosterUrl { get; }

        // What the library and the acquisition say of a related Work; none for a catalog title without details the profile has neither a file, a request nor a wanted entry of.
        [DataField(typeof(WorkFields), nameof(WorkFields.AvailabilityStateId), false)]
        public MediaAvailabilityState? AvailabilityStateId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.AvailableUnits))]
        public int AvailableUnits { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MonitoredUnits))]
        public int MonitoredUnits { get; }
    }
}
