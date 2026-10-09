namespace Jularr.Web.Features.MediaCore;

/// <summary>A per-type record already belongs to another canonical work, so linking it elsewhere would silently change its identity. The caller reports it for review.</summary>
public sealed class WorkSourceLinkConflictException(WorkSourceKind sourceKind, Guid sourceId, long linkedWorkId, long requestedWorkId)
    : InvalidOperationException($"The {sourceKind} record {sourceId} already belongs to work {linkedWorkId}; it is not moved to work {requestedWorkId} without an explicit merge.")
{
    public WorkSourceKind SourceKind { get; } = sourceKind;

    public Guid SourceId { get; } = sourceId;

    public long LinkedWorkId { get; } = linkedWorkId;

    public long RequestedWorkId { get; } = requestedWorkId;
}
