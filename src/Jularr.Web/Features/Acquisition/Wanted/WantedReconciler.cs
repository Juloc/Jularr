using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Providers;
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

// Queues what Monitoring or an open request wants and the library lacks, plus what it holds but its profile still wants better (decided by the
// media type's <see cref="IUpgradeAssessor"/>, only when one Work is reconciled or an upgrade scan reaches it).
public sealed record UpgradePage(IReadOnlyList<Guid> Works, bool ReachedEnd);

public sealed class WantedReconciler(AppDbContext db, TimeProvider clock, WantedReconcileState? state = null, UpgradeAssessors? upgrades = null)
{
    private static readonly (MediaAcquisitionKind Kind, WorkMediaType Type)[] Reconciled =
    [
        (MediaAcquisitionKind.Movie, WorkMediaType.Movie),
        (MediaAcquisitionKind.Tv, WorkMediaType.Series),
        (MediaAcquisitionKind.Book, WorkMediaType.Book),
        (MediaAcquisitionKind.Audiobook, WorkMediaType.Book),
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

    // Brings the items of one Work, or of every Work of the reconciled types, in line with the current intent and installed coverage. One Work is also
    // assessed for upgrades; the full run leaves that to the upgrade scan so it stays one statement.
    public async Task ReconcileAsync(Guid? workId, CancellationToken cancellationToken)
    {
        var upgradeTypes = upgrades?.Types.Select(type => (int)type).ToArray() ?? [];
        await db.Database.ExecuteSqlRawAsync(WantedSql.Reconcile, [.. CoverageParameters(workId), new NpgsqlParameter("upgradeTypes", upgradeTypes)], cancellationToken);
        if (workId is { } id && upgrades is not null)
        {
            await SyncUpgradesAsync(id, cancellationToken);
        }
    }

    // Looks at a page of the Works of one kind that hold something wanted, in id order, and says whether the library ended with it.
    public async Task<UpgradePage> ReconcileUpgradesAsync(MediaAcquisitionKind kind, Guid after, int limit, CancellationToken cancellationToken)
    {
        var works = await db.Database
            .SqlQueryRaw<Guid>(WantedSql.HeldWorks, [.. CoverageParameters(null), new NpgsqlParameter("mediaType", (int)WorkTypeOf(kind)), new NpgsqlParameter("after", after), new NpgsqlParameter("limit", limit)])
            .ToListAsync(cancellationToken);
        foreach (var workId in works)
        {
            await SyncUpgradesAsync(workId, cancellationToken);
        }

        return new UpgradePage(works, works.Count < limit);
    }

    private async Task SyncUpgradesAsync(Guid workId, CancellationToken cancellationToken)
    {
        var type = await db.Works.AsNoTracking().Where(work => work.Id == workId).Select(work => (WorkMediaType?)work.MediaType).FirstOrDefaultAsync(cancellationToken);
        if (type is null || upgrades?.For(type.Value) is not { } assessor)
        {
            return;
        }

        var held = await db.Database.SqlQueryRaw<HeldTarget>(WantedSql.HeldTargets, CoverageParameters(workId)).ToListAsync(cancellationToken);
        if (held.Count == 0)
        {
            return;
        }

        var upgradable = await assessor.UpgradableAsync(workId, held, cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            WantedSql.SyncUpgrades,
            [
                .. CoverageParameters(workId),
                new NpgsqlParameter("upgradeKinds", upgradable.Select(target => target.TargetKind).ToArray()),
                new NpgsqlParameter("upgradeIds", upgradable.Select(target => target.TargetId).ToArray())
            ],
            cancellationToken);
    }

    // The reconciled Work (null for all) and the media type numbers the coverage rules distinguish; each statement needs its own parameter objects.
    private NpgsqlParameter[] CoverageParameters(Guid? workId) =>
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
    ];

    // The episodes the open requests of a Work explicitly ask for, independent of Monitoring.
    public async Task<IReadOnlySet<Guid>> RequestedEpisodeIdsAsync(Guid workId, CancellationToken cancellationToken) =>
        (await db.Database.SqlQueryRaw<Guid>(WantedSql.RequestedEpisodes, new NpgsqlParameter("workId", workId)).ToListAsync(cancellationToken)).ToHashSet();

    public async Task<bool> AnyAsync(Guid workId, CancellationToken cancellationToken) =>
        await db.WantedItems.AsNoTracking().AnyAsync(x => x.WorkId == workId, cancellationToken);

    public async Task<IReadOnlySet<Guid>> TargetIdsAsync(Guid workId, WantedTargetKind kind, CancellationToken cancellationToken) =>
        (await db.WantedItems.AsNoTracking().Where(x => x.WorkId == workId && x.TargetKind == kind).Select(x => x.TargetId).ToListAsync(cancellationToken)).ToHashSet();

    // The Works of one kind that miss something and have no open request to carry it, in id order.
    public async Task<IReadOnlyList<Guid>> WorksWithoutOpenRequestAsync(MediaAcquisitionKind kind, Guid after, int limit, CancellationToken cancellationToken) =>
        await db.Database
            .SqlQueryRaw<Guid>(
                WantedSql.WorksWithoutOpenRequest,
                [.. CoverageParameters(null), .. RequestParameters(kind), new NpgsqlParameter("mediaType", (int)WorkTypeOf(kind)), new NpgsqlParameter("after", after), new NpgsqlParameter("limit", limit), new NpgsqlParameter("editions", kind == MediaAcquisitionKind.Audiobook)])
            .ToListAsync(cancellationToken);

    // The request of the Work that ended Completed as its latest one, or null.
    public async Task<string?> CompletedRequestOfAsync(MediaAcquisitionKind kind, Guid workId, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<string>(WantedSql.CompletedRequestOf, [new NpgsqlParameter("workId", workId), .. RequestParameters(kind)]).FirstOrDefaultAsync(cancellationToken);

    private static NpgsqlParameter[] RequestParameters(MediaAcquisitionKind kind) =>
    [
        new NpgsqlParameter("kind", AcquisitionAccessNames.Kind(kind)),
        new NpgsqlParameter("musicBrainz", ProviderKeys.MusicBrainz)
    ];
}
