using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Playback.V1
{
    // The skippable stretches of the file the player plays: per kind the one the most trusted source knows.
    [ServiceEndpoint("GET", "/api/v1/playback/assets/{mediaAssetId:long}/markers")]
    public sealed class ReadPlaybackMarkersV1 : UserService<MediaAssetParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<PlaybackMarkerV1>>()];
        protected override ModuleRequirement GetInstanceModules(MediaAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(MediaAssetParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    marker."MediaMarkerKindTypeId",
                    marker."MediaMarkerSourceTypeId",
                    marker."StartMs",
                    marker."EndMs",
                    played."DurationMs"
                FROM
                    "MediaAssets" AS asset
                    JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                    JOIN LATERAL (
                        SELECT
                            file."Id",
                            file."DurationMs"
                        FROM
                            "StoredFiles" AS file
                        WHERE
                            file."MediaAssetId" = asset."Id"
                            AND file."MissingSince" IS NULL
                            AND NOT file."IsSubtitle"
                        ORDER BY
                            file."Id"
                        LIMIT 1
                    ) AS played ON TRUE
                    JOIN "MediaMarkers" AS marker ON marker."StoredFileId" = played."Id"
                WHERE
                    asset."Id" = @MediaAssetId
                ORDER BY
                    marker."MediaMarkerKindTypeId",
                    marker."MediaMarkerSourceTypeId";
                """;
            sql.Parameters.Add(new PlaybackSourceParameters(Parameters.MediaAssetId, Auth.ProfileId!.Value));
            var rows = await Context.ReadSql.ReadListAsync<PlaybackMarkerRowData>(sql, CancellationToken);
            // One marker per kind, the most trusted source first; a marker read from chapters that cannot be right is left out (it may be stored from before that rule).
            var markers = rows.Where(row => MediaMarkers.IsUsable(row.MediaMarkerKindTypeId, row.MediaMarkerSourceTypeId, row.StartMs, row.EndMs, row.DurationMs))
                .OrderBy(row => row.MediaMarkerSourceTypeId).GroupBy(row => row.MediaMarkerKindTypeId).Select(group => group.First())
                .Select(row => new PlaybackMarkerV1(row.MediaMarkerKindTypeId, row.StartMs, row.EndMs));
            return new BaseDataList<PlaybackMarkerV1>([.. markers.OrderBy(marker => marker.StartMs)]);
        }
    }

    // The chapters of the played file for the chapter menu.
    [ServiceEndpoint("GET", "/api/v1/playback/assets/{mediaAssetId:long}/chapters")]
    public sealed class ReadPlaybackChaptersV1 : UserService<MediaAssetParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<PlaybackChapterV1>>()];
        protected override ModuleRequirement GetInstanceModules(MediaAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(MediaAssetParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    chapter."ChapterIndex",
                    chapter."StartMs",
                    chapter."EndMs",
                    chapter."Title"
                FROM
                    "MediaAssets" AS asset
                    JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                    JOIN LATERAL (
                        SELECT
                            file."Id"
                        FROM
                            "StoredFiles" AS file
                        WHERE
                            file."MediaAssetId" = asset."Id"
                            AND file."MissingSince" IS NULL
                            AND NOT file."IsSubtitle"
                        ORDER BY
                            file."Id"
                        LIMIT 1
                    ) AS played ON TRUE
                    JOIN "MediaChapters" AS chapter ON chapter."StoredFileId" = played."Id"
                WHERE
                    asset."Id" = @MediaAssetId
                ORDER BY
                    chapter."ChapterIndex";
                """;
            sql.Parameters.Add(new PlaybackSourceParameters(Parameters.MediaAssetId, Auth.ProfileId!.Value));
            var chapters = await Context.ReadSql.ReadListAsync<PlaybackChapterV1>(sql, CancellationToken);
            // A file whose only chapter is a stretch over the whole episode named "Credits" has no chapters worth listing.
            var usable = MediaMarkers.UsableChapters([.. chapters.Select(chapter => new MediaChapterRow(chapter.ChapterIndex, chapter.StartMs, chapter.EndMs, chapter.Title))]);
            return new BaseDataList<PlaybackChapterV1>([.. chapters.Where(chapter => usable.Any(item => item.ChapterIndex == chapter.ChapterIndex))]);
        }
    }
}
