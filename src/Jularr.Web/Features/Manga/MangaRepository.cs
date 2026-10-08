using System.Data;
using System.Data.Common;
using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Manga;

public sealed class MangaRepository(AppDbContext db)
{
    public async Task<IReadOnlyList<MangaSeriesItem>> GetLibraryAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        return await QueryAsync(
            """
            SELECT
                s."Id",
                COALESCE(s."MetadataTitle", s."Title") AS "Title",
                s."MetadataNativeTitle",
                s."CoverImageUrl",
                (SELECT COUNT(*) FROM "MangaChapters" c WHERE c."SeriesId" = s."Id") AS "ChapterCount",
                (SELECT c0."Id" FROM "MangaChapters" c0 WHERE c0."SeriesId" = s."Id" ORDER BY c0."Number" LIMIT 1) AS "PreviewChapterId",
                p."ChapterId",
                c."Number",
                COALESCE(p."PageIndex", 0),
                COALESCE(c."PageCount", 0),
                p."UpdatedAt"
            FROM "MangaSeries" s
            LEFT JOIN "MangaProgress" p
                ON p."SeriesId" = s."Id" AND p."ProfileId" = @profileId
            LEFT JOIN "MangaChapters" c
                ON c."Id" = p."ChapterId"
            ORDER BY
                CASE WHEN p."UpdatedAt" IS NULL THEN 1 ELSE 0 END,
                p."UpdatedAt" DESC,
                lower(COALESCE(s."MetadataTitle", s."Title"));
            """,
            command => AddParameter(command, "@profileId", profileId),
            reader => new MangaSeriesItem(
                ReadGuid(reader, 0),
                reader.GetString(1),
                ReadNullableString(reader, 2),
                ReadNullableString(reader, 3),
                reader.GetInt32(4),
                ReadNullableGuid(reader, 5),
                ReadNullableGuid(reader, 6),
                ReadNullableDouble(reader, 7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                ReadNullableDateTime(reader, 10)),
            cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, Guid>> GetAniListMatchesAsync(
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken)
    {
        if (externalIds.Count == 0)
        {
            return new Dictionary<string, Guid>(StringComparer.Ordinal);
        }

        var wanted = externalIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);

        if (wanted.Count == 0)
        {
            return new Dictionary<string, Guid>(StringComparer.Ordinal);
        }

        var rows = await QueryAsync(
            """
            SELECT "Id", "MetadataExternalId"
            FROM "MangaSeries"
            WHERE "MetadataProvider" = @provider
              AND "MetadataExternalId" IS NOT NULL;
            """,
            command => AddParameter(command, "@provider", "anilist"),
            reader => new
            {
                Id = ReadGuid(reader, 0),
                ExternalId = reader.GetString(1)
            },
            cancellationToken);

        var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (wanted.Contains(row.ExternalId))
            {
                result.TryAdd(row.ExternalId, row.Id);
            }
        }

        return result;
    }

    /// <summary>The series already matched to this metadata entry (for example one AniList id).</summary>
    public async Task<MangaSeriesLocation?> FindByIdAsync(Guid seriesId, CancellationToken cancellationToken)
    {
        return (await QueryAsync(
            """
            SELECT "Id", "Title", "SourcePath"
            FROM "MangaSeries"
            WHERE "Id" = @id
            LIMIT 1;
            """,
            command => AddParameter(command, "@id", seriesId.ToString()),
            reader => new MangaSeriesLocation(
                ReadGuid(reader, 0),
                reader.GetString(1),
                reader.GetString(2)),
            cancellationToken)).SingleOrDefault();
    }

    public async Task<MangaSeriesLocation?> FindByMetadataAsync(
        string provider,
        string externalId,
        CancellationToken cancellationToken)
    {
        return (await QueryAsync(
            """
            SELECT "Id", "Title", "SourcePath"
            FROM "MangaSeries"
            WHERE "MetadataProvider" = @provider AND "MetadataExternalId" = @externalId
            LIMIT 1;
            """,
            command =>
            {
                AddParameter(command, "@provider", provider);
                AddParameter(command, "@externalId", externalId);
            },
            reader => new MangaSeriesLocation(
                ReadGuid(reader, 0),
                reader.GetString(1),
                reader.GetString(2)),
            cancellationToken)).SingleOrDefault();
    }

    public async Task<MangaAutoMatchSource?> GetAutoMatchSourceAsync(
        Guid seriesId,
        CancellationToken cancellationToken)
    {
        return (await QueryAsync(
            """
            SELECT "Id", "Title", "MetadataExternalId"
            FROM "MangaSeries"
            WHERE "Id" = @seriesId
            LIMIT 1;
            """,
            command => AddParameter(command, "@seriesId", seriesId.ToString()),
            reader => new MangaAutoMatchSource(
                ReadGuid(reader, 0),
                reader.GetString(1),
                ReadNullableString(reader, 2)),
            cancellationToken)).SingleOrDefault();
    }

    public async Task<MangaAniListProgressContext?> GetAniListProgressContextAsync(
        string profileId,
        Guid seriesId,
        CancellationToken cancellationToken)
    {
        return (await QueryAsync(
            """
            SELECT
                s."Id",
                COALESCE(s."MetadataTitle", s."Title"),
                s."MetadataExternalId",
                c."Number",
                c."VolumeNumber",
                p."PageIndex",
                c."PageCount"
            FROM "MangaSeries" s
            JOIN "MangaProgress" p
                ON p."SeriesId" = s."Id" AND p."ProfileId" = @profileId
            JOIN "MangaChapters" c
                ON c."Id" = p."ChapterId" AND c."SeriesId" = s."Id"
            WHERE s."Id" = @seriesId
            LIMIT 1;
            """,
            command =>
            {
                AddParameter(command, "@profileId", profileId);
                AddParameter(command, "@seriesId", seriesId.ToString());
            },
            reader => new MangaAniListProgressContext(
                ReadGuid(reader, 0),
                reader.GetString(1),
                ReadNullableString(reader, 2),
                reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6)),
            cancellationToken)).SingleOrDefault();
    }

    public async Task<MangaSeriesDetail?> GetSeriesAsync(
        Guid seriesId,
        CancellationToken cancellationToken)
    {
        var series = (await QueryAsync(
            """
            SELECT
                s."Id",
                COALESCE(s."MetadataTitle", s."Title"),
                s."MetadataNativeTitle",
                s."MetadataDescription",
                s."CoverImageUrl",
                s."BannerImageUrl",
                s."MetadataStatus",
                s."Direction",
                s."SourcePath"
            FROM "MangaSeries" s
            WHERE s."Id" = @id
            LIMIT 1;
            """,
            command => AddParameter(command, "@id", seriesId.ToString()),
            reader => new
            {
                Id = ReadGuid(reader, 0),
                Title = reader.GetString(1),
                NativeTitle = ReadNullableString(reader, 2),
                Description = ReadNullableString(reader, 3),
                CoverImageUrl = ReadNullableString(reader, 4),
                BannerImageUrl = ReadNullableString(reader, 5),
                Status = ReadNullableString(reader, 6),
                Direction = reader.GetString(7),
                SourcePath = reader.GetString(8)
            },
            cancellationToken)).SingleOrDefault();

        if (series is null)
        {
            return null;
        }

        var chapters = await GetChaptersAsync(seriesId, cancellationToken);
        return new MangaSeriesDetail(
            series.Id,
            series.Title,
            series.NativeTitle,
            series.Description,
            series.CoverImageUrl,
            series.BannerImageUrl,
            series.Status,
            series.Direction,
            series.SourcePath,
            chapters);
    }

    public async Task<IReadOnlyList<MangaChapterItem>> GetChaptersAsync(
        Guid seriesId,
        CancellationToken cancellationToken)
    {
        return await QueryAsync(
            """
            SELECT
                "Id",
                "SeriesId",
                "Number",
                "VolumeNumber",
                "Title",
                "PageCount",
                "SourceKind",
                "SourceUpdatedAt"
            FROM "MangaChapters"
            WHERE "SeriesId" = @seriesId
            ORDER BY "Number", lower("Title");
            """,
            command => AddParameter(command, "@seriesId", seriesId.ToString()),
            reader => new MangaChapterItem(
                ReadGuid(reader, 0),
                ReadGuid(reader, 1),
                reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetString(6),
                ReadDateTime(reader, 7)),
            cancellationToken);
    }

    public async Task<MangaChapterRead?> GetChapterAsync(
        Guid chapterId,
        CancellationToken cancellationToken)
    {
        return (await QueryAsync(
            """
            SELECT
                c."Id",
                c."SeriesId",
                COALESCE(s."MetadataTitle", s."Title"),
                c."Number",
                c."VolumeNumber",
                c."Title",
                c."PageCount",
                s."Direction"
            FROM "MangaChapters" c
            JOIN "MangaSeries" s ON s."Id" = c."SeriesId"
            WHERE c."Id" = @id
            LIMIT 1;
            """,
            command => AddParameter(command, "@id", chapterId.ToString()),
            reader => new MangaChapterRead(
                ReadGuid(reader, 0),
                ReadGuid(reader, 1),
                reader.GetString(2),
                reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetString(7)),
            cancellationToken)).SingleOrDefault();
    }

    public async Task<(Guid? Previous, Guid? Next)> GetAdjacentChapterIdsAsync(
        Guid seriesId,
        double chapterNumber,
        CancellationToken cancellationToken)
    {
        var previous = await QueryScalarGuidAsync(
            """
            SELECT "Id" FROM "MangaChapters"
            WHERE "SeriesId" = @seriesId AND "Number" < @number
            ORDER BY "Number" DESC LIMIT 1;
            """,
            command =>
            {
                AddParameter(command, "@seriesId", seriesId.ToString());
                AddParameter(command, "@number", chapterNumber);
            },
            cancellationToken);

        var next = await QueryScalarGuidAsync(
            """
            SELECT "Id" FROM "MangaChapters"
            WHERE "SeriesId" = @seriesId AND "Number" > @number
            ORDER BY "Number" LIMIT 1;
            """,
            command =>
            {
                AddParameter(command, "@seriesId", seriesId.ToString());
                AddParameter(command, "@number", chapterNumber);
            },
            cancellationToken);

        return (previous, next);
    }

    public async Task<MangaPageItem?> GetPageAsync(
        Guid chapterId,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        return (await QueryAsync(
            """
            SELECT "ChapterId", "PageIndex", "CachedPath", "MimeType"
            FROM "MangaPages"
            WHERE "ChapterId" = @chapterId AND "PageIndex" = @pageIndex
            LIMIT 1;
            """,
            command =>
            {
                AddParameter(command, "@chapterId", chapterId.ToString());
                AddParameter(command, "@pageIndex", pageIndex);
            },
            reader => new MangaPageItem(
                ReadGuid(reader, 0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3)),
            cancellationToken)).SingleOrDefault();
    }

    public async Task<MangaProgressItem?> GetProgressAsync(
        string profileId,
        Guid seriesId,
        CancellationToken cancellationToken)
    {
        return (await QueryAsync(
            """
            SELECT "SeriesId", "ChapterId", "PageIndex", "UpdatedAt"
            FROM "MangaProgress"
            WHERE "ProfileId" = @profileId AND "SeriesId" = @seriesId
            LIMIT 1;
            """,
            command =>
            {
                AddParameter(command, "@profileId", profileId);
                AddParameter(command, "@seriesId", seriesId.ToString());
            },
            reader => new MangaProgressItem(
                ReadGuid(reader, 0),
                ReadGuid(reader, 1),
                reader.GetInt32(2),
                ReadDateTime(reader, 3)),
            cancellationToken)).SingleOrDefault();
    }

    public async Task SaveProgressAsync(
        string profileId,
        MangaChapterRead chapter,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(pageIndex, 0, Math.Max(0, chapter.PageCount - 1));
        var now = DateTime.UtcNow.ToString("O");

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "MangaProgress"
                ("Id", "ProfileId", "SeriesId", "ChapterId", "PageIndex", "UpdatedAt")
            VALUES
                ({Guid.NewGuid().ToString()}, {profileId}, {chapter.SeriesId.ToString()},
                 {chapter.Id.ToString()}, {clamped}, {now})
            ON CONFLICT("ProfileId", "SeriesId") DO UPDATE SET
                "ChapterId" = excluded."ChapterId",
                "PageIndex" = excluded."PageIndex",
                "UpdatedAt" = excluded."UpdatedAt";
            """,
            cancellationToken);
    }

    public async Task<IReadOnlyList<MangaBookmarkItem>> GetBookmarksAsync(
        string profileId,
        Guid seriesId,
        CancellationToken cancellationToken)
    {
        return await QueryAsync(
            """
            SELECT "Id", "SeriesId", "ChapterId", "PageIndex", "Label", "CreatedAt"
            FROM "MangaBookmarks"
            WHERE "ProfileId" = @profileId AND "SeriesId" = @seriesId
            ORDER BY "CreatedAt" DESC;
            """,
            command =>
            {
                AddParameter(command, "@profileId", profileId);
                AddParameter(command, "@seriesId", seriesId.ToString());
            },
            reader => new MangaBookmarkItem(
                ReadGuid(reader, 0),
                ReadGuid(reader, 1),
                ReadGuid(reader, 2),
                reader.GetInt32(3),
                ReadNullableString(reader, 4),
                ReadDateTime(reader, 5)),
            cancellationToken);
    }

    public async Task<MangaBookmarkItem> AddBookmarkAsync(
        string profileId,
        MangaChapterRead chapter,
        int pageIndex,
        string? label,
        CancellationToken cancellationToken)
    {
        var item = new MangaBookmarkItem(
            Guid.NewGuid(),
            chapter.SeriesId,
            chapter.Id,
            Math.Clamp(pageIndex, 0, Math.Max(0, chapter.PageCount - 1)),
            NormalizeOptional(label, 120),
            DateTime.UtcNow);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "MangaBookmarks"
                ("Id", "ProfileId", "SeriesId", "ChapterId", "PageIndex", "Label", "CreatedAt")
            VALUES
                ({item.Id.ToString()}, {profileId}, {item.SeriesId.ToString()},
                 {item.ChapterId.ToString()}, {item.PageIndex}, {item.Label},
                 {item.CreatedAt.ToString("O")});
            """,
            cancellationToken);

        return item;
    }

    public async Task RemoveBookmarkAsync(
        string profileId,
        Guid bookmarkId,
        CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM "MangaBookmarks"
            WHERE "Id" = {bookmarkId.ToString()} AND "ProfileId" = {profileId};
            """,
            cancellationToken);
    }

    public async Task UpsertSeriesAsync(
        Guid id,
        string title,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow.ToString("O");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "MangaSeries"
                ("Id", "Title", "SourcePath", "Direction", "CreatedAt", "UpdatedAt")
            VALUES
                ({id.ToString()}, {title}, {sourcePath}, {"rtl"}, {now}, {now})
            ON CONFLICT("Id") DO UPDATE SET
                "Title" = excluded."Title",
                "SourcePath" = excluded."SourcePath",
                "UpdatedAt" = excluded."UpdatedAt";
            """,
            cancellationToken);
    }

    /// <summary>
    /// The series already stored at this exact source path (#563): lets a rescan of an
    /// unmoved/unrenamed series folder reuse its existing stable id instead of minting a new one,
    /// without requiring the caller to already know it.
    /// </summary>
    public async Task<Guid?> FindSeriesIdByPathAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        return await QueryScalarGuidAsync(
            """SELECT "Id" FROM "MangaSeries" WHERE "SourcePath" = @sourcePath LIMIT 1;""",
            command => AddParameter(command, "@sourcePath", sourcePath),
            cancellationToken);
    }

    public async Task UpsertChapterAsync(
        MangaChapterItem chapter,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow.ToString("O");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "MangaChapters"
                ("Id", "SeriesId", "Number", "VolumeNumber", "VolumeId", "Title", "SourcePath",
                 "SourceKind", "PageCount", "SourceUpdatedAt", "CreatedAt", "UpdatedAt")
            VALUES
                ({chapter.Id.ToString()}, {chapter.SeriesId.ToString()}, {chapter.Number},
                 {chapter.VolumeNumber}, {(chapter.VolumeId.HasValue ? chapter.VolumeId.Value.ToString() : null)},
                 {chapter.Title}, {sourcePath}, {chapter.SourceKind},
                 {chapter.PageCount}, {chapter.SourceUpdatedAt.ToString("O")}, {now}, {now})
            ON CONFLICT("Id") DO UPDATE SET
                "Number" = excluded."Number",
                "VolumeNumber" = excluded."VolumeNumber",
                "VolumeId" = excluded."VolumeId",
                "Title" = excluded."Title",
                "SourcePath" = excluded."SourcePath",
                "SourceKind" = excluded."SourceKind",
                "PageCount" = excluded."PageCount",
                "SourceUpdatedAt" = excluded."SourceUpdatedAt",
                "UpdatedAt" = excluded."UpdatedAt";
            """,
            cancellationToken);
    }

    /// <summary>
    /// A volume's own durable id for this (series, number) (#563), reusing an existing row's id
    /// when one already exists so the volume's identity - and anything keyed on it - survives a
    /// rescan even when every chapter file in it was renamed.
    /// </summary>
    public async Task<Guid> UpsertVolumeAsync(
        Guid seriesId,
        int number,
        string? title,
        CancellationToken cancellationToken)
    {
        var existingId = await QueryScalarGuidAsync(
            """
            SELECT "Id" FROM "MangaVolumes"
            WHERE "SeriesId" = @seriesId AND "Number" = @number
            LIMIT 1;
            """,
            command =>
            {
                AddParameter(command, "@seriesId", seriesId.ToString());
                AddParameter(command, "@number", number);
            },
            cancellationToken);

        var id = existingId ?? Guid.NewGuid();
        var now = DateTime.UtcNow.ToString("O");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "MangaVolumes"
                ("Id", "SeriesId", "Number", "Title", "CreatedAt", "UpdatedAt")
            VALUES
                ({id.ToString()}, {seriesId.ToString()}, {number}, {title}, {now}, {now})
            ON CONFLICT("Id") DO UPDATE SET
                "Title" = COALESCE(excluded."Title", "MangaVolumes"."Title"),
                "UpdatedAt" = excluded."UpdatedAt";
            """,
            cancellationToken);

        return id;
    }

    public async Task<IReadOnlyList<MangaVolumeItem>> GetVolumesAsync(
        Guid seriesId,
        CancellationToken cancellationToken)
    {
        return await QueryAsync(
            """
            SELECT "Id", "SeriesId", "Number", "Title", "CreatedAt", "UpdatedAt"
            FROM "MangaVolumes"
            WHERE "SeriesId" = @seriesId
            ORDER BY "Number";
            """,
            command => AddParameter(command, "@seriesId", seriesId.ToString()),
            reader => new MangaVolumeItem(
                ReadGuid(reader, 0),
                ReadGuid(reader, 1),
                reader.GetInt32(2),
                ReadNullableString(reader, 3),
                ReadDateTime(reader, 4),
                ReadDateTime(reader, 5)),
            cancellationToken);
    }

    public async Task ReplacePagesAsync(
        Guid chapterId,
        IReadOnlyList<MangaPageItem> pages,
        IReadOnlyList<string> sourceEntries,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM "MangaPages" WHERE "ChapterId" = {chapterId.ToString()};""",
            cancellationToken);

        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var sourceEntry = sourceEntries[i];
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "MangaPages"
                    ("Id", "ChapterId", "PageIndex", "CachedPath", "MimeType", "SourceEntry")
                VALUES
                    ({Guid.NewGuid().ToString()}, {chapterId.ToString()}, {page.PageIndex},
                     {page.CachedPath}, {page.MimeType}, {sourceEntry});
                """,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Every chapter's stable id, current source path and content/number identity (#563), used by
    /// <see cref="MangaImportService"/> to match a rescanned file against an existing chapter by
    /// path first, falling back to (number, volume) so a rename updates the row in place - instead
    /// of by recomputing an id from the path, which would treat a renamed file as a new chapter.
    /// </summary>
    public async Task<IReadOnlyList<(Guid Id, string SourcePath, double Number, int? VolumeNumber)>>
        GetChapterSourcesAsync(
            Guid seriesId,
            CancellationToken cancellationToken)
    {
        return await QueryAsync(
            """
            SELECT "Id", "SourcePath", "Number", "VolumeNumber"
            FROM "MangaChapters"
            WHERE "SeriesId" = @seriesId;
            """,
            command => AddParameter(command, "@seriesId", seriesId.ToString()),
            reader => (
                ReadGuid(reader, 0),
                reader.GetString(1),
                reader.GetDouble(2),
                reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3)),
            cancellationToken);
    }

    public async Task RemoveChapterAsync(
        Guid chapterId,
        CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM "MangaChapters" WHERE "Id" = {chapterId.ToString()};""",
            cancellationToken);
    }

    public async Task UpdateMetadataAsync(
        Guid seriesId,
        MangaAniListCandidate candidate,
        CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "MangaSeries"
            SET
                "MetadataProvider" = {"anilist"},
                "MetadataExternalId" = {candidate.ExternalId},
                "MetadataTitle" = {candidate.Title},
                "MetadataNativeTitle" = {candidate.NativeTitle},
                "MetadataDescription" = {candidate.Description},
                "CoverImageUrl" = {candidate.CoverImageUrl},
                "BannerImageUrl" = {candidate.BannerImageUrl},
                "MetadataStatus" = {candidate.Status},
                "UpdatedAt" = {DateTime.UtcNow.ToString("O")}
            WHERE "Id" = {seriesId.ToString()};
            """,
            cancellationToken);
    }

    public async Task SetDirectionAsync(
        Guid seriesId,
        string direction,
        CancellationToken cancellationToken)
    {
        direction = string.Equals(direction, "ltr", StringComparison.OrdinalIgnoreCase)
            ? "ltr"
            : "rtl";

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "MangaSeries"
            SET "Direction" = {direction}, "UpdatedAt" = {DateTime.UtcNow.ToString("O")}
            WHERE "Id" = {seriesId.ToString()};
            """,
            cancellationToken);
    }

    private async Task<List<T>> QueryAsync<T>(
        string sql,
        Action<DbCommand> bind,
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;

        if (close)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            bind(command);

            var result = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(map(reader));
            }

            return result;
        }
        finally
        {
            if (close)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    private async Task<Guid?> QueryScalarGuidAsync(
        string sql,
        Action<DbCommand> bind,
        CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(
            sql,
            bind,
            reader => reader.GetString(0),
            cancellationToken);

        return rows.Count == 0 ? null : Guid.Parse(rows[0]);
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static Guid ReadGuid(DbDataReader reader, int ordinal) =>
        Guid.Parse(reader.GetString(ordinal));

    private static Guid? ReadNullableGuid(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));

    private static string? ReadNullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static double? ReadNullableDouble(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static DateTime ReadDateTime(DbDataReader reader, int ordinal) =>
        DateTime.Parse(
            reader.GetString(ordinal),
            null,
            System.Globalization.DateTimeStyles.RoundtripKind);

    private static DateTime? ReadNullableDateTime(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadDateTime(reader, ordinal);

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }
}
