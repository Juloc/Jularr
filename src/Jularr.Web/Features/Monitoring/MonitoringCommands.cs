using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Monitoring;

/// <summary>
/// The convenience commands over the canonical Monitoring state. Each is set-based whatever the number of children, runs as one transaction, and none
/// stores a mode: "future" only writes ordinary decisions, so what is discovered later simply inherits.
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
        [MonitoringTargetKind.Track] = ("WorkTracks", "WorkId"),
        [MonitoringTargetKind.Edition] = ("WorkEditions", "WorkId")
    };

    // The statements per node kind are built once from the fixed table list above; no caller input ever reaches an identifier.
    private static readonly IReadOnlyDictionary<MonitoringTargetKind, string> WorkOfSql = Nodes.ToDictionary(
        node => node.Key,
        node => $"SELECT n.\"{node.Value.WorkColumn}\" AS \"Value\" FROM \"{node.Value.Table}\" n WHERE n.\"Id\" = {{0}}");

    private static readonly IReadOnlyDictionary<MonitoringTargetKind, string> UpsertManySql = Nodes.ToDictionary(
        node => node.Key,
        node => $$"""
            INSERT INTO "WorkMonitoring" ("WorkId", "Kind", "TargetId", "Monitored", "UpdatedAt")
            SELECT n."{{node.Value.WorkColumn}}", {1}, n."Id", {2}, {3} FROM "{{node.Value.Table}}" n WHERE n."Id" = ANY({0})
            ON CONFLICT ("TargetId") DO UPDATE SET "Monitored" = EXCLUDED."Monitored", "UpdatedAt" = EXCLUDED."UpdatedAt"
            """);

    // The decisions a container's own decision replaces: all of a Work, the episodes of a season, the chapters of a volume.
    private static readonly IReadOnlyDictionary<MonitoringTargetKind, string> DescendantsSql = new Dictionary<MonitoringTargetKind, string>
    {
        [MonitoringTargetKind.Work] = """DELETE FROM "WorkMonitoring" WHERE "WorkId" = {0} AND "TargetId" <> {0} AND "Kind" <> 6""",
        [MonitoringTargetKind.Season] = """DELETE FROM "WorkMonitoring" WHERE "TargetId" IN (SELECT "Id" FROM "WorkEpisodes" WHERE "SeasonId" = {0})""",
        [MonitoringTargetKind.Volume] = """DELETE FROM "WorkMonitoring" WHERE "TargetId" IN (SELECT "Id" FROM "WorkChapters" WHERE "VolumeId" = {0})"""
    };

    /// <summary>
    /// Switches one node on or off, or back to Inherit with null. A container (Work, season, volume) that is switched on or off takes all of its children
    /// with it (unless <paramref name="replaceChildren"/> is false), so "monitor all" and "unmonitor all" are this call on the Work. Returns the Work the node
    /// belongs to, or null when the node does not exist.
    /// </summary>
    public async Task<Guid?> SetAsync(MonitoringTargetKind kind, Guid targetId, bool? monitored, CancellationToken cancellationToken, bool replaceChildren = true)
    {
        var workId = await db.Database.SqlQueryRaw<Guid?>(WorkOfSql[kind], targetId).FirstOrDefaultAsync(cancellationToken);
        if (workId is null)
        {
            return null;
        }

        if (monitored is null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "WorkMonitoring" WHERE "TargetId" = {targetId}""", cancellationToken);
            return workId;
        }

        var now = clock.GetUtcNow();
        await db.Database.InTransactionAsync(
            async () =>
            {
                var written = await db.Database.ExecuteSqlRawAsync(UpsertManySql[kind], [new[] { targetId }, (short)kind, monitored.Value, now], cancellationToken);
                if (written > 0 && replaceChildren && DescendantsSql.TryGetValue(kind, out var descendants))
                {
                    await db.Database.ExecuteSqlRawAsync(descendants, [targetId], cancellationToken);
                }
            },
            cancellationToken);
        return workId;
    }

    /// <summary>
    /// Switches the audiobook of a Book Work on or off (null returns it to not monitored), independent of the Book: the audio edition is made when it is first asked
    /// for, and no decision on the Book touches this one. Returns the Work, or null when it is not a Book.
    /// </summary>
    public async Task<Guid?> SetAudiobookAsync(Guid workId, bool? monitored, CancellationToken cancellationToken)
    {
        if (!await db.Works.AnyAsync(work => work.Id == workId && work.MediaType == MediaCore.WorkMediaType.Book, cancellationToken))
        {
            return null;
        }

        return await SetAsync(MonitoringTargetKind.Edition, await MediaCore.AudiobookEditions.EnsureAsync(db, workId, cancellationToken), monitored, cancellationToken);
    }

    /// <summary>
    /// Switches episodes on or off by season and episode number (null returns them to Inherit), creating the canonical episodes and seasons the Work does
    /// not have yet in one pass. That is how a title that is only known by numbers (Anime) gets nodes to decide on; a new node carries nothing but its numbers.
    /// </summary>
    public async Task SetEpisodesByNumberAsync(Guid workId, IReadOnlyCollection<(int Season, int Episode)> numbers, bool? monitored, CancellationToken cancellationToken)
    {
        if (numbers.Count == 0)
        {
            return;
        }

        await db.Database.InTransactionAsync(() => SetEpisodesByNumberCoreAsync(workId, numbers, monitored, cancellationToken), cancellationToken);
    }

    private async Task SetEpisodesByNumberCoreAsync(Guid workId, IReadOnlyCollection<(int Season, int Episode)> numbers, bool? monitored, CancellationToken cancellationToken)
    {
        var wanted = numbers.Distinct().ToHashSet();
        var seasonNumbers = wanted.Select(pair => pair.Season).Distinct().ToArray();
        var existing = await db.WorkEpisodes.Where(x => x.WorkId == workId && seasonNumbers.Contains(x.SeasonNumber)).ToListAsync(cancellationToken);
        var ids = existing.Where(x => wanted.Contains((x.SeasonNumber, x.EpisodeNumber))).Select(x => x.Id).ToList();
        if (monitored is not null)
        {
            var seasonIds = await EnsureSeasonsAsync(workId, seasonNumbers, cancellationToken);
            var known = existing.Select(x => (x.SeasonNumber, x.EpisodeNumber)).ToHashSet();
            var created = wanted.Where(pair => !known.Contains(pair))
                .Select(pair => new MediaCore.WorkEpisode { WorkId = workId, SeasonId = seasonIds[pair.Season], SeasonNumber = pair.Season, EpisodeNumber = pair.Episode, IsSpecial = pair.Season == 0 })
                .ToList();
            db.WorkEpisodes.AddRange(created);
            await db.SaveChangesAsync(cancellationToken);
            ids.AddRange(created.Select(x => x.Id));
        }

        if (monitored is null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "WorkMonitoring" WHERE "TargetId" = ANY({ids.ToArray()})""", cancellationToken);
        }
        else
        {
            await SetManyAsync(MonitoringTargetKind.Episode, ids, monitored.Value, cancellationToken);
        }
    }

    /// <summary>Switches whole seasons on or off by number, creating the canonical seasons the Work does not have yet; the decisions of their episodes are replaced by the season's.</summary>
    public async Task SetSeasonsByNumberAsync(Guid workId, IReadOnlyCollection<int> seasonNumbers, bool monitored, CancellationToken cancellationToken)
    {
        if (seasonNumbers.Count == 0)
        {
            return;
        }

        var numbers = seasonNumbers.Distinct().ToArray();
        await db.Database.InTransactionAsync(
            async () =>
            {
                var seasonIds = await EnsureSeasonsAsync(workId, numbers, cancellationToken);
                await SetManyAsync(MonitoringTargetKind.Season, seasonIds.Values.ToArray(), monitored, cancellationToken);
                // Episodes that carry only the season number (no season id) are covered by the season's decision too.
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""DELETE FROM "WorkMonitoring" WHERE "Kind" = 2 AND "TargetId" IN (SELECT "Id" FROM "WorkEpisodes" WHERE "WorkId" = {workId} AND "SeasonNumber" = ANY({numbers}))""",
                    cancellationToken);
            },
            cancellationToken);
    }

    private async Task<Dictionary<int, Guid>> EnsureSeasonsAsync(Guid workId, int[] seasonNumbers, CancellationToken cancellationToken)
    {
        var seasons = await db.WorkSeasons.Where(x => x.WorkId == workId && seasonNumbers.Contains(x.SeasonNumber)).ToListAsync(cancellationToken);
        var created = seasonNumbers.Where(number => seasons.All(season => season.SeasonNumber != number))
            .Select(number => new MediaCore.WorkSeason { WorkId = workId, SeasonNumber = number, IsSpecial = number == 0 })
            .ToList();
        if (created.Count > 0)
        {
            db.WorkSeasons.AddRange(created);
            await db.SaveChangesAsync(cancellationToken);
        }

        return seasons.Concat(created).ToDictionary(season => season.SeasonNumber, season => season.Id);
    }

    /// <summary>Switches a list of leaf nodes (episodes, chapters, tracks) on or off in one statement; the ids may belong to several Works.</summary>
    public async Task SetManyAsync(MonitoringTargetKind kind, IReadOnlyCollection<Guid> targetIds, bool monitored, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(UpsertManySql[kind], [targetIds.Distinct().ToArray(), (short)kind, monitored, clock.GetUtcNow()], cancellationToken);
    }

    /// <summary>
    /// "Future": the Work is monitored and what it has today (episodes that have aired, chapters, tracks) is switched off by an ordinary decision, so what is
    /// discovered later, and the episodes that are announced but not aired yet, have no decision and inherit the Work. Containers keep no decision, because a
    /// switched-off season would switch off its future episodes.
    /// </summary>
    public async Task FutureAsync(Guid workId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM "WorkMonitoring" WHERE "WorkId" = {workId} AND "TargetId" <> {workId} AND "Kind" <> 6;
            INSERT INTO "WorkMonitoring" ("WorkId", "Kind", "TargetId", "Monitored", "UpdatedAt")
            SELECT w."Id", 0, w."Id", TRUE, {now} FROM "Works" w WHERE w."Id" = {workId}
            ON CONFLICT ("TargetId") DO UPDATE SET "Monitored" = TRUE, "UpdatedAt" = EXCLUDED."UpdatedAt";
            INSERT INTO "WorkMonitoring" ("WorkId", "Kind", "TargetId", "Monitored", "UpdatedAt")
            SELECT x."WorkId", x."Kind", x."Id", FALSE, {now} FROM (
                SELECT "WorkId", 2::smallint AS "Kind", "Id" FROM "WorkEpisodes" WHERE "WorkId" = {workId} AND ("AiredAt" IS NULL OR "AiredAt" <= {now})
                UNION ALL SELECT "WorkId", 4::smallint, "Id" FROM "WorkChapters" WHERE "WorkId" = {workId}
                UNION ALL SELECT "WorkId", 5::smallint, "Id" FROM "WorkTracks" WHERE "WorkId" = {workId}) x
            ON CONFLICT ("TargetId") DO UPDATE SET "Monitored" = FALSE, "UpdatedAt" = EXCLUDED."UpdatedAt"
            """,
            cancellationToken);
    }

    /// <summary>
    /// A custom selection of a Series: the chosen seasons and episodes are on, everything else known today is off, and with
    /// <paramref name="future"/> the Work itself is on so what appears later is monitored. Replaces every earlier decision of the Work.
    /// </summary>
    public async Task ApplySelectionAsync(Guid workId, IReadOnlyCollection<Guid> seasonIds, IReadOnlyCollection<Guid> episodeIds, bool future, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var seasons = seasonIds.Distinct().ToArray();
        var episodes = episodeIds.Distinct().ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM "WorkMonitoring" WHERE "WorkId" = {workId} AND "TargetId" <> {workId} AND "Kind" <> 6;
            INSERT INTO "WorkMonitoring" ("WorkId", "Kind", "TargetId", "Monitored", "UpdatedAt")
            SELECT w."Id", 0, w."Id", {future}, {now} FROM "Works" w WHERE w."Id" = {workId}
            ON CONFLICT ("TargetId") DO UPDATE SET "Monitored" = EXCLUDED."Monitored", "UpdatedAt" = EXCLUDED."UpdatedAt";
            INSERT INTO "WorkMonitoring" ("WorkId", "Kind", "TargetId", "Monitored", "UpdatedAt")
            SELECT s."WorkId", 1, s."Id", TRUE, {now} FROM "WorkSeasons" s WHERE s."WorkId" = {workId} AND s."Id" = ANY({seasons});
            INSERT INTO "WorkMonitoring" ("WorkId", "Kind", "TargetId", "Monitored", "UpdatedAt")
            SELECT e."WorkId", 2, e."Id", TRUE, {now} FROM "WorkEpisodes" e WHERE e."WorkId" = {workId} AND e."Id" = ANY({episodes});
            INSERT INTO "WorkMonitoring" ("WorkId", "Kind", "TargetId", "Monitored", "UpdatedAt")
            SELECT e."WorkId", 2, e."Id", FALSE, {now} FROM "WorkEpisodes" e
            WHERE {future} AND e."WorkId" = {workId} AND NOT (e."Id" = ANY({episodes})) AND (e."SeasonId" IS NULL OR NOT (e."SeasonId" = ANY({seasons})))
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
        await db.Database.InTransactionAsync(() => AddRelationAsync(source, normalized, onlyFuture, now, cancellationToken), cancellationToken);
    }

    private async Task AddRelationAsync(MonitoringRelationSource source, string[]? normalized, bool onlyFuture, DateTimeOffset now, CancellationToken cancellationToken)
    {
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
