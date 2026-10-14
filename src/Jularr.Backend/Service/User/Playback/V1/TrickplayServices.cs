using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.User.Playback.V1
{
    [ServiceEndpoint("GET", "/api/v1/playback/assets/{mediaAssetId:long}/trickplay")]
    public sealed class ReadTrickplayV1 : UserService<MediaAssetParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<TrickplayV1>()];
        protected override ModuleRequirement GetInstanceModules(MediaAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Playback", "Trickplay");
        protected override ServicePermission GetPermission(MediaAssetParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The played file's thumbnails; their height follows the video's shape.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."TrickplayFrames",
                    video."Width",
                    video."Height"
                FROM
                    (
                        SELECT
                            candidate."Id",
                            candidate."TrickplayFrames"
                        FROM
                            "MediaAssets" AS asset
                            JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                            JOIN "StoredFiles" AS candidate ON candidate."MediaAssetId" = asset."Id"
                        WHERE
                            asset."Id" = @MediaAssetId
                            AND candidate."MissingSince" IS NULL
                            AND candidate."RelativePath" !~* '\.(srt|ass|ssa|vtt|sub|idx|sup)$'
                        ORDER BY
                            candidate."Id"
                        LIMIT 1
                    ) AS file
                    JOIN "MediaTracks" AS video ON video."StoredFileId" = file."Id" AND video."MediaTrackTypeId" = 1 AND video."Width" > 0 AND video."Height" > 0
                WHERE
                    file."TrickplayFrames" IS NOT NULL
                ORDER BY
                    video."StreamIndex"
                LIMIT 1;
                """;
            sql.Parameters.Add(new PlaybackSourceParameters(Parameters.MediaAssetId, Auth.ProfileId!.Value));
            var file = await Context.ReadSql.ReadOptionalAsync<TrickplayFileData>(sql, CancellationToken) ?? throw new KeyNotFoundException("No seek thumbnails exist.");
            return new TrickplayV1(Trickplay.IntervalSeconds, file.TrickplayFrames, Trickplay.Columns, Trickplay.Rows, Trickplay.TileWidth, Trickplay.TileHeight(file.Width, file.Height));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/playback/assets/{mediaAssetId:long}/trickplay/{sheet:int}")]
    public sealed class ReadTrickplaySheetV1 : UserService<TrickplaySheetParametersV1>
    {
        private readonly MediaOptions _options;

        internal ReadTrickplaySheetV1(IOptions<MediaOptions> options)
        {
            _options = options.Value;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PlaybackStreamV1>()];
        protected override ModuleRequirement GetInstanceModules(TrickplaySheetParametersV1 parameters) => ModuleRequirement.All("Library", "Playback", "Trickplay");
        protected override ServicePermission GetPermission(TrickplaySheetParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The sheet of the played file, from the thumbnail folder only.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    candidate."Id",
                    candidate."TrickplayFrames"
                FROM
                    "MediaAssets" AS asset
                    JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                    JOIN "StoredFiles" AS candidate ON candidate."MediaAssetId" = asset."Id"
                WHERE
                    asset."Id" = @MediaAssetId
                    AND candidate."MissingSince" IS NULL
                    AND candidate."RelativePath" !~* '\.(srt|ass|ssa|vtt|sub|idx|sup)$'
                ORDER BY
                    candidate."Id"
                LIMIT 1;
                """;
            sql.Parameters.Add(new PlaybackSourceParameters(Parameters.MediaAssetId, Auth.ProfileId!.Value));
            var file = await Context.ReadSql.ReadOptionalAsync<TrickplaySheetFileData>(sql, CancellationToken);
            var path = file?.TrickplayFrames is not null && _options.TrickplayPath is { } root ? Trickplay.Sheet(Trickplay.Folder(root, file.Id), Parameters.Sheet) : null;
            return path is not null && File.Exists(path) ? new PlaybackStreamV1(path, "image/jpeg") : throw new KeyNotFoundException("The sheet was not found.");
        }
    }
}
