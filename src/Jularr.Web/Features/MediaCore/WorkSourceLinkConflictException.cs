namespace Jularr.Web.Features.MediaCore;

/// <summary>A per-type record already belongs to another canonical work, so linking it elsewhere would silently change its identity. The caller reports it for review.</summary>
public sealed class WorkSourceLinkConflictException(WorkSourceKind sourceKind, Guid sourceId, Guid linkedWorkId, Guid requestedWorkId)
    : InvalidOperationException($"The {sourceKind} record {sourceId} already belongs to work {linkedWorkId}; it is not moved to work {requestedWorkId} without an explicit merge.")
{
    public WorkSourceKind SourceKind { get; } = sourceKind;

    public Guid SourceId { get; } = sourceId;

    public Guid LinkedWorkId { get; } = linkedWorkId;

    public Guid RequestedWorkId { get; } = requestedWorkId;
}
