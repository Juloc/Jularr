using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Progress;

public sealed record ActiveSessionSnapshot(
    Guid Id,
    string ProfileId,
    Guid WorkId,
    Guid? WorkEpisodeId,
    Guid MediaAssetId,
    Guid StoredFileId,
    string DeliveryMode,
    string ClientKind,
    DateTime StartedAt,
    DateTime LastUpdatedAt,
    DateTime? EndedAt)
{
    public bool IsActive => EndedAt is null;
}

public sealed class ActiveSessionService(
    AppDbContext db,
    TimeProvider time)
{
    public async Task<ActiveSessionSnapshot?> OpenAsync(
        Guid sessionId,
        string profileId,
        Guid storedFileId,
        string deliveryMode,
        string clientKind,
        Guid? replacesSessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryMode);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientKind);

        var target = await (
                from file in db.StoredFiles.AsNoTracking()
                join asset in db.MediaAssets.AsNoTracking()
                    on file.MediaAssetId equals (Guid?)asset.Id
                where file.Id == storedFileId
                select new ActiveTargetRow(
                    asset.WorkId,
                    asset.WorkEpisodeId,
                    asset.Id,
                    file.Id))
            .SingleOrDefaultAsync(cancellationToken);

        if (target is null)
        {
            return null;
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (replacesSessionId is { } replaced)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE "ActiveSessions"
                SET "EndedAt" = {0}, "LastUpdatedAt" = {0}
                WHERE "Id" = {1} AND "ProfileId" = {2} AND "EndedAt" IS NULL
                """,
                [now, replaced, profileId],
                cancellationToken);
        }

        var normalizedMode = deliveryMode.Trim().ToLowerInvariant();
        var normalizedClient = clientKind.Trim().ToLowerInvariant();
        if (target.WorkEpisodeId is { } episodeId)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "ActiveSessions"
                    ("Id", "ProfileId", "WorkId", "WorkEpisodeId", "MediaAssetId", "StoredFileId", "DeliveryMode", "ClientKind", "StartedAt", "LastUpdatedAt", "EndedAt")
                VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {8}, NULL)
                ON CONFLICT ("Id") DO UPDATE
                SET "ProfileId" = excluded."ProfileId",
                    "WorkId" = excluded."WorkId",
                    "WorkEpisodeId" = excluded."WorkEpisodeId",
                    "MediaAssetId" = excluded."MediaAssetId",
                    "StoredFileId" = excluded."StoredFileId",
                    "DeliveryMode" = excluded."DeliveryMode",
                    "ClientKind" = excluded."ClientKind",
                    "LastUpdatedAt" = excluded."LastUpdatedAt",
                    "EndedAt" = NULL
                """,
                [
                    sessionId,
                    profileId,
                    target.WorkId,
                    episodeId,
                    target.MediaAssetId,
                    target.StoredFileId,
                    normalizedMode,
                    normalizedClient,
                    now
                ],
                cancellationToken);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "ActiveSessions"
                    ("Id", "ProfileId", "WorkId", "WorkEpisodeId", "MediaAssetId", "StoredFileId", "DeliveryMode", "ClientKind", "StartedAt", "LastUpdatedAt", "EndedAt")
                VALUES ({0}, {1}, {2}, NULL, {3}, {4}, {5}, {6}, {7}, {7}, NULL)
                ON CONFLICT ("Id") DO UPDATE
                SET "ProfileId" = excluded."ProfileId",
                    "WorkId" = excluded."WorkId",
                    "WorkEpisodeId" = NULL,
                    "MediaAssetId" = excluded."MediaAssetId",
                    "StoredFileId" = excluded."StoredFileId",
                    "DeliveryMode" = excluded."DeliveryMode",
                    "ClientKind" = excluded."ClientKind",
                    "LastUpdatedAt" = excluded."LastUpdatedAt",
                    "EndedAt" = NULL
                """,
                [
                    sessionId,
                    profileId,
                    target.WorkId,
                    target.MediaAssetId,
                    target.StoredFileId,
                    normalizedMode,
                    normalizedClient,
                    now
                ],
                cancellationToken);
        }

        return await GetAsync(sessionId, profileId, cancellationToken);
    }

    public async Task<ActiveSessionSnapshot?> GetAsync(
        Guid sessionId,
        string profileId,
        CancellationToken cancellationToken = default) =>
        await db.Database.SqlQueryRaw<ActiveSessionSnapshot>(
                """
                SELECT "Id", "ProfileId", "WorkId", "WorkEpisodeId", "MediaAssetId", "StoredFileId",
                       "DeliveryMode", "ClientKind", "StartedAt", "LastUpdatedAt", "EndedAt"
                FROM "ActiveSessions"
                WHERE "Id" = {0} AND "ProfileId" = {1}
                LIMIT 1
                """,
                sessionId,
                profileId)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> TouchAsync(
        Guid sessionId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var updated = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "ActiveSessions"
            SET "LastUpdatedAt" = {0}
            WHERE "Id" = {1} AND "ProfileId" = {2} AND "EndedAt" IS NULL
            """,
            [time.GetUtcNow().UtcDateTime, sessionId, profileId],
            cancellationToken);
        return updated > 0;
    }

    public async Task<bool> EndAsync(
        Guid sessionId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var updated = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "ActiveSessions"
            SET "EndedAt" = {0}, "LastUpdatedAt" = {0}
            WHERE "Id" = {1} AND "ProfileId" = {2} AND "EndedAt" IS NULL
            """,
            [now, sessionId, profileId],
            cancellationToken);
        return updated > 0;
    }

    private sealed record ActiveTargetRow(
        Guid WorkId,
        Guid? WorkEpisodeId,
        Guid MediaAssetId,
        Guid StoredFileId);
}
