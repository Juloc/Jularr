using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Playback
{
    // The one owner of chapters and skip markers: what a scan finds in a file, and what an administrator sets or copies by hand.
    internal static class MediaMarkerLogic
    {
        // A file's chapters, and the markers its chapter titles and its sidecar file give; manual markers stay.
        public static async Task SaveMediaFileMarkersAsync(this LogicContext ctx, StoredFileProbeData data, CancellationToken ct)
        {
            var chapters = Read<MediaChapterRow>(data.Chapters).Where(chapter => chapter.StartMs >= 0 && chapter.EndMs >= chapter.StartMs).OrderBy(chapter => chapter.ChapterIndex)
                .Take(MediaMarkers.MaximumChapters).Select((chapter, index) => chapter with { ChapterIndex = index }).ToList();
            var markers = MediaMarkers.FromChapters(chapters, data.DurationMs).ToList();
            foreach (var marker in Read<MediaMarkerRow>(data.SidecarMarkers))
            {
                if (markers.All(known => known.MediaMarkerKindTypeId != marker.MediaMarkerKindTypeId)
                    && MediaMarkers.Fit(marker.MediaMarkerKindTypeId, MediaMarkerSource.Sidecar, marker.StartMs, marker.EndMs, 1m, data.DurationMs) is { } fitted)
                {
                    markers.Add(fitted);
                }
            }

            var rows = new MediaFileMarkerRowsData(data.Id, JsonSerializer.Serialize(chapters), JsonSerializer.Serialize(markers));

            // Chapters
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "MediaChapters" AS chapter
                WHERE
                    chapter."StoredFileId" = @StoredFileId;
                """;
            sql.Parameters.Add(rows);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "MediaChapters" ("StoredFileId", "ChapterIndex", "StartMs", "EndMs", "Title")
                SELECT
                    @StoredFileId,
                    chapter."ChapterIndex",
                    chapter."StartMs",
                    chapter."EndMs",
                    NULLIF(btrim(chapter."Title"), '')
                FROM
                    jsonb_to_recordset(@Chapters::jsonb) AS chapter ("ChapterIndex" integer, "StartMs" bigint, "EndMs" bigint, "Title" text);
                """;
            sql.Parameters.Add(rows);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // Markers found in the file or beside it
            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "MediaMarkers" AS marker
                WHERE
                    marker."StoredFileId" = @StoredFileId
                    AND marker."MediaMarkerSourceTypeId" IN (2, 3);
                """;
            sql.Parameters.Add(rows);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "MediaMarkers" ("StoredFileId", "MediaMarkerKindTypeId", "MediaMarkerSourceTypeId", "StartMs", "EndMs", "Confidence")
                SELECT
                    @StoredFileId,
                    marker."MediaMarkerKindTypeId",
                    marker."MediaMarkerSourceTypeId",
                    marker."StartMs",
                    marker."EndMs",
                    marker."Confidence"
                FROM
                    jsonb_to_recordset(@Markers::jsonb) AS marker ("MediaMarkerKindTypeId" smallint, "MediaMarkerSourceTypeId" smallint, "StartMs" bigint, "EndMs" bigint, "Confidence" numeric);
                """;
            sql.Parameters.Add(rows);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // A marker set by hand goes onto every available file of the episode, replacing the one set before.
        public static async Task SaveManualMarkerAsync(this LogicContext ctx, MediaMarkerSaveData data, CancellationToken ct)
        {
            // The end is named as the field the person edited.
            if (data.EndMs <= data.StartMs)
            {
                throw new DataValidationException("EndSeconds", "out_of_range");
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "MediaMarkers" ("StoredFileId", "MediaMarkerKindTypeId", "MediaMarkerSourceTypeId", "StartMs", "EndMs", "Confidence")
                SELECT
                    file."Id",
                    @MediaMarkerKindTypeId,
                    1,
                    @StartMs,
                    least(@EndMs, coalesce(file."DurationMs", @EndMs)),
                    1
                FROM
                    "WorkUnits" AS unit
                    JOIN "MediaAssets" AS asset ON asset."WorkUnitId" = unit."Id"
                    JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id"
                WHERE
                    unit."Id" = @WorkUnitId
                    AND unit."WorkId" = @WorkId
                    AND file."MissingSince" IS NULL
                    AND NOT file."IsSubtitle"
                    AND (file."DurationMs" IS NULL OR file."DurationMs" > @StartMs)
                ON CONFLICT ("StoredFileId", "MediaMarkerKindTypeId", "MediaMarkerSourceTypeId") DO UPDATE
                SET
                    "StartMs" = EXCLUDED."StartMs",
                    "EndMs" = EXCLUDED."EndMs",
                    "Confidence" = 1,
                    "UpdatedAt" = now();
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 0)
            {
                throw new KeyNotFoundException("No file of the episode fits the marker.");
            }
        }

        // Only the manual marker goes; what the file or a sidecar says shows again.
        public static async Task DeleteManualMarkerAsync(this LogicContext ctx, MediaMarkerKeyData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "MediaMarkers" AS marker
                USING
                    "MediaAssets" AS asset,
                    "StoredFiles" AS file,
                    "WorkUnits" AS unit
                WHERE
                    unit."Id" = @WorkUnitId
                    AND unit."WorkId" = @WorkId
                    AND asset."WorkUnitId" = unit."Id"
                    AND file."MediaAssetId" = asset."Id"
                    AND marker."StoredFileId" = file."Id"
                    AND marker."MediaMarkerKindTypeId" = @MediaMarkerKindTypeId
                    AND marker."MediaMarkerSourceTypeId" = 1;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 0)
            {
                throw new KeyNotFoundException("The episode has no marker of this kind set by hand.");
            }
        }

        // The marker the episode's first file shows goes to the season's other episodes, shifted; each takes it as a manual marker, fitted to its own length.
        public static async Task<int> CopyMarkerToSeasonAsync(this LogicContext ctx, MediaMarkerCopyData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH source AS (
                    SELECT
                        marker."StartMs" + @OffsetMs AS "StartMs",
                        marker."EndMs" + @OffsetMs AS "EndMs"
                    FROM
                        "WorkUnits" AS unit
                        JOIN LATERAL (
                            SELECT
                                file."Id"
                            FROM
                                "MediaAssets" AS asset
                                JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id"
                            WHERE
                                asset."WorkUnitId" = unit."Id"
                                AND file."MissingSince" IS NULL
                                AND NOT file."IsSubtitle"
                            ORDER BY
                                asset."Id",
                                file."Id"
                            LIMIT 1
                        ) AS first ON TRUE
                        JOIN "MediaMarkers" AS marker ON marker."StoredFileId" = first."Id" AND marker."MediaMarkerKindTypeId" = @MediaMarkerKindTypeId
                    WHERE
                        unit."Id" = @WorkUnitId
                        AND unit."WorkId" = @WorkId
                    ORDER BY
                        marker."MediaMarkerSourceTypeId"
                    LIMIT 1
                )
                INSERT INTO
                    "MediaMarkers" ("StoredFileId", "MediaMarkerKindTypeId", "MediaMarkerSourceTypeId", "StartMs", "EndMs", "Confidence")
                SELECT
                    file."Id",
                    @MediaMarkerKindTypeId,
                    1,
                    greatest(source."StartMs", 0),
                    least(source."EndMs", coalesce(file."DurationMs", source."EndMs")),
                    1
                FROM
                    source,
                    "WorkUnits" AS me
                    JOIN "WorkUnits" AS other ON other."WorkId" = me."WorkId"
                        AND other."WorkUnitTypeId" = me."WorkUnitTypeId"
                        AND other."ParentWorkUnitId" IS NOT DISTINCT FROM me."ParentWorkUnitId"
                        AND other."Id" <> me."Id"
                    JOIN "MediaAssets" AS asset ON asset."WorkUnitId" = other."Id"
                    JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id"
                WHERE
                    me."Id" = @WorkUnitId
                    AND me."WorkId" = @WorkId
                    AND file."MissingSince" IS NULL
                    AND NOT file."IsSubtitle"
                    AND greatest(source."StartMs", 0) < least(source."EndMs", coalesce(file."DurationMs", source."EndMs"))
                ON CONFLICT ("StoredFileId", "MediaMarkerKindTypeId", "MediaMarkerSourceTypeId") DO UPDATE
                SET
                    "StartMs" = EXCLUDED."StartMs",
                    "EndMs" = EXCLUDED."EndMs",
                    "Confidence" = 1,
                    "UpdatedAt" = now();
                """;
            sql.Parameters.Add(data);
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        private static IReadOnlyList<T> Read<T>(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<T[]>(json) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }
}
