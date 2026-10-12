using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Playback
{
    internal sealed record PlaybackConversion(PlaybackModeType PlaybackModeTypeId, int? VideoStreamIndex, int? AudioStreamIndex, bool CopyVideo, bool CopyAudio);

    internal static class PlaybackLogic
    {
        public static bool CanPlayDirectly(string container, IReadOnlyList<PlaybackTrackData> tracks, IReadOnlySet<string> containers, IReadOnlySet<string> videoCodecs,
            IReadOnlySet<string> audioCodecs)
        {
            // Unanalysed files are tried directly when the container plays.
            if (!containers.Contains(container))
            {
                return false;
            }

            var video = Select(tracks, MediaTrackType.Video);
            var audio = Select(tracks, MediaTrackType.Audio);
            return (video is null || video.Codec is { } videoCodec && videoCodecs.Contains(videoCodec))
                && (audio is null || audio.Codec is { } audioCodec && audioCodecs.Contains(audioCodec));
        }

        public static PlaybackConversion? PlanConversion(IReadOnlyList<PlaybackTrackData> tracks, IReadOnlySet<string> containers, IReadOnlySet<string> videoCodecs,
            IReadOnlySet<string> audioCodecs)
        {
            // HLS delivers H.264 and AAC; matching streams are copied.
            var video = Select(tracks, MediaTrackType.Video);
            var audio = Select(tracks, MediaTrackType.Audio);
            if (tracks.Count == 0 || !containers.Contains("hls") || video is not null && !videoCodecs.Contains("h264") || audio is not null && !audioCodecs.Contains("aac"))
            {
                return null;
            }

            var copyVideo = video is null || video.Codec == "h264";
            var copyAudio = audio is null || audio.Codec is "aac" or "mp3" && audioCodecs.Contains(audio.Codec);
            var mode = copyVideo && copyAudio ? PlaybackModeType.Remux : PlaybackModeType.Transcode;
            return new PlaybackConversion(mode, video?.StreamIndex, audio?.StreamIndex, copyVideo, copyAudio);
        }

        public static async Task<Guid> CreatePlaybackSessionAsync(this LogicContext ctx, PlaybackSessionCreationData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "PlaybackSessions" ("PublicId", "ProfileId", "MediaAssetId", "StoredFileId", "PlaybackModeTypeId", "PositionMs")
                VALUES
                    (gen_random_uuid(), @ProfileId, @MediaAssetId, @StoredFileId, @PlaybackModeTypeId, @PositionMs)
                RETURNING
                    "PublicId";
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadRequiredAsync<PlaybackSessionIdData>(sql, ct)).PublicId;
        }

        public static async Task UpdatePlaybackAsync(this LogicContext ctx, PlaybackUpdateData data, CancellationToken ct)
        {
            // Only the owning profile's open session moves.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "PlaybackSessions" AS session
                SET
                    "PositionMs" = @PositionMs,
                    "LastSeenAt" = now(),
                    "EndedAt" = CASE WHEN @IsEnded THEN now() END
                WHERE
                    session."PublicId" = @PlaybackSessionId
                    AND session."ProfileId" = @ProfileId
                    AND session."EndedAt" IS NULL
                RETURNING
                    session."MediaAssetId" AS "Id";
                """;
            sql.Parameters.Add(data);
            var asset = await ctx.ReadOptionalAsync<MediaAssetIdData>(sql, ct) ?? throw new KeyNotFoundException("The playback session is not active.");

            // Completion stays; a finished item restarts at zero.
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "PositionMs", "DurationMs", "IsCompleted", "CompletedAt")
                SELECT
                    @ProfileId,
                    asset."WorkId",
                    asset."WorkUnitId",
                    1,
                    CASE WHEN @IsCompleted THEN 0 ELSE @PositionMs END,
                    @DurationMs,
                    @IsCompleted,
                    CASE WHEN @IsCompleted THEN now() END
                FROM
                    "MediaAssets" AS asset
                WHERE
                    asset."Id" = @Id
                ON CONFLICT ("ProfileId", "WorkId", "WorkUnitId") DO UPDATE
                SET
                    "PositionMs" = EXCLUDED."PositionMs",
                    "DurationMs" = COALESCE(EXCLUDED."DurationMs", "MediaProgress"."DurationMs"),
                    "IsCompleted" = "MediaProgress"."IsCompleted" OR EXCLUDED."IsCompleted",
                    "CompletedAt" = COALESCE("MediaProgress"."CompletedAt", EXCLUDED."CompletedAt"),
                    "UpdatedAt" = now();
                """;
            sql.Parameters.Add(data);
            sql.Parameters.Add(asset);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        private static PlaybackTrackData? Select(IReadOnlyList<PlaybackTrackData> tracks, MediaTrackType type) =>
            tracks.FirstOrDefault(track => track.MediaTrackTypeId == type && track.IsDefault) ?? tracks.FirstOrDefault(track => track.MediaTrackTypeId == type);
    }
}
