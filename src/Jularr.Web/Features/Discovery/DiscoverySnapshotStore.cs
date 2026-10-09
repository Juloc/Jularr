using System.Text.Json;
using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Discovery;

/// <summary>One persisted answer of a browse source: the key of the call it answers and what it returned.</summary>
public sealed record DiscoverySnapshot(string Key, IReadOnlyList<DiscoveryItem> Items, DateTimeOffset FetchedAt);

/// <summary>
/// The local discovery snapshot (table <c>DiscoverySnapshots</c>): the last successful answer of each browse source, so the first response after a restart and a
/// provider outage still have titles to show. It holds provider facts only, never per-viewer state (requests, follows, library overlay are applied per response),
/// and never makes a title a Work. The number of rows and their age are bounded.
/// </summary>
public sealed class DiscoverySnapshotStore(AppDbContext db)
{
    /// <summary>The most answers kept; the oldest go first.</summary>
    public const int MaximumRows = 256;

    /// <summary>How long an answer stays usable as a stale answer; an older one is dropped rather than shown as current.</summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(14);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DiscoverySnapshot>> LoadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var oldest = now - MaximumAge;
        var rows = await db.Database
            .SqlQuery<SnapshotRow>($"""SELECT "Key", "Payload", "FetchedAt" FROM "DiscoverySnapshots" WHERE "FetchedAt" >= {oldest} ORDER BY "FetchedAt" DESC LIMIT {MaximumRows}""")
            .ToListAsync(cancellationToken);
        var snapshots = new List<DiscoverySnapshot>(rows.Count);
        foreach (var row in rows)
        {
            // A row an older version wrote that no longer reads is skipped: the next successful answer replaces it.
            if (TryRead(row.Payload) is { } items)
            {
                snapshots.Add(new DiscoverySnapshot(row.Key, items, row.FetchedAt));
            }
        }

        return snapshots;
    }

    public Task SaveAsync(string key, IReadOnlyList<DiscoveryItem> items, DateTimeOffset fetchedAt, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(items, Json);
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "DiscoverySnapshots" ("Key", "Payload", "FetchedAt") VALUES ({key}, {payload}, {fetchedAt})
            ON CONFLICT ("Key") DO UPDATE SET "Payload" = EXCLUDED."Payload", "FetchedAt" = EXCLUDED."FetchedAt"
            """,
            cancellationToken);
    }

    /// <summary>Drops what is too old and keeps the newest <see cref="MaximumRows"/>.</summary>
    public Task PruneAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var oldest = now - MaximumAge;
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM "DiscoverySnapshots"
            WHERE "FetchedAt" < {oldest}
               OR "Key" IN (SELECT "Key" FROM "DiscoverySnapshots" ORDER BY "FetchedAt" DESC OFFSET {MaximumRows})
            """,
            cancellationToken);
    }

    private static IReadOnlyList<DiscoveryItem>? TryRead(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<DiscoveryItem[]>(payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record SnapshotRow(string Key, string Payload, DateTimeOffset FetchedAt);
}
