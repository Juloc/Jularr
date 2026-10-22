using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.User.Media.V1
{
    // A title related to a Work, as the Work sees it. The title is a Work the profile may open or a catalog title that is none (the namespace and id name it for Discover).
    public sealed class WorkRelationV1 : BaseResponse
    {
        public WorkRelationV1(long? workRelationId, WorkRelationType workRelationTypeId, string sourceKey, long? relatedWorkId, string? @namespace, string? externalId, MediaType mediaTypeId,
            MediaSubtype? mediaSubtypeId, string title, int? releaseYear, string? posterUrl, MediaAvailabilityState? availabilityStateId = null, int availableUnits = 0, int monitoredUnits = 0)
        {
            WorkRelationId = workRelationId;
            WorkRelationTypeId = workRelationTypeId;
            SourceKey = sourceKey;
            RelatedWorkId = relatedWorkId;
            Namespace = @namespace;
            ExternalId = externalId;
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

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? RelatedWorkId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Namespace), false)]
        public string? Namespace { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ExternalId), false)]
        public string? ExternalId { get; }

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

        // What the library and the acquisition say of a related Work; none for a title that is no Work.
        [DataField(typeof(WorkFields), nameof(WorkFields.AvailabilityStateId), false)]
        public MediaAvailabilityState? AvailabilityStateId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.AvailableUnits))]
        public int AvailableUnits { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MonitoredUnits))]
        public int MonitoredUnits { get; }
    }
}
