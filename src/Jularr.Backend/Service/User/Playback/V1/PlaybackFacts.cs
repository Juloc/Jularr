using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Playback.V1
{
    // What a playback is made of: the file as probed and what the server does with it, for the Info pane of the player.
    [ServiceEndpoint("GET", "/api/v1/playback/sessions/{playbackSessionId:guid}/facts")]
    public sealed class ReadPlaybackFactsV1 : UserService<PlaybackSessionParametersV1>
    {
        private readonly PlaybackTranscodes _transcodes;

        internal ReadPlaybackFactsV1(PlaybackTranscodes transcodes)
        {
            _transcodes = transcodes;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PlaybackFactsV1>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackSessionParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(PlaybackSessionParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Only the owning profile's session tells.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."Id" AS "StoredFileId",
                    file."Container",
                    file."BitRate",
                    session."PlaybackModeTypeId"
                FROM
                    "PlaybackSessions" AS session
                    JOIN "StoredFiles" AS file ON file."Id" = session."StoredFileId"
                WHERE
                    session."PublicId" = @PlaybackSessionId
                    AND session."ProfileId" = @ProfileId;
                """;
            sql.Parameters.Add(new PlaybackSessionLookupData(Parameters.PlaybackSessionId, Auth.ProfileId!.Value));
            var file = await Context.ReadSql.ReadRequiredAsync<PlaybackFactsFileData>(sql, CancellationToken);

            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    track."StreamIndex",
                    track."MediaTrackTypeId",
                    track."IsDefault",
                    track."Codec",
                    track."Language",
                    track."Width",
                    track."Height",
                    track."Channels",
                    track."BitDepth",
                    track."DynamicRange"
                FROM
                    "MediaTracks" AS track
                WHERE
                    track."StoredFileId" = @Id
                ORDER BY
                    track."StreamIndex";
                """;
            sql.Parameters.Add(new StoredFileIdData(file.StoredFileId));
            var tracks = await Context.ReadSql.ReadListAsync<PlaybackFactsTrackData>(sql, CancellationToken);

            // A conversion names its streams; a direct playback shows the default ones.
            var conversion = _transcodes.Find(Parameters.PlaybackSessionId, Auth.ProfileId!.Value);
            var video = tracks.FirstOrDefault(track => track.MediaTrackTypeId == MediaTrackType.Video && (conversion?.VideoStreamIndex is not { } wantedVideo || track.StreamIndex == wantedVideo));
            var audio = tracks.FirstOrDefault(track => track.MediaTrackTypeId == MediaTrackType.Audio && (conversion?.AudioStreamIndex is { } wantedAudio ? track.StreamIndex == wantedAudio : track.IsDefault))
                ?? tracks.FirstOrDefault(track => track.MediaTrackTypeId == MediaTrackType.Audio);

            // The video encoder FFmpeg is given, such as libx264 or a hardware one.
            string? encoder = null;
            if (conversion is { CopyVideo: false, Encoding: { } encoding })
            {
                var names = encoding.VideoArguments.ToList();
                var at = names.FindIndex(argument => argument is "-c:v" or "-vcodec");
                encoder = at >= 0 && at + 1 < names.Count ? names[at + 1] : null;
            }

            return new PlaybackFactsV1(file.PlaybackModeTypeId, file.Container, file.BitRate, video?.Codec, video?.Width, video?.Height, video?.BitDepth, video?.DynamicRange,
                audio?.Codec, audio?.Channels, audio?.Language, conversion?.CopyVideo, conversion?.CopyAudio, conversion?.MaxHeight, conversion?.VideoKilobits, conversion?.AudioKilobits, encoder);
        }
    }
}
