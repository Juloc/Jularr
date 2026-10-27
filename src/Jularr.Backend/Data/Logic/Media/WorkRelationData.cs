using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    // A person's relation: the related title is a Work, or a catalog title of Discover named by its identity.
    internal sealed class WorkRelationAddData : BaseData
    {
        public WorkRelationAddData(long workId, WorkRelationType workRelationTypeId, long? relatedWorkId, string? @namespace, string? externalId)
        {
            WorkId = workId;
            WorkRelationTypeId = workRelationTypeId;
            RelatedWorkId = relatedWorkId;
            Namespace = @namespace;
            ExternalId = externalId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkRelationFields), nameof(WorkRelationFields.WorkRelationTypeId))]
        public WorkRelationType WorkRelationTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? RelatedWorkId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Namespace), false)]
        public string? Namespace { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ExternalId), false)]
        public string? ExternalId { get; }
    }

    // A relation as the Work sees it (its id, the type and the related Work) and as it is stored, which may be turned around.
    internal sealed class WorkRelationInsertData : BaseData
    {
        public WorkRelationInsertData(long workId, WorkRelationType workRelationTypeId, long relatedWorkId, long storedWorkId, long storedRelatedWorkId, WorkRelationType storedWorkRelationTypeId)
        {
            WorkId = workId;
            WorkRelationTypeId = workRelationTypeId;
            RelatedWorkId = relatedWorkId;
            StoredWorkId = storedWorkId;
            StoredRelatedWorkId = storedRelatedWorkId;
            StoredWorkRelationTypeId = storedWorkRelationTypeId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkRelationFields), nameof(WorkRelationFields.WorkRelationTypeId))]
        public WorkRelationType WorkRelationTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long RelatedWorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long StoredWorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long StoredRelatedWorkId { get; }

        [DataField(typeof(WorkRelationFields), nameof(WorkRelationFields.WorkRelationTypeId))]
        public WorkRelationType StoredWorkRelationTypeId { get; }
    }

    internal sealed record WorkRelationWorkRow(long Id);

    internal sealed record WorkRelationTargetRow(long RelatedWorkId);

    internal sealed class WorkRelationRemoveData : BaseData
    {
        public WorkRelationRemoveData(long workId, long workRelationId)
        {
            WorkId = workId;
            WorkRelationId = workRelationId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkRelationFields), nameof(WorkRelationFields.Id))]
        public long WorkRelationId { get; }
    }

    // What one provider says a Work is related to; its earlier rows give way to these.
    internal sealed class WorkRelationSyncData : BaseData
    {
        public WorkRelationSyncData(long workId, string sourceKey, IReadOnlyList<WorkRelationTarget> targets)
        {
            WorkId = workId;
            SourceKey = sourceKey;
            Targets = targets;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkRelationFields), nameof(WorkRelationFields.SourceKey))]
        public string SourceKey { get; }

        public IReadOnlyList<WorkRelationTarget> Targets { get; }
    }

    // One related catalog title as a provider names it.
    internal sealed record WorkRelationTarget(string Namespace, string Value, WorkRelationType WorkRelationTypeId, MediaType MediaTypeId, MediaSubtype? MediaSubtypeId, string Title, int? ReleaseYear,
        string? ImageUrl);

    // A related title once it is a Work.
    internal sealed record WorkRelationTargetWorkData(long WorkId, WorkRelationType WorkRelationTypeId);

    // The Works one provider relates a Work to, as JSON rows of WorkId and WorkRelationTypeId.
    internal sealed class WorkRelationSaveData : BaseData
    {
        public WorkRelationSaveData(long workId, string sourceKey, IReadOnlyList<WorkRelationTargetWorkData> related)
        {
            WorkId = workId;
            SourceKey = sourceKey;
            Related = JsonSerializer.Serialize(related);
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkRelationFields), nameof(WorkRelationFields.SourceKey))]
        public string SourceKey { get; }

        [DataField(typeof(WorkRelationFields), nameof(WorkRelationFields.Targets))]
        public string Related { get; }
    }

    internal sealed class WorkRelationWorksData : BaseData
    {
        public WorkRelationWorksData(long[] workIds)
        {
            WorkIds = workIds;
        }

        [DataField(typeof(WorkRelationFields), nameof(WorkRelationFields.WorkIds))]
        public long[] WorkIds { get; }
    }
}
