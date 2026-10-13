using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Media.V1
{
    [ServiceEndpoint("GET", "/api/v1/works/{workId:long}/platforms")]
    public sealed class ReadWorkPlatformsV1 : UserService<WorkParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<GamePlatformV1>>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The platforms a game can be requested for.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    platform."Id",
                    platform."Name",
                    platform."Abbreviation"
                FROM
                    "ProfileWorks" AS visible
                    JOIN "WorkPlatforms" AS listed ON listed."WorkId" = visible."WorkId"
                    JOIN "GamePlatforms" AS platform ON platform."Id" = listed."GamePlatformId"
                WHERE
                    visible."ProfileId" = @ProfileId
                    AND visible."WorkId" = @WorkId
                ORDER BY
                    platform."Name";
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return new BaseDataList<GamePlatformV1>([.. await Context.ReadSql.ReadListAsync<GamePlatformV1>(sql, CancellationToken)]);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/works/{workId:long}")]
    public sealed class ReadWorkV1 : UserService<WorkParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorkDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Persisted metadata of a Work the profile may open.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id",
                    work."MediaTypeId",
                    visible."MediaSubtypeId",
                    work."CanonicalTitle",
                    (entry."WorkId" IS NOT NULL) AS "IsWatchlisted",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    (
                        SELECT
                            fact."Value"
                        FROM
                            "WorkMetadataFacts" AS fact
                        WHERE
                            fact."WorkId" = work."Id"
                            AND fact."FactKey" = 'Overview'
                        ORDER BY
                            fact."IsManual" DESC
                        LIMIT 1
                    ) AS "Overview",
                    (
                        SELECT
                            fact."Value"
                        FROM
                            "WorkMetadataFacts" AS fact
                        WHERE
                            fact."WorkId" = work."Id"
                            AND fact."FactKey" = 'Poster'
                        ORDER BY
                            fact."IsManual" DESC
                        LIMIT 1
                    ) AS "PosterUrl",
                    (
                        SELECT
                            fact."Value"
                        FROM
                            "WorkMetadataFacts" AS fact
                        WHERE
                            fact."WorkId" = work."Id"
                            AND fact."FactKey" = 'Backdrop'
                        ORDER BY
                            fact."IsManual" DESC
                        LIMIT 1
                    ) AS "BackdropUrl",
                    (
                        SELECT
                            'https://app.plex.tv/desktop/#!/server/' || provider."ExternalId" || '/details?key=%2Flibrary%2Fmetadata%2F' || plex."Value"
                        FROM
                            "WorkExternalIdentities" AS plex
                            JOIN "Providers" AS provider ON provider."Key" = 'plex' AND provider."IsEnabled" AND provider."ExternalId" IS NOT NULL
                        WHERE
                            plex."WorkId" = work."Id"
                            AND plex."Namespace" = 'plex'
                    ) AS "PlexUrl",
                    ARRAY(
                        SELECT DISTINCT
                            platform."Name"
                        FROM
                            "MediaAssets" AS asset
                            JOIN "GameReleases" AS release ON release."WorkVersionId" = asset."WorkVersionId"
                            JOIN "GamePlatforms" AS platform ON platform."Id" = release."GamePlatformId"
                        WHERE
                            asset."WorkId" = work."Id"
                            AND EXISTS (SELECT 1 FROM "StoredFiles" AS file WHERE file."MediaAssetId" = asset."Id" AND file."MissingSince" IS NULL)
                        ORDER BY
                            platform."Name"
                    ) AS "Platforms"
                FROM
                    "ProfileWorks" AS visible
                    JOIN "Works" AS work ON work."Id" = visible."WorkId"
                    LEFT JOIN "WatchlistEntries" AS entry ON entry."WorkId" = work."Id" AND entry."ProfileId" = @ProfileId
                WHERE
                    visible."ProfileId" = @ProfileId
                    AND work."Id" = @WorkId;
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return await Context.ReadSql.ReadRequiredAsync<WorkDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/works/{workId:long}/availability")]
    public sealed class ReadWorkAvailabilityV1 : UserService<ReadWorkAvailabilityParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<WorkAvailabilityV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadWorkAvailabilityParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadWorkAvailabilityParametersV1 parameters, Type resultType) => new("profile.media.read");
        protected override PageRequest GetPageRequest(ReadWorkAvailabilityParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Stored inventory only; storage is never contacted.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    asset."Id",
                    asset."WorkVersionId",
                    asset."MediaAssetTypeId",
                    unit."WorkUnitTypeId",
                    unit."Number" AS "UnitNumber",
                    parent."Number" AS "ParentUnitNumber",
                    files."FileCount",
                    files."SizeBytes",
                    files."DurationMs",
                    files."IsAvailable",
                    tracks."Height",
                    tracks."AudioLanguages",
                    tracks."SubtitleLanguages",
                    progress."PositionMs" AS "ResumePositionMs",
                    coalesce(progress."IsCompleted", FALSE) AS "IsCompleted"
                FROM
                    "MediaAssets" AS asset
                    LEFT JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
                    LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                    LEFT JOIN "MediaProgress" AS progress ON progress."ProfileId" = @ProfileId
                        AND progress."WorkId" = asset."WorkId"
                        AND progress."WorkUnitId" IS NOT DISTINCT FROM asset."WorkUnitId"
                    CROSS JOIN LATERAL (
                        SELECT
                            count(*) AS "FileCount",
                            sum(file."SizeBytes")::bigint AS "SizeBytes",
                            max(file."DurationMs") AS "DurationMs",
                            coalesce(bool_or(file."MissingSince" IS NULL) FILTER (WHERE file."RelativePath" !~* '\.(srt|ass|ssa|vtt|sub|idx|sup)$'), FALSE) AS "IsAvailable"
                        FROM
                            "StoredFiles" AS file
                        WHERE
                            file."MediaAssetId" = asset."Id"
                    ) AS files
                    CROSS JOIN LATERAL (
                        SELECT
                            max(track."Height") FILTER (WHERE track."MediaTrackTypeId" = 1) AS "Height",
                            coalesce(array_agg(DISTINCT track."Language") FILTER (WHERE track."MediaTrackTypeId" = 2 AND track."Language" IS NOT NULL), '{}') AS "AudioLanguages",
                            coalesce(array_agg(DISTINCT track."Language") FILTER (WHERE track."MediaTrackTypeId" = 3 AND track."Language" IS NOT NULL), '{}') AS "SubtitleLanguages"
                        FROM
                            "StoredFiles" AS file
                            JOIN "MediaTracks" AS track ON track."StoredFileId" = file."Id"
                        WHERE
                            file."MediaAssetId" = asset."Id"
                    ) AS tracks
                WHERE
                    asset."WorkId" = @WorkId
                    AND files."FileCount" > 0
                    AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."ProfileId" = @ProfileId AND visible."WorkId" = asset."WorkId")
                ORDER BY
                    parent."Number" NULLS FIRST,
                    unit."Number" NULLS FIRST,
                    asset."WorkVersionId",
                    asset."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new WorkParametersV1(Parameters.WorkId));
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<WorkAvailabilityV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/watchlist")]
    public sealed class ReadWatchlistV1 : UserService<ReadWatchlistParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<WorkDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadWatchlistParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadWatchlistParametersV1 parameters, Type resultType) => new("profile.media.read");
        protected override PageRequest GetPageRequest(ReadWatchlistParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // One profile-scoped page.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id",
                    work."MediaTypeId",
                    visible."MediaSubtypeId",
                    work."CanonicalTitle",
                    TRUE AS "IsWatchlisted",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    NULL::text AS "Overview",
                    (
                        SELECT
                            fact."Value"
                        FROM
                            "WorkMetadataFacts" AS fact
                        WHERE
                            fact."WorkId" = work."Id"
                            AND fact."FactKey" = 'Poster'
                        ORDER BY
                            fact."IsManual" DESC
                        LIMIT 1
                    ) AS "PosterUrl",
                    NULL::text AS "BackdropUrl",
                    NULL::text AS "PlexUrl",
                    ARRAY[]::text[] AS "Platforms"
                FROM
                    "WatchlistEntries" AS entry
                    JOIN "ProfileWorks" AS visible ON visible."ProfileId" = entry."ProfileId" AND visible."WorkId" = entry."WorkId"
                    JOIN "Works" AS work ON work."Id" = entry."WorkId"
                WHERE
                    entry."ProfileId" = @ProfileId
                    AND NOT visible."IsHidden"
                ORDER BY
                    work."CanonicalTitle",
                    work."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<WorkDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/watchlist/{workId:long}")]
    public sealed class UpdateWatchlistV1 : UserService<WorkParametersV1, UpdateWatchlistDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("profile.watchlist.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.UpdateWatchlistAsync(new WatchlistData(Auth.ProfileId!.Value, Parameters.WorkId), Data.IsWatchlisted, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
