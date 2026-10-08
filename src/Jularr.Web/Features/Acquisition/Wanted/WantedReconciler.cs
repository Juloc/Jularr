using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Jularr.Web.Features.Acquisition.Wanted;

// The one writer of WantedItems: what the intent sources want right now minus what the library already holds, set-based and idempotent.
// When the whole library was last reconciled, shared by every pass: changes of one Work are reconciled where they happen, so the full run only has to
// catch what nobody announced (new metadata, files that appeared or vanished outside Jularr).
public sealed class WantedReconcileState
{
    public static readonly TimeSpan FullInterval = TimeSpan.FromMinutes(10);

    private long lastTicks;

    public bool IsDue(DateTime nowUtc) => nowUtc.Ticks - Interlocked.Read(ref lastTicks) >= FullInterval.Ticks;

    public void Mark(DateTime nowUtc) => Interlocked.Exchange(ref lastTicks, nowUtc.Ticks);
}

public sealed class WantedReconciler(AppDbContext db, TimeProvider clock, WantedReconcileState? state = null)
{
    private static readonly (MediaAcquisitionKind Kind, WorkMediaType Type)[] Reconciled =
    [
        (MediaAcquisitionKind.Movie, WorkMediaType.Movie),
        (MediaAcquisitionKind.Tv, WorkMediaType.Series),
        (MediaAcquisitionKind.Book, WorkMediaType.Book),
        (MediaAcquisitionKind.LightNovel, WorkMediaType.LightNovel),
        (MediaAcquisitionKind.Manga, WorkMediaType.Manga),
        (MediaAcquisitionKind.Music, WorkMediaType.Music)
    ];

    public static WorkMediaType WorkTypeOf(MediaAcquisitionKind kind) =>
        Reconciled.Single(entry => entry.Kind == kind).Type;

    // The full run every source of a pass asks for: only the first one in a while does the work.
    public async Task ReconcileAllIfDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (state is not null && !state.IsDue(now))
        {
            return;
        }

        await ReconcileAsync(null, cancellationToken);
        state?.Mark(now);
    }

    // Brings the items of one Work, or of every Work of the reconciled types, in line with the current intent and installed coverage.
    public async Task ReconcileAsync(Guid? workId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlRawAsync(
            WantedSql.Reconcile,
            [
                new NpgsqlParameter("workId", NpgsqlDbType.Uuid) { Value = workId.HasValue ? workId.Value : DBNull.Value },
                new NpgsqlParameter("now", clock.GetUtcNow().UtcDateTime),
                new NpgsqlParameter("types", Reconciled.Select(entry => (int)entry.Type).ToArray()),
                new NpgsqlParameter<int>("movie", (int)WorkMediaType.Movie),
                new NpgsqlParameter<int>("series", (int)WorkMediaType.Series),
                new NpgsqlParameter<int>("book", (int)WorkMediaType.Book),
                new NpgsqlParameter<int>("lightNovel", (int)WorkMediaType.LightNovel),
                new NpgsqlParameter<int>("manga", (int)WorkMediaType.Manga),
                new NpgsqlParameter<int>("music", (int)WorkMediaType.Music)
            ],
            cancellationToken);

    // The episodes the open requests of a Work explicitly ask for, independent of Monitoring.
    public async Task<IReadOnlySet<Guid>> RequestedEpisodeIdsAsync(Guid workId, CancellationToken cancellationToken) =>
        (await db.Database.SqlQueryRaw<Guid>(WantedSql.RequestedEpisodes, new NpgsqlParameter("workId", workId)).ToListAsync(cancellationToken)).ToHashSet();

    public async Task<bool> AnyAsync(Guid workId, CancellationToken cancellationToken) =>
        await db.WantedItems.AsNoTracking().AnyAsync(x => x.WorkId == workId, cancellationToken);

    public async Task<IReadOnlySet<Guid>> TargetIdsAsync(Guid workId, WantedTargetKind kind, CancellationToken cancellationToken) =>
        (await db.WantedItems.AsNoTracking().Where(x => x.WorkId == workId && x.TargetKind == kind).Select(x => x.TargetId).ToListAsync(cancellationToken)).ToHashSet();

    // The Works of one kind that have wanted items and no open request to carry them, in id order.
    public async Task<IReadOnlyList<Guid>> WorksWithoutOpenRequestAsync(MediaAcquisitionKind kind, Guid after, int limit, CancellationToken cancellationToken) =>
        await db.Database
            .SqlQueryRaw<Guid>(
                WantedSql.WorksWithoutOpenRequest,
                new NpgsqlParameter("mediaType", (int)WorkTypeOf(kind)),
                new NpgsqlParameter("after", after),
                new NpgsqlParameter("kind", AcquisitionAccessNames.Kind(kind)),
                new NpgsqlParameter("limit", limit))
            .ToListAsync(cancellationToken);
}
