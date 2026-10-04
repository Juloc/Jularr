using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Progress;

public sealed record MediaProgressTarget(Guid WorkId, Guid? WorkEpisodeId)
{
    public static MediaProgressTarget Movie(Guid workId) => new(workId, null);

    public static MediaProgressTarget Episode(Guid workId, Guid workEpisodeId) =>
        new(workId, workEpisodeId);
}

public sealed record MediaProgressSnapshot(
    Guid WorkId,
    Guid? WorkEpisodeId,
    long PositionMs,
    long? DurationMs,
    bool IsCompleted,
    DateTime? UpdatedAt)
{
    public int Percent =>
        IsCompleted
            ? 100
            : DurationMs is > 0
                ? Math.Clamp((int)Math.Round(PositionMs * 100d / DurationMs.Value), 0, 100)
                : 0;

    public long ResumePositionMs =>
        PositionMs >= VideoProgressService.MinimumResumeMs
            ? PositionMs
            : 0;
}

public sealed record MediaProgressUpdate(
    long PositionMs,
    long? DurationMs,
    bool Completed);

public enum VideoContinueWatchingKind
{
    Resume,
    UpNext
}

public sealed record VideoContinueWatchingItem(
    VideoContinueWatchingKind Kind,
    WorkMediaType MediaType,
    Guid WorkId,
    Guid? WorkEpisodeId,
    string WorkTitle,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? EpisodeTitle,
    long ResumePositionMs,
    long? DurationMs,
    DateTime UpdatedAt);

public sealed record MediaPlaybackHistoryItem(
    Guid Id,
    WorkMediaType MediaType,
    Guid WorkId,
    Guid? WorkEpisodeId,
    string WorkTitle,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? EpisodeTitle,
    DateTime StartedAt,
    DateTime LastPlayedAt,
    long PositionMs,
    long? DurationMs,
    bool ReachedEnd);

public sealed class VideoProgressService(AppDbContext db)
{
    public const double CompletionThreshold = 0.95;
    public const long MinimumResumeMs = 30_000;
    public const int HistoryLimit = 50;
    public const int ContinueWatchingLimit = 12;
    public static readonly TimeSpan HistorySessionGap = TimeSpan.FromMinutes(30);

    private const int ContinueWatchingCandidateLimit = 500;

    public async Task<MediaProgressSnapshot?> GetAsync(
        string profileId,
        MediaProgressTarget target,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profileId);
        if (!await TargetExistsAsync(target, cancellationToken))
        {
            return null;
        }

        var row = await LoadProgressRowAsync(profileId, target, cancellationToken);
        return row is null
            ? new MediaProgressSnapshot(target.WorkId, target.WorkEpisodeId, 0, null, false, null)
            : ToSnapshot(row);
    }

    public async Task<MediaProgressSnapshot?> UpdateAsync(
        string profileId,
        MediaProgressTarget target,
        MediaProgressUpdate update,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profileId);
        if (!await TargetExistsAsync(target, cancellationToken))
        {
            return null;
        }

        var current = await LoadProgressRowAsync(profileId, target, cancellationToken);
        var positionMs = Math.Max(0, update.PositionMs);
        var durationMs = update.DurationMs is > 0 ? update.DurationMs : null;
        if (durationMs is { } duration)
        {
            positionMs = Math.Min(positionMs, duration);
        }

        var finalDurationMs = durationMs ?? current?.DurationMs;
        var reachedEnd = update.Completed ||
                         (finalDurationMs is { } knownDuration &&
                          positionMs >= knownDuration * CompletionThreshold);

        if (current is null && !reachedEnd && positionMs < MinimumResumeMs)
        {
            return new MediaProgressSnapshot(
                target.WorkId,
                target.WorkEpisodeId,
                positionMs,
                durationMs,
                false,
                null);
        }

        var now = DateTime.UtcNow;
        var storedPosition = reachedEnd ? 0 : positionMs;
        await EnsureProgressRowAsync(
            profileId,
            target,
            storedPosition,
            finalDurationMs,
            reachedEnd,
            now,
            cancellationToken);
        await UpdateProgressRowAsync(
            profileId,
            target,
            storedPosition,
            finalDurationMs,
            reachedEnd,
            now,
            cancellationToken);

        await RecordHistoryAsync(
            profileId,
            target,
            reachedEnd ? finalDurationMs ?? positionMs : positionMs,
            finalDurationMs,
            reachedEnd,
            now,
            cancellationToken);

        return await GetAsync(profileId, target, cancellationToken);
    }

    public async Task<MediaProgressSnapshot?> SetCompletedAsync(
        string profileId,
        MediaProgressTarget target,
        bool completed,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profileId);
        if (!await TargetExistsAsync(target, cancellationToken))
        {
            return null;
        }

        var current = await LoadProgressRowAsync(profileId, target, cancellationToken);
        if (current is null && !completed)
        {
            return new MediaProgressSnapshot(target.WorkId, target.WorkEpisodeId, 0, null, false, null);
        }

        var now = DateTime.UtcNow;
        await EnsureProgressRowAsync(profileId, target, 0, current?.DurationMs, completed, now, cancellationToken);
        await SetCompletedRowAsync(profileId, target, completed, now, cancellationToken);
        return await GetAsync(profileId, target, cancellationToken);
    }

    public async Task<IReadOnlyList<VideoContinueWatchingItem>> GetContinueWatchingAsync(
        string profileId,
        int limit = ContinueWatchingLimit,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profileId);
        limit = Math.Clamp(limit, 1, ContinueWatchingLimit);

        var rows = await db.Database.SqlQueryRaw<ContinueWatchingDbRow>(
                """
                SELECT p."WorkId",
                       p."WorkEpisodeId",
                       p."PositionMs",
                       p."DurationMs",
                       p."IsCompleted",
                       p."UpdatedAt",
                       w."MediaType",
                       w."CanonicalTitle" AS "WorkTitle",
                       e."SeasonNumber",
                       e."EpisodeNumber",
                       e."Title" AS "EpisodeTitle"
                FROM "MediaProgress" p
                JOIN "Works" w ON w."Id" = p."WorkId"
                LEFT JOIN "WorkEpisodes" e ON e."Id" = p."WorkEpisodeId"
                WHERE p."ProfileId" = {0}
                  AND (p."IsCompleted" = TRUE OR p."PositionMs" >= {1})
                  AND EXISTS (
                      SELECT 1
                      FROM "MediaAssets" a
                      JOIN "StoredFiles" f ON f."MediaAssetId" = a."Id"
                      WHERE a."Kind" = 0
                        AND a."WorkId" = p."WorkId"
                        AND (
                            (p."WorkEpisodeId" IS NULL AND a."WorkEpisodeId" IS NULL)
                            OR a."WorkEpisodeId" = p."WorkEpisodeId"
                        )
                  )
                ORDER BY p."UpdatedAt" DESC
                LIMIT 500
                """,
                profileId,
                MinimumResumeMs)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var anchors = rows
            .OrderByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.WorkEpisodeId?.ToString("D") ?? x.WorkId.ToString("D"), StringComparer.Ordinal)
            .GroupBy(x => x.WorkId)
            .Select(group => group.First())
            .ToArray();

        var completedEpisodeWorkIds = anchors
            .Where(x => x.IsCompleted && x.WorkEpisodeId.HasValue && (WorkMediaType)x.MediaType != WorkMediaType.Movie)
            .Select(x => x.WorkId)
            .Distinct()
            .ToArray();

        var playableEpisodes = completedEpisodeWorkIds.Length == 0
            ? []
            : await db.WorkEpisodes
                .AsNoTracking()
                .Where(episode =>
                    completedEpisodeWorkIds.Contains(episode.WorkId) &&
                    db.MediaAssets.Any(asset =>
                        asset.Kind == MediaAssetKind.Video &&
                        asset.WorkEpisodeId == episode.Id &&
                        db.StoredFiles.Any(file => file.MediaAssetId == asset.Id)))
                .Select(episode => new EpisodeCandidate(
                    episode.Id,
                    episode.WorkId,
                    episode.SeasonNumber,
                    episode.EpisodeNumber,
                    episode.Title))
                .ToListAsync(cancellationToken);

        var progressByEpisode = rows
            .Where(x => x.WorkEpisodeId.HasValue)
            .GroupBy(x => x.WorkEpisodeId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.UpdatedAt).First());

        var result = new List<VideoContinueWatchingItem>(limit);
        foreach (var anchor in anchors)
        {
            if (result.Count >= limit)
            {
                break;
            }

            if (!anchor.IsCompleted)
            {
                result.Add(ToContinueWatching(VideoContinueWatchingKind.Resume, anchor));
                continue;
            }

            if (anchor.WorkEpisodeId is not { } currentEpisodeId ||
                (WorkMediaType)anchor.MediaType == WorkMediaType.Movie)
            {
                continue;
            }

            var current = playableEpisodes.FirstOrDefault(x => x.Id == currentEpisodeId);
            if (current is null)
            {
                continue;
            }

            var next = playableEpisodes
                .Where(x => x.WorkId == anchor.WorkId)
                .OrderBy(x => x.SeasonNumber)
                .ThenBy(x => x.EpisodeNumber)
                .FirstOrDefault(x =>
                    (x.SeasonNumber > current.SeasonNumber ||
                     (x.SeasonNumber == current.SeasonNumber && x.EpisodeNumber > current.EpisodeNumber)) &&
                    (!progressByEpisode.TryGetValue(x.Id, out var progress) || !progress.IsCompleted));

            if (next is null)
            {
                continue;
            }

            progressByEpisode.TryGetValue(next.Id, out var nextProgress);
            result.Add(new VideoContinueWatchingItem(
                VideoContinueWatchingKind.UpNext,
                (WorkMediaType)anchor.MediaType,
                anchor.WorkId,
                next.Id,
                anchor.WorkTitle,
                next.SeasonNumber,
                next.EpisodeNumber,
                next.Title,
                nextProgress?.PositionMs ?? 0,
                nextProgress?.DurationMs,
                anchor.UpdatedAt));
        }

        return result;
    }

    public async Task<IReadOnlyList<MediaPlaybackHistoryItem>> GetHistoryAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profileId);
        var rows = await db.Database.SqlQueryRaw<HistoryDbRow>(
                """
                SELECT h."Id",
                       h."WorkId",
                       h."WorkEpisodeId",
                       h."StartedAt",
                       h."LastPlayedAt",
                       h."PositionMs",
                       h."DurationMs",
                       h."ReachedEnd",
                       w."MediaType",
                       w."CanonicalTitle" AS "WorkTitle",
                       e."SeasonNumber",
                       e."EpisodeNumber",
                       e."Title" AS "EpisodeTitle"
                FROM "MediaPlaybackHistory" h
                JOIN "Works" w ON w."Id" = h."WorkId"
                LEFT JOIN "WorkEpisodes" e ON e."Id" = h."WorkEpisodeId"
                WHERE h."ProfileId" = {0}
                ORDER BY h."LastPlayedAt" DESC, h."StartedAt" DESC
                LIMIT 50
                """,
                profileId)
            .ToListAsync(cancellationToken);

        return rows.Select(row => new MediaPlaybackHistoryItem(
                row.Id,
                (WorkMediaType)row.MediaType,
                row.WorkId,
                row.WorkEpisodeId,
                row.WorkTitle,
                row.SeasonNumber,
                row.EpisodeNumber,
                row.EpisodeTitle,
                row.StartedAt,
                row.LastPlayedAt,
                row.PositionMs,
                row.DurationMs,
                row.ReachedEnd))
            .ToArray();
    }

    public Task<int> ClearHistoryAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profileId);
        return db.Database.ExecuteSqlRawAsync(
            """DELETE FROM "MediaPlaybackHistory" WHERE "ProfileId" = {0}""",
            [profileId],
            cancellationToken);
    }

    public async Task ImportProgressAsync(
        string profileId,
        MediaProgressTarget target,
        long positionMs,
        long? durationMs,
        bool completed,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profileId);
        await EnsureProgressRowAsync(
            profileId,
            target,
            Math.Max(0, positionMs),
            durationMs is > 0 ? durationMs : null,
            completed,
            updatedAt,
            cancellationToken);

        var existing = await LoadProgressRowAsync(profileId, target, cancellationToken);
        if (existing is null || existing.UpdatedAt > updatedAt)
        {
            return;
        }

        await UpdateProgressRowAsync(
            profileId,
            target,
            completed ? 0 : Math.Max(0, positionMs),
            durationMs is > 0 ? durationMs : null,
            completed,
            updatedAt,
            cancellationToken);
    }

    public async Task ImportHistoryAsync(
        Guid id,
        string profileId,
        MediaProgressTarget target,
        DateTime startedAt,
        DateTime lastPlayedAt,
        long positionMs,
        long? durationMs,
        bool reachedEnd,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profileId);
        var sql = target.WorkEpisodeId.HasValue
            ? """
              INSERT INTO "MediaPlaybackHistory"
                  ("Id", "ProfileId", "WorkId", "WorkEpisodeId", "StartedAt", "LastPlayedAt", "PositionMs", "DurationMs", "ReachedEnd")
              VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8})
              ON CONFLICT ("Id") DO NOTHING
              """
            : """
              INSERT INTO "MediaPlaybackHistory"
                  ("Id", "ProfileId", "WorkId", "WorkEpisodeId", "StartedAt", "LastPlayedAt", "PositionMs", "DurationMs", "ReachedEnd")
              VALUES ({0}, {1}, {2}, NULL, {3}, {4}, {5}, {6}, {7})
              ON CONFLICT ("Id") DO NOTHING
              """;

        if (target.WorkEpisodeId is { } episodeId)
        {
            await ExecuteDurationAwareAsync(
                sql,
                durationMs,
                [id, profileId, target.WorkId, episodeId, startedAt, lastPlayedAt, Math.Max(0, positionMs)],
                reachedEnd,
                cancellationToken);
        }
        else
        {
            await ExecuteDurationAwareAsync(
                sql,
                durationMs,
                [id, profileId, target.WorkId, startedAt, lastPlayedAt, Math.Max(0, positionMs)],
                reachedEnd,
                cancellationToken);
        }
    }

    private async Task<bool> TargetExistsAsync(
        MediaProgressTarget target,
        CancellationToken cancellationToken)
    {
        var workType = await db.Works
            .AsNoTracking()
            .Where(x => x.Id == target.WorkId)
            .Select(x => (WorkMediaType?)x.MediaType)
            .SingleOrDefaultAsync(cancellationToken);

        if (workType is null)
        {
            return false;
        }

        if (target.WorkEpisodeId is null)
        {
            return workType == WorkMediaType.Movie;
        }

        if (workType is not (WorkMediaType.Anime or WorkMediaType.Series))
        {
            return false;
        }

        return await db.WorkEpisodes
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == target.WorkEpisodeId.Value && x.WorkId == target.WorkId,
                cancellationToken);
    }

    private async Task<MediaProgressDbRow?> LoadProgressRowAsync(
        string profileId,
        MediaProgressTarget target,
        CancellationToken cancellationToken)
    {
        if (target.WorkEpisodeId is { } episodeId)
        {
            return await db.Database.SqlQueryRaw<MediaProgressDbRow>(
                    """
                    SELECT "Id", "ProfileId", "WorkId", "WorkEpisodeId", "PositionMs", "DurationMs", "IsCompleted", "UpdatedAt"
                    FROM "MediaProgress"
                    WHERE "ProfileId" = {0} AND "WorkEpisodeId" = {1}
                    LIMIT 1
                    """,
                    profileId,
                    episodeId)
                .SingleOrDefaultAsync(cancellationToken);
        }

        return await db.Database.SqlQueryRaw<MediaProgressDbRow>(
                """
                SELECT "Id", "ProfileId", "WorkId", "WorkEpisodeId", "PositionMs", "DurationMs", "IsCompleted", "UpdatedAt"
                FROM "MediaProgress"
                WHERE "ProfileId" = {0} AND "WorkId" = {1} AND "WorkEpisodeId" IS NULL
                LIMIT 1
                """,
                profileId,
                target.WorkId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task EnsureProgressRowAsync(
        string profileId,
        MediaProgressTarget target,
        long positionMs,
        long? durationMs,
        bool completed,
        DateTime updatedAt,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        if (target.WorkEpisodeId is { } episodeId)
        {
            var sql = durationMs.HasValue
                ? """
                  INSERT INTO "MediaProgress"
                      ("Id", "ProfileId", "WorkId", "WorkEpisodeId", "PositionMs", "DurationMs", "IsCompleted", "UpdatedAt")
                  VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7})
                  ON CONFLICT DO NOTHING
                  """
                : """
                  INSERT INTO "MediaProgress"
                      ("Id", "ProfileId", "WorkId", "WorkEpisodeId", "PositionMs", "DurationMs", "IsCompleted", "UpdatedAt")
                  VALUES ({0}, {1}, {2}, {3}, {4}, NULL, {5}, {6})
                  ON CONFLICT DO NOTHING
                  """;

            object[] args = durationMs.HasValue
                ? [id, profileId, target.WorkId, episodeId, positionMs, durationMs.Value, completed, updatedAt]
                : [id, profileId, target.WorkId, episodeId, positionMs, completed, updatedAt];
            await db.Database.ExecuteSqlRawAsync(sql, args, cancellationToken);
            return;
        }

        var movieSql = durationMs.HasValue
            ? """
              INSERT INTO "MediaProgress"
                  ("Id", "ProfileId", "WorkId", "WorkEpisodeId", "PositionMs", "DurationMs", "IsCompleted", "UpdatedAt")
              VALUES ({0}, {1}, {2}, NULL, {3}, {4}, {5}, {6})
              ON CONFLICT DO NOTHING
              """
            : """
              INSERT INTO "MediaProgress"
                  ("Id", "ProfileId", "WorkId", "WorkEpisodeId", "PositionMs", "DurationMs", "IsCompleted", "UpdatedAt")
              VALUES ({0}, {1}, {2}, NULL, {3}, NULL, {4}, {5})
              ON CONFLICT DO NOTHING
              """;

        object[] movieArgs = durationMs.HasValue
            ? [id, profileId, target.WorkId, positionMs, durationMs.Value, completed, updatedAt]
            : [id, profileId, target.WorkId, positionMs, completed, updatedAt];
        await db.Database.ExecuteSqlRawAsync(movieSql, movieArgs, cancellationToken);
    }

    private async Task UpdateProgressRowAsync(
        string profileId,
        MediaProgressTarget target,
        long positionMs,
        long? durationMs,
        bool completed,
        DateTime updatedAt,
        CancellationToken cancellationToken)
    {
        var durationSql = durationMs.HasValue ? ""DurationMs" = {0}," : ""DurationMs" = NULL,";
        var where = target.WorkEpisodeId.HasValue
            ? ""ProfileId" = {5} AND "WorkEpisodeId" = {6}"
            : ""ProfileId" = {5} AND "WorkId" = {6} AND "WorkEpisodeId" IS NULL";

        if (durationMs.HasValue)
        {
            await db.Database.ExecuteSqlRawAsync(
                $"""
                UPDATE "MediaProgress"
                SET "DurationMs" = {{0}},
                    "PositionMs" = {{1}},
                    "IsCompleted" = CASE WHEN "IsCompleted" = TRUE OR {{2}} = TRUE THEN TRUE ELSE FALSE END,
                    "UpdatedAt" = {{3}}
                WHERE {(target.WorkEpisodeId.HasValue ? ""ProfileId" = {4} AND "WorkEpisodeId" = {5}" : ""ProfileId" = {4} AND "WorkId" = {5} AND "WorkEpisodeId" IS NULL")}
                """,
                [durationMs.Value, positionMs, completed, updatedAt, profileId, target.WorkEpisodeId ?? target.WorkId],
                cancellationToken);
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            $"""
            UPDATE "MediaProgress"
            SET "PositionMs" = {{0}},
                "IsCompleted" = CASE WHEN "IsCompleted" = TRUE OR {{1}} = TRUE THEN TRUE ELSE FALSE END,
                "UpdatedAt" = {{2}}
            WHERE {(target.WorkEpisodeId.HasValue ? ""ProfileId" = {3} AND "WorkEpisodeId" = {4}" : ""ProfileId" = {3} AND "WorkId" = {4} AND "WorkEpisodeId" IS NULL")}
            """,
            [positionMs, completed, updatedAt, profileId, target.WorkEpisodeId ?? target.WorkId],
            cancellationToken);
    }

    private async Task SetCompletedRowAsync(
        string profileId,
        MediaProgressTarget target,
        bool completed,
        DateTime updatedAt,
        CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            $"""
            UPDATE "MediaProgress"
            SET "PositionMs" = 0,
                "IsCompleted" = {{0}},
                "UpdatedAt" = {{1}}
            WHERE {(target.WorkEpisodeId.HasValue ? ""ProfileId" = {2} AND "WorkEpisodeId" = {3}" : ""ProfileId" = {2} AND "WorkId" = {3} AND "WorkEpisodeId" IS NULL")}
            """,
            [completed, updatedAt, profileId, target.WorkEpisodeId ?? target.WorkId],
            cancellationToken);
    }

    private async Task RecordHistoryAsync(
        string profileId,
        MediaProgressTarget target,
        long positionMs,
        long? durationMs,
        bool reachedEnd,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var latest = target.WorkEpisodeId is { } episodeId
            ? await db.Database.SqlQueryRaw<HistoryIdentityDbRow>(
                    """
                    SELECT "Id", "WorkId", "WorkEpisodeId", "LastPlayedAt", "DurationMs", "ReachedEnd"
                    FROM "MediaPlaybackHistory"
                    WHERE "ProfileId" = {0}
                    ORDER BY "LastPlayedAt" DESC
                    LIMIT 1
                    """,
                    profileId)
                .SingleOrDefaultAsync(cancellationToken)
            : await db.Database.SqlQueryRaw<HistoryIdentityDbRow>(
                    """
                    SELECT "Id", "WorkId", "WorkEpisodeId", "LastPlayedAt", "DurationMs", "ReachedEnd"
                    FROM "MediaPlaybackHistory"
                    WHERE "ProfileId" = {0}
                    ORDER BY "LastPlayedAt" DESC
                    LIMIT 1
                    """,
                    profileId)
                .SingleOrDefaultAsync(cancellationToken);

        var sameTarget = latest is not null &&
                         latest.WorkId == target.WorkId &&
                         latest.WorkEpisodeId == target.WorkEpisodeId;
        if (sameTarget && now - latest!.LastPlayedAt <= HistorySessionGap)
        {
            if (durationMs.HasValue)
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    UPDATE "MediaPlaybackHistory"
                    SET "LastPlayedAt" = {0},
                        "PositionMs" = {1},
                        "DurationMs" = {2},
                        "ReachedEnd" = CASE WHEN "ReachedEnd" = TRUE OR {3} = TRUE THEN TRUE ELSE FALSE END
                    WHERE "Id" = {4}
                    """,
                    [now, positionMs, durationMs.Value, reachedEnd, latest.Id],
                    cancellationToken);
            }
            else
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    UPDATE "MediaPlaybackHistory"
                    SET "LastPlayedAt" = {0},
                        "PositionMs" = {1},
                        "ReachedEnd" = CASE WHEN "ReachedEnd" = TRUE OR {2} = TRUE THEN TRUE ELSE FALSE END
                    WHERE "Id" = {3}
                    """,
                    [now, positionMs, reachedEnd, latest.Id],
                    cancellationToken);
            }

            return;
        }

        await ImportHistoryAsync(
            Guid.NewGuid(),
            profileId,
            target,
            now,
            now,
            positionMs,
            durationMs,
            reachedEnd,
            cancellationToken);
        await TrimHistoryAsync(profileId, cancellationToken);
    }

    private async Task TrimHistoryAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        var stale = await db.Database.SqlQueryRaw<Guid>(
                """
                SELECT "Id" AS "Value"
                FROM "MediaPlaybackHistory"
                WHERE "ProfileId" = {0}
                ORDER BY "LastPlayedAt" DESC, "StartedAt" DESC
                LIMIT 100000 OFFSET 50
                """,
                profileId)
            .ToListAsync(cancellationToken);

        foreach (var id in stale)
        {
            await db.Database.ExecuteSqlRawAsync(
                """DELETE FROM "MediaPlaybackHistory" WHERE "Id" = {0}""",
                [id],
                cancellationToken);
        }
    }

    private async Task ExecuteDurationAwareAsync(
        string sql,
        long? durationMs,
        object[] prefix,
        bool reachedEnd,
        CancellationToken cancellationToken)
    {
        object[] args;
        if (durationMs.HasValue)
        {
            args = [.. prefix, durationMs.Value, reachedEnd];
        }
        else
        {
            sql = sql.Replace(
                prefix.Length == 7 ? "{7}" : "{6}",
                "NULL",
                StringComparison.Ordinal);
            args = [.. prefix, reachedEnd];
        }

        await db.Database.ExecuteSqlRawAsync(sql, args, cancellationToken);
    }

    private static void ValidateProfile(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (profileId.Length > 80)
        {
            throw new ArgumentOutOfRangeException(nameof(profileId));
        }
    }

    private static MediaProgressSnapshot ToSnapshot(MediaProgressDbRow row) =>
        new(
            row.WorkId,
            row.WorkEpisodeId,
            row.PositionMs,
            row.DurationMs,
            row.IsCompleted,
            row.UpdatedAt);

    private static VideoContinueWatchingItem ToContinueWatching(
        VideoContinueWatchingKind kind,
        ContinueWatchingDbRow row) =>
        new(
            kind,
            (WorkMediaType)row.MediaType,
            row.WorkId,
            row.WorkEpisodeId,
            row.WorkTitle,
            row.SeasonNumber,
            row.EpisodeNumber,
            row.EpisodeTitle,
            row.PositionMs,
            row.DurationMs,
            row.UpdatedAt);

    private sealed record MediaProgressDbRow(
        Guid Id,
        string ProfileId,
        Guid WorkId,
        Guid? WorkEpisodeId,
        long PositionMs,
        long? DurationMs,
        bool IsCompleted,
        DateTime UpdatedAt);

    private sealed record HistoryIdentityDbRow(
        Guid Id,
        Guid WorkId,
        Guid? WorkEpisodeId,
        DateTime LastPlayedAt,
        long? DurationMs,
        bool ReachedEnd);

    private sealed record ContinueWatchingDbRow(
        Guid WorkId,
        Guid? WorkEpisodeId,
        long PositionMs,
        long? DurationMs,
        bool IsCompleted,
        DateTime UpdatedAt,
        int MediaType,
        string WorkTitle,
        int? SeasonNumber,
        int? EpisodeNumber,
        string? EpisodeTitle);

    private sealed record HistoryDbRow(
        Guid Id,
        Guid WorkId,
        Guid? WorkEpisodeId,
        DateTime StartedAt,
        DateTime LastPlayedAt,
        long PositionMs,
        long? DurationMs,
        bool ReachedEnd,
        int MediaType,
        string WorkTitle,
        int? SeasonNumber,
        int? EpisodeNumber,
        string? EpisodeTitle);

    private sealed record EpisodeCandidate(
        Guid Id,
        Guid WorkId,
        int SeasonNumber,
        int EpisodeNumber,
        string? Title);
}

public sealed class CanonicalVideoTargetResolver(
    AppDbContext db,
    LegacyWorkBridge bridge)
{
    public async Task<MediaProgressTarget?> ResolveLegacyEpisodeAsync(
        Guid episodeId,
        CancellationToken cancellationToken = default)
    {
        var legacy = await (
                from episode in db.Episodes
                join anime in db.Anime on episode.AnimeId equals anime.Id
                where episode.Id == episodeId
                select new { Episode = episode, Anime = anime })
            .SingleOrDefaultAsync(cancellationToken);
        if (legacy is null)
        {
            return null;
        }

        var episodeWorkId = await db.WorkSourceLinks
            .AsNoTracking()
            .Where(x => x.SourceKind == WorkSourceKind.Episode && x.SourceId == episodeId)
            .Select(x => (Guid?)x.WorkId)
            .SingleOrDefaultAsync(cancellationToken);

        var workId = episodeWorkId ?? await db.WorkSourceLinks
            .AsNoTracking()
            .Where(x => x.SourceKind == WorkSourceKind.Anime && x.SourceId == legacy.Anime.Id)
            .Select(x => (Guid?)x.WorkId)
            .SingleOrDefaultAsync(cancellationToken);

        workId ??= await bridge.EnsureWorkForAnimeAsync(legacy.Anime, cancellationToken);

        if (episodeWorkId is null)
        {
            db.WorkSourceLinks.Add(new WorkSourceLink
            {
                WorkId = workId.Value,
                SourceKind = WorkSourceKind.Episode,
                SourceId = episodeId
            });
        }

        var canonicalEpisode = await db.WorkEpisodes
            .SingleOrDefaultAsync(
                x => x.WorkId == workId.Value &&
                     x.SeasonNumber == legacy.Episode.SeasonNumber &&
                     x.EpisodeNumber == legacy.Episode.Number,
                cancellationToken);

        if (canonicalEpisode is null)
        {
            canonicalEpisode = new WorkEpisode
            {
                WorkId = workId.Value,
                SeasonNumber = legacy.Episode.SeasonNumber,
                EpisodeNumber = legacy.Episode.Number,
                IsSpecial = legacy.Episode.SeasonNumber == 0,
                Title = legacy.Episode.Title
            };
            db.WorkEpisodes.Add(canonicalEpisode);
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return MediaProgressTarget.Episode(workId.Value, canonicalEpisode.Id);
    }
}

public sealed class CanonicalVideoProgressBackfillService(
    AppDbContext db,
    CanonicalVideoTargetResolver targets,
    VideoProgressService progress)
{
    public async Task<int> BackfillLegacyAnimeAsync(
        CancellationToken cancellationToken = default)
    {
        var legacyProgress = await db.EpisodeProgress.AsNoTracking().ToListAsync(cancellationToken);
        var legacyHistory = await db.EpisodePlaybackHistory.AsNoTracking().ToListAsync(cancellationToken);
        var episodeIds = legacyProgress.Select(x => x.EpisodeId)
            .Concat(legacyHistory.Select(x => x.EpisodeId))
            .Distinct()
            .ToArray();

        var resolved = new Dictionary<Guid, MediaProgressTarget>();
        foreach (var episodeId in episodeIds)
        {
            var target = await targets.ResolveLegacyEpisodeAsync(episodeId, cancellationToken);
            if (target is not null)
            {
                resolved[episodeId] = target;
            }
        }

        var imported = 0;
        foreach (var row in legacyProgress)
        {
            if (!resolved.TryGetValue(row.EpisodeId, out var target))
            {
                continue;
            }

            await progress.ImportProgressAsync(
                row.ProfileId,
                target,
                row.PositionMs,
                row.DurationMs,
                row.IsCompleted,
                row.UpdatedAt,
                cancellationToken);
            imported++;
        }

        foreach (var row in legacyHistory)
        {
            if (!resolved.TryGetValue(row.EpisodeId, out var target))
            {
                continue;
            }

            await progress.ImportHistoryAsync(
                row.Id,
                row.ProfileId,
                target,
                row.StartedAt,
                row.LastPlayedAt,
                row.PositionMs,
                row.DurationMs,
                row.ReachedEnd,
                cancellationToken);
        }

        return imported;
    }
}
