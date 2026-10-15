using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Playback
{
    internal static class PlaybackLogic
    {
        public static bool CanPlayDirectly(string container, IReadOnlyList<PlaybackTrackData> tracks, IReadOnlySet<string> containers, IReadOnlySet<string> videoCodecs,
            IReadOnlySet<string> audioCodecs, int? audioStreamIndex = null)
        {
            // Unanalysed files are tried directly when the container plays; browsers play the file's default audio only.
            var audio = Select(tracks, MediaTrackType.Audio);
            if (!containers.Contains(container) || audioStreamIndex is { } chosen && audio?.StreamIndex != chosen)
            {
                return false;
            }

            var video = Select(tracks, MediaTrackType.Video);
            return (video is null || video.Codec is { } videoCodec && videoCodecs.Contains(videoCodec))
                && (audio is null || audio.Codec is { } audioCodec && audioCodecs.Contains(audioCodec));
        }

        // Music is converted for a device that asks for a smaller size when the file is clearly larger; video and unknown sizes play as they are.
        public static int? AudioKilobits(AudioQuality quality, IReadOnlyList<PlaybackTrackData> tracks, long? bitRate)
        {
            var kilobits = quality switch
            {
                AudioQuality.High => 256,
                AudioQuality.Small => 128,
                _ => (int?)null
            };
            return kilobits is { } target && Select(tracks, MediaTrackType.Video) is null && bitRate > target * 1200L ? target : null;
        }

        public static PlaybackConversion? PlanConversion(IReadOnlyList<PlaybackTrackData> tracks, IReadOnlySet<string> containers, IReadOnlySet<string> videoCodecs,
            IReadOnlySet<string> audioCodecs, int? audioStreamIndex = null, int? audioKilobits = null)
        {
            // HLS delivers H.264 and AAC; matching streams are copied, a chosen audio track replaces the default.
            var video = Select(tracks, MediaTrackType.Video);
            var audio = audioStreamIndex is { } chosen ? tracks.FirstOrDefault(track => track.StreamIndex == chosen) : Select(tracks, MediaTrackType.Audio);
            if (tracks.Count == 0 || !containers.Contains("hls") || video is not null && !videoCodecs.Contains("h264") || audio is not null && !audioCodecs.Contains("aac"))
            {
                return null;
            }

            var copyVideo = video is null || video.Codec == "h264";
            var copyAudio = audio is null || audioKilobits is null && audio.Codec is "aac" or "mp3" && audioCodecs.Contains(audio.Codec);
            var mode = copyVideo && copyAudio ? PlaybackModeType.Remux : PlaybackModeType.Transcode;
            return new PlaybackConversion(mode, video?.StreamIndex, audio?.StreamIndex, copyVideo, copyAudio) { AudioKilobits = copyAudio ? null : audioKilobits ?? 192 };
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
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "MediaAssets" AS asset
                            JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = session."ProfileId"
                        WHERE
                            asset."Id" = session."MediaAssetId"
                    )
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
