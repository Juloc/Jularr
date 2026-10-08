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

/// <summary>One explicit decision: which kind of node it is on, and whether it switches the node on or off.</summary>
public sealed record MonitoringDecision(MonitoringTargetKind Kind, bool Monitored);

/// <summary>The decisions on episodes and seasons addressed by their numbers, for callers that know a title by season and episode number (Anime).</summary>
public sealed record NumberedDecisions(IReadOnlyDictionary<(int Season, int Episode), bool> Episodes, IReadOnlyDictionary<int, bool> Seasons);

public sealed record MonitoringRelationSource(MonitoringRelationKind Kind, string SourceKey, string Label, IReadOnlyList<string>? Roles, string AddedByProfileId);

/// <summary>
/// The explicit decisions of one Work and its structure, plus whether a monitored relation reaches the Work. A missing decision is Inherit. This is the
/// only place an effective state is derived: the explicit decision of the node, then of its parent, then the Work's own state,
/// which is itself an explicit decision or else the relation.
/// </summary>
public sealed class WorkMonitoringView(Guid workId, IReadOnlyDictionary<Guid, MonitoringDecision> decisions, bool relationMonitored, NumberedDecisions? numbered = null)
{
    public Guid WorkId { get; } = workId;

    public bool IsRelationMonitored { get; } = relationMonitored;

    public bool IsWorkMonitored => DecisionOf(WorkId) ?? IsRelationMonitored;

    /// <summary>Whether anything of the Work is monitored: the Work itself, or a node that was switched on while the Work is not.</summary>
    public bool IsAnyMonitored => IsWorkMonitored || decisions.Values.Any(decision => decision.Monitored);

    /// <summary>Whether nodes that appear later can be monitored: the Work is monitored, or a season or volume that takes its new children with it.</summary>
    public bool ReachesFutureNodes => IsWorkMonitored || decisions.Values.Any(decision => decision.Monitored && decision.Kind is MonitoringTargetKind.Season or MonitoringTargetKind.Volume);

    /// <summary>Whether any season, episode, volume, chapter or track carries a decision of its own.</summary>
    public bool HasNodeDecisions => decisions.Values.Any(decision => decision.Kind != MonitoringTargetKind.Work);

    public bool? DecisionOf(Guid targetId) => decisions.TryGetValue(targetId, out var decision) ? decision.Monitored : null;

    /// <summary>The ids of the nodes of one kind that carry a decision with the given value (for example the episodes switched on).</summary>
    public IEnumerable<Guid> DecidedIds(MonitoringTargetKind kind, bool monitored) =>
        decisions.Where(pair => pair.Value.Kind == kind && pair.Value.Monitored == monitored).Select(pair => pair.Key);

    /// <summary>The state of a node: its own decision, else its parent's (the season of an episode, the volume of a chapter), else the Work's.</summary>
    public bool IsMonitored(Guid targetId, Guid? parentId = null) =>
        DecisionOf(targetId) ?? (parentId is { } parent ? DecisionOf(parent) : null) ?? IsWorkMonitored;

    /// <summary>The state of an episode by season and episode number; needs the view to be loaded with numbers.</summary>
    public bool IsEpisodeMonitored(int seasonNumber, int episodeNumber) =>
        numbered is null
            ? throw new InvalidOperationException("The view was loaded without numbers.")
            : numbered.Episodes.TryGetValue((seasonNumber, episodeNumber), out var episode) ? episode : IsSeasonMonitored(seasonNumber);

    /// <summary>The state of a season by its number; needs the view to be loaded with numbers.</summary>
    public bool IsSeasonMonitored(int seasonNumber) =>
        numbered is null
            ? throw new InvalidOperationException("The view was loaded without numbers.")
            : numbered.Seasons.TryGetValue(seasonNumber, out var season) ? season : IsWorkMonitored;

    public static WorkMonitoringView Unmonitored(Guid workId) => new(workId, new Dictionary<Guid, MonitoringDecision>(), relationMonitored: false, new NumberedDecisions(new Dictionary<(int, int), bool>(), new Dictionary<int, bool>()));
}
