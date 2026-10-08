namespace Jularr.Web.Features.Monitoring;

/// <summary>The canonical node a monitoring decision is about; the id is the node's own canonical id (the Work id for <see cref="Work"/>).</summary>
public enum MonitoringTargetKind : short
{
    Work = 0,
    Season = 1,
    Episode = 2,
    Volume = 3,
    Chapter = 4,
    Track = 5
}

/// <summary>A canonical relation that can be monitored as a whole; every Work it reaches is monitored unless that Work decides otherwise.</summary>
public enum MonitoringRelationKind : short
{
    /// <summary>A person from the Work credits; the key is the provider person id.</summary>
    Person = 0,

    /// <summary>A studio from the Work metadata; the key is the studio name.</summary>
    Studio = 1,

    /// <summary>A collection; the key is the collection id.</summary>
    Collection = 2,

    /// <summary>A music artist; the key is the artist id.</summary>
    Artist = 3
}

public sealed record MonitoringRelationSource(MonitoringRelationKind Kind, string SourceKey, string Label, IReadOnlyList<string>? Roles, string AddedByProfileId);

/// <summary>
/// The explicit decisions of one Work and its structure, plus whether a monitored relation reaches the Work. A missing decision is Inherit. This is the
/// only place an effective state is derived: the explicit decision of the node, then of its parent, then the Work's own state,
/// which is itself an explicit decision or else the relation.
/// </summary>
public sealed class WorkMonitoringView(Guid workId, IReadOnlyDictionary<Guid, bool> decisions, bool relationMonitored)
{
    public Guid WorkId { get; } = workId;

    public bool IsRelationMonitored { get; } = relationMonitored;

    public bool IsWorkMonitored => decisions.TryGetValue(WorkId, out var decided) ? decided : IsRelationMonitored;

    /// <summary>Whether anything of the Work is monitored: the Work itself, or a node that was switched on while the Work is not.</summary>
    public bool IsAnyMonitored => IsWorkMonitored || decisions.Values.Any(decided => decided);

    public bool? DecisionOf(Guid targetId) => decisions.TryGetValue(targetId, out var decided) ? decided : null;

    /// <summary>The state of a node: its own decision, else its parent's (the season of an episode, the volume of a chapter), else the Work's.</summary>
    public bool IsMonitored(Guid targetId, Guid? parentId = null) =>
        DecisionOf(targetId) ?? (parentId is { } parent ? DecisionOf(parent) : null) ?? IsWorkMonitored;

    public static WorkMonitoringView Unmonitored(Guid workId) => new(workId, new Dictionary<Guid, bool>(), relationMonitored: false);
}
