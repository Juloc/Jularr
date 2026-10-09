namespace Jularr.Web.Features.MediaCore;

/// <summary>What kind of identity resolution produced a <see cref="WorkIdentityChange"/> entry (#432).</summary>
public enum WorkIdentityChangeType
{
    /// <summary>Two works were merged into one; the source work was absorbed and removed.</summary>
    Merge,

    /// <summary>A provider identity was peeled off a work into a brand-new work.</summary>
    Split,

    /// <summary>A provider identity was moved from one existing work to another.</summary>
    Reassign
}

/// <summary>
/// Append-only history of an identity resolution — a merge, split or reassignment (#432). Unlike the
/// rest of the media core these rows deliberately carry <b>no</b> foreign key to <see cref="Work"/>:
/// a merge removes the absorbed work, so the log must outlive it as a durable tombstone the
/// Merge/Duplicate Review Center (#437) reads to explain what happened and by whom. One row per action.
/// </summary>
public sealed class WorkIdentityChange
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public WorkIdentityChangeType ChangeType { get; set; }

    /// <summary>Media type of the works involved (the log spans every media type, not just anime).</summary>
    public WorkMediaType MediaType { get; set; }

    /// <summary>The surviving/destination work the change resolved toward.</summary>
    public long TargetWorkId { get; set; }

    /// <summary>The absorbed (merge) or origin (split/reassign) work; null when unknown.</summary>
    public long? SourceWorkId { get; set; }

    /// <summary>Normalized provider of the moved identity, or empty for a whole-work merge.</summary>
    public string Provider { get; set; } = "";

    /// <summary>External id of the moved identity, or empty for a whole-work merge.</summary>
    public string ExternalId { get; set; } = "";

    /// <summary>Who performed the change (profile id, <c>owner</c>, or a system actor).</summary>
    public string Actor { get; set; } = "";

    /// <summary>Short human-readable summary of the change.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Detailed justification and what moved.</summary>
    public string Details { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
