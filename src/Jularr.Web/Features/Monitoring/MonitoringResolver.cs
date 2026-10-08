using Jularr.Web.Data;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Monitoring;

/// <summary>
/// Reads the canonical Monitoring state (tables <c>WorkMonitoring</c> and <c>WorkMonitoringSources</c>). One query set per Work, or per batch of Works for
/// lists; nothing is cached, so a command is visible to the next read.
/// </summary>
public sealed class MonitoringResolver(AppDbContext db)
{
    /// <summary>
    /// The (source, Work) pairs of the monitored relation sources. Person: credits of the person, limited to the allowed roles when the source has some ("Actor" is every
    /// cast credit, any other role is matched against the credit's role). Studio: the studio names of the Work metadata. Collection: the collection items.
    /// Artist: the albums of the artist.
    /// </summary>
    public const string RelationCoveredWorksSql =
        """
        SELECT s."Id" AS "SourceId", c."WorkId" FROM "WorkCredits" c
        JOIN "WorkMonitoringSources" s ON s."Kind" = 0 AND s."SourceKey" = c."ProviderPersonId"
            AND (s."Roles" IS NULL OR EXISTS (SELECT 1 FROM unnest(s."Roles") r WHERE lower(r) = lower(c."Role") OR (lower(r) = 'actor' AND c."Kind" = 0)))
        UNION
        SELECT s."Id", f."WorkId" FROM "WorkMetadataFacts" f
        JOIN "WorkMonitoringSources" s ON s."Kind" = 1 AND EXISTS (SELECT 1 FROM unnest(f."Studios") st WHERE lower(st) = lower(s."SourceKey"))
        UNION
        SELECT s."Id", i."WorkId" FROM "CollectionItems" i
        JOIN "WorkMonitoringSources" s ON s."Kind" = 2 AND s."SourceKey" = i."CollectionId"::text
        UNION
        SELECT s."Id", a."WorkId" FROM "MusicAlbums" a
        JOIN "WorkMonitoringSources" s ON s."Kind" = 3 AND s."SourceKey" = a."ArtistId"::text
        """;

    public async Task<WorkMonitoringView> LoadAsync(Guid workId, CancellationToken cancellationToken) =>
        (await LoadManyAsync([workId], cancellationToken))[workId];

    /// <summary>The views of several Works in two queries, whatever their number.</summary>
    public async Task<IReadOnlyDictionary<Guid, WorkMonitoringView>> LoadManyAsync(IReadOnlyCollection<Guid> workIds, CancellationToken cancellationToken)
    {
        var ids = workIds.Distinct().ToArray();
        var rows = await db.Database
            .SqlQuery<DecisionRow>($"""SELECT "WorkId", "TargetId", "Monitored" FROM "WorkMonitoring" WHERE "WorkId" = ANY({ids})""")
            .ToListAsync(cancellationToken);
        var reached = (await db.Database
            .SqlQueryRaw<Guid>("SELECT DISTINCT r.\"WorkId\" AS \"Value\" FROM (" + RelationCoveredWorksSql + ") r WHERE r.\"WorkId\" = ANY({0})", (object)ids)
            .ToListAsync(cancellationToken)).ToHashSet();
        var byWork = rows.ToLookup(row => row.WorkId);
        return ids.ToDictionary(
            id => id,
            id => new WorkMonitoringView(id, byWork[id].ToDictionary(row => row.TargetId, row => row.Monitored), reached.Contains(id)));
    }

    /// <summary>
    /// The Works of one media type that have anything monitored, after <paramref name="after"/> in id order: a Work decided as monitored, a Work that has a
    /// node switched on, or a Work with no decision that a monitored relation reaches.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> MonitoredWorkIdsAsync(WorkMediaType mediaType, Guid after, int limit, CancellationToken cancellationToken)
    {
        var type = (int)mediaType;
        return await db.Database
            .SqlQueryRaw<Guid>(
                """
                SELECT w."Id" AS "Value" FROM "Works" w
                WHERE w."MediaType" = {0} AND w."Id" > {1}
                  AND (EXISTS (SELECT 1 FROM "WorkMonitoring" m WHERE m."WorkId" = w."Id" AND m."Monitored")
                       OR (NOT EXISTS (SELECT 1 FROM "WorkMonitoring" m WHERE m."TargetId" = w."Id")
                           AND EXISTS (SELECT 1 FROM (
                """ + RelationCoveredWorksSql + """
                           ) r WHERE r."WorkId" = w."Id")))
                ORDER BY w."Id" LIMIT {2}
                """,
                type,
                after,
                limit)
            .ToListAsync(cancellationToken);
    }

    private sealed record DecisionRow(Guid WorkId, Guid TargetId, bool Monitored);
}
