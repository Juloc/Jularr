using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    public sealed class WorkRelationParametersV1 : BaseParameters
    {
        public WorkRelationParametersV1(long workId, long workRelationId)
        {
            WorkId = workId;
            WorkRelationId = workRelationId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkRelationFields), nameof(WorkRelationFields.Id))]
        public long WorkRelationId { get; }
    }

    // What the related title is to the Work; the title is a Work of the library or a catalog title that Discover knows.
    public sealed class CreateWorkRelationDataV1 : BaseData
    {
        public CreateWorkRelationDataV1(WorkRelationType workRelationTypeId, long? relatedWorkId = null, string? @namespace = null, string? externalId = null)
        {
            WorkRelationTypeId = workRelationTypeId;
            RelatedWorkId = relatedWorkId;
            Namespace = @namespace;
            ExternalId = externalId;
        }

        [DataField(typeof(WorkRelationFields), nameof(WorkRelationFields.WorkRelationTypeId))]
        public WorkRelationType WorkRelationTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? RelatedWorkId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Namespace), false)]
        public string? Namespace { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ExternalId), false)]
        public string? ExternalId { get; }
    }
}
