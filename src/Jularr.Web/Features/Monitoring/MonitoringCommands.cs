using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Monitoring;

/// <summary>
/// The convenience commands over the canonical Monitoring state. Each is one set-based statement batch (one round trip, one implicit transaction),
/// whatever the number of children, and none stores a mode: "future" only writes ordinary decisions, so what is discovered later simply inherits.
/// </summary>
public sealed class MonitoringCommands(AppDbContext db, TimeProvider clock)
{
    // The node table of each kind and the column that names its Work (a Work is its own Work).
    private static readonly IReadOnlyDictionary<MonitoringTargetKind, (string Table, string WorkColumn)> Nodes = new Dictionary<MonitoringTargetKind, (string, string)>
    {
        [MonitoringTargetKind.Work] = ("Works", "Id"),
        [MonitoringTargetKind.Season] = ("WorkSeasons", "WorkId"),
        [MonitoringTargetKind.Episode] = ("WorkEpisodes", "WorkId"),
        [MonitoringTargetKind.Volume] = ("WorkVolumes", "WorkId"),
        [MonitoringTargetKind.Chapter] = ("WorkChapters", "WorkId"),
        [MonitoringTargetKind.Track] = ("WorkTracks", "WorkId")
    };

    // The decisions a container's own decision replaces: all of a Work, the episodes of a season, the chapters of a volume.
    private static readonly IReadOnlyDictionary<MonitoringTargetKind, string> DescendantsSql = new Dictionary<MonitoringTargetKind, string>
    {
        [MonitoringTargetKind.Work] = """DELETE FROM "WorkMonitoring" WHERE "WorkId" = {0} AND "TargetId" <> {0}""",
        [MonitoringTargetKind.Season] = """DELETE FROM "WorkMonitoring" WHERE "TargetId" IN (SELECT "Id" FROM "WorkEpisodes" WHERE "SeasonId" = {0})""",
        [MonitoringTargetKind.Volume] = """DELETE FROM "WorkMonitoring" WHERE "TargetId" IN (SELECT "Id" FROM "WorkChapters" WHERE "VolumeId" = {0})"""
    };

    /// <summary>
    /// Switches one node on or off, or back to Inherit with null. A container (Work, season, volume) that is switched on or off takes all of its children
    /// with it, so "monitor all" and "unmonitor all" are this call on the Work. Returns false when the node does not exist.
    /// </summary>
    public async Task<bool> SetAsync(MonitoringTargetKind kind, Guid targetId, bool? monitored, CancellationToken cancellationToken)
    {
        if (monitored is null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "WorkMonitoring" WHERE "TargetId" = {targetId}""", cancellationToken);
            return true;
        }

        var (table, workColumn) = Nodes[kind];
        var now = clock.GetUtcNow();
        // The table and column names come from the constant map above, never from input.
        var upsert = $$"""
            INSERT INTO "WorkMonitoring" ("WorkId", "Kind", "TargetId", "Monitored", "UpdatedAt")
            SELECT n."{{workColumn}}", {1}, n."Id", {2}, {3} FROM "{{table}}" n WHERE n."Id" = {0}
            ON CONFLICT ("TargetId") DO UPDATE SET "Monitored" = EXCLUDED."Monitored", "UpdatedAt" = EXCLUDED."UpdatedAt"
            """;
        var written = await db.Database.ExecuteSqlRawAsync(upsert, [targetId, (short)kind, monitored.Value, now], cancellationToken);
        if (written > 0 && DescendantsSql.TryGetValue(kind, out var descendants))
        {
            await db.Database.ExecuteSqlRawAsync(descendants, [targetId], cancellationToken);
        }

        return written > 0;
    }

    /// <summary>Switches a list of leaf nodes (episodes, chapters, tracks) on or off in one statement; the ids may belong to several Works.</summary>
    public async Task SetManyAsync(MonitoringTargetKind kind, IReadOnlyCollection<Guid> targetIds, bool monitored, CancellationToken cancellationToken)
    {
        var (table, workColumn) = Nodes[kind];
        var upsert = $$"""
            INSERT INTO "WorkMonitoring" ("WorkId", "Kind", "TargetId", "Monitored", "UpdatedAt")
            SELECT n."{{workColumn}}", {1}, n."Id", {2}, {3} FROM "{{table}}" n WHERE n."Id" = ANY({0})
            ON CONFLICT ("TargetId") DO UPDATE SET "Monitored" = EXCLUDED."Monitored", "UpdatedAt" = EXCLUDED."UpdatedAt"
            """;
        await db.Database.ExecuteSqlRawAsync(upsert, [targetIds.Distinct().ToArray(), (short)kind, monitored, clock.GetUtcNow()], cancellationToken);
    }

    /// <summary>
    /// "Future": the Work is monitored and everything it has today (episodes, chapters, tracks) is switched off by an ordinary decision, so what is
    /// discovered later has no decision and inherits the Work. Containers keep no decision, because a switched-off season would switch off its future episodes.
    /// </summary>
    public async Task FutureAsync(Guid workId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM "WorkMonitoring" WHERE "WorkId" = {workId} AND "TargetId" <> {workId};
            INSERT INTO "WorkMonitoring" ("WorkId", "Kind", "TargetId", "Monitored", "UpdatedAt")
            SELECT w."Id", 0, w."Id", TRUE, {now} FROM "Works" w WHERE w."Id" = {workId}
            ON CONFLICT ("TargetId") DO UPDATE SET "Monitored" = TRUE, "UpdatedAt" = EXCLUDED."UpdatedAt";
            INSERT INTO "WorkMonitoring" ("WorkId", "Kind", "TargetId", "Monitored", "UpdatedAt")
            SELECT x."WorkId", x."Kind", x."Id", FALSE, {now} FROM (
                SELECT "WorkId", 2::smallint AS "Kind", "Id" FROM "WorkEpisodes" WHERE "WorkId" = {workId}
                UNION ALL SELECT "WorkId", 4::smallint, "Id" FROM "WorkChapters" WHERE "WorkId" = {workId}
                UNION ALL SELECT "WorkId", 5::smallint, "Id" FROM "WorkTracks" WHERE "WorkId" = {workId}) x
            ON CONFLICT ("TargetId") DO UPDATE SET "Monitored" = FALSE, "UpdatedAt" = EXCLUDED."UpdatedAt"
            """,
            cancellationToken);
    }

    /// <summary>
    /// Monitors or stops monitoring a relation source. With <paramref name="onlyFuture"/> the Works the source reaches today get an ordinary "off"
    /// decision (unless they already decided for themselves), so only Works that appear later are monitored through it.
    /// </summary>
    public async Task SetRelationAsync(MonitoringRelationSource source, bool monitored, bool onlyFuture, CancellationToken cancellationToken)
    {
        if (!monitored)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "WorkMonitoringSources" WHERE "Kind" = {(short)source.Kind} AND "SourceKey" = {source.SourceKey}""", cancellationToken);
            return;
        }

        var roles = source.Roles?.Select(role => role.Trim()).Where(role => role.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var normalized = roles is { Length: > 0 } ? roles : null;
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "WorkMonitoringSources" ("Kind", "SourceKey", "Label", "Roles", "AddedByProfileId", "UpdatedAt")
            VALUES ({(short)source.Kind}, {source.SourceKey}, {source.Label}, {normalized}::text[], {source.AddedByProfileId}, {now})
            ON CONFLICT ("Kind", "SourceKey") DO UPDATE SET "Label" = EXCLUDED."Label", "Roles" = EXCLUDED."Roles", "UpdatedAt" = EXCLUDED."UpdatedAt"
            """,
            cancellationToken);
        if (onlyFuture)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "WorkMonitoring" ("WorkId", "Kind", "TargetId", "Monitored", "UpdatedAt")
                SELECT DISTINCT r."WorkId", 0, r."WorkId", FALSE, {2} FROM (
                """ + MonitoringResolver.RelationCoveredWorksSql + """
                ) r WHERE r."SourceId" = (SELECT "Id" FROM "WorkMonitoringSources" WHERE "Kind" = {0} AND "SourceKey" = {1})
                ON CONFLICT ("TargetId") DO NOTHING
                """,
                [(short)source.Kind, source.SourceKey, now],
                cancellationToken);
        }
    }
}
