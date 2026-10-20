using System.Text.Json;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Library
{
    // An administrator's override of a file's quality and audio language; a scan keeps it while the file is unchanged.
    internal static class FileFactsLogic
    {
        public static async Task<int> UpdateStoredFileFactsAsync(this LogicContext ctx, UpdateLibraryFileFactsDataV1 data, CancellationToken ct)
        {
            if (data.QualityKey is null && data.AudioLanguage is null && data.TrackLanguages is null)
            {
                throw new DataValidationException(nameof(data.QualityKey), "required");
            }

            if (data.AudioLanguage is { } language && !FileLanguages.Known.Any(known => known.Three == language))
            {
                throw new DataValidationException(nameof(data.AudioLanguage), "invalid_value");
            }

            // A track is told apart from its fellows only in one file.
            if (data.TrackLanguages is { } rows)
            {
                if (data.StoredFileIds.Length != 1)
                {
                    throw new DataValidationException(nameof(data.TrackLanguages), "invalid_value");
                }

                try
                {
                    var tracks = JsonSerializer.Deserialize<TrackLanguageRow[]>(rows) ?? throw new JsonException();
                    if (tracks.Any(track => !string.IsNullOrEmpty(track.Language) && !FileLanguages.Known.Any(known => known.Three == track.Language)))
                    {
                        throw new DataValidationException(nameof(data.TrackLanguages), "invalid_value");
                    }
                }
                catch (JsonException)
                {
                    throw new DataValidationException(nameof(data.TrackLanguages), "invalid_value");
                }
            }

            // Only identified media files have a type whose qualities can be chosen.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."Id",
                    work."MediaTypeId",
                    work."MediaSubtypeId"
                FROM
                    "StoredFiles" AS file
                    JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                    JOIN "Works" AS work ON work."Id" = asset."WorkId"
                WHERE
                    file."Id" = ANY (@StoredFileIds)
                    AND NOT file."IsSubtitle";
                """;
            sql.Parameters.Add(data);
            var targets = await ctx.ReadSql.ReadListAsync<FileFactTargetData>(sql, ct);
            if (data.QualityKey is { } quality && targets.Any(target => !ProfileQualities.For(target.MediaTypeId, target.MediaSubtypeId).Contains(quality)))
            {
                throw new DataValidationException(nameof(data.QualityKey), "invalid_value");
            }

            if (targets.Count == 0)
            {
                return 0;
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                WITH quality AS (
                    UPDATE
                        "StoredFiles" AS file
                    SET
                        "QualityKey" = @QualityKey
                    WHERE
                        @QualityKey IS NOT NULL
                        AND file."Id" = ANY (@StoredFileIds)
                        AND NOT file."IsSubtitle"
                        AND file."MediaAssetId" IS NOT NULL
                )
                UPDATE
                    "MediaTracks" AS track
                SET
                    "Language" = @AudioLanguage
                WHERE
                    @AudioLanguage IS NOT NULL
                    AND track."StoredFileId" = ANY (@StoredFileIds)
                    AND track."MediaTrackTypeId" = 2
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "StoredFiles" AS file
                        WHERE
                            file."Id" = track."StoredFileId"
                            AND file."MediaAssetId" IS NOT NULL
                    );
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            if (data.TrackLanguages is not null)
            {
                sql = new SqlStatement();
                sql.Statement =
                    """
                    UPDATE
                        "MediaTracks" AS track
                    SET
                        "Language" = NULLIF(item."Language", '')
                    FROM
                        jsonb_to_recordset(@TrackLanguages::jsonb) AS item ("StreamIndex" integer, "Language" text)
                    WHERE
                        track."StoredFileId" = ANY (@StoredFileIds)
                        AND track."StreamIndex" = item."StreamIndex"
                        AND track."MediaTrackTypeId" = 2;
                    """;
                sql.Parameters.Add(data);
                await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            }

            return targets.Count;
        }

        private sealed record TrackLanguageRow(int StreamIndex, string? Language);
    }

    internal sealed class FileFactTargetData : BaseData
    {
        public FileFactTargetData(long id, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }
    }
}
