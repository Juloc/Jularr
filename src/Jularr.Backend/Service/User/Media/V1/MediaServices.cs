using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Data;
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
                    facts."Overview",
                    facts."PosterUrl",
                    facts."BackdropUrl",
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
                    ) AS "Platforms",
                    coalesce((
                        SELECT
                            array_remove(CASE WHEN fact."FactKey" = 'Artist' THEN ARRAY[btrim(fact."Value")] ELSE regexp_split_to_array(btrim(fact."Value"), '\s*[,;]\s*') END, '')
                        FROM
                            "WorkMetadataFacts" AS fact
                        WHERE
                            fact."WorkId" = work."Id"
                            AND fact."FactKey" IN ('Author', 'Artist')
                        ORDER BY
                            fact."IsManual" DESC,
                            fact."Id"
                        LIMIT 1
                    ), ARRAY[]::text[]) AS "Creators",
                    franchise."Id" AS "FranchiseId",
                    franchise."Name" AS "FranchiseName",
                    NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "AcquisitionRequests" AS open
                        WHERE
                            open."WorkId" = work."Id"
                            AND open."RequestedByProfileId" = @ProfileId
                            AND open."AcquisitionRequestStatusTypeId" BETWEEN 1 AND 5
                    ) AND (work."MediaTypeId" = 2 OR work."MediaSubtypeId" IS NOT DISTINCT FROM 2 OR NOT (EXISTS (
                        SELECT
                            1
                        FROM
                            "MediaAssets" AS asset
                            JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                        WHERE
                            asset."WorkId" = work."Id"
                    ) OR EXISTS (
                        SELECT
                            1
                        FROM
                            "WorkExternalIdentities" AS plex
                        WHERE
                            plex."WorkId" = work."Id"
                            AND plex."Namespace" = 'plex'
                    ))) AS "IsRequestable",
                    (
                        SELECT
                            request."AcquisitionRequestStatusTypeId"
                        FROM
                            "AcquisitionRequests" AS request
                        WHERE
                            request."WorkId" = work."Id"
                            AND request."RequestedByProfileId" = @ProfileId
                        ORDER BY
                            request."CreatedAt" DESC,
                            request."Id" DESC
                        LIMIT 1
                    ) AS "RequestStatusTypeId",
                    work."RuntimeMinutes"::integer AS "RuntimeMinutes",
                    facts."Genres",
                    facts."TrailerKey",
                    facts."Studio"
                FROM
                    "ProfileWorks" AS visible
                    JOIN "Works" AS work ON work."Id" = visible."WorkId"
                    LEFT JOIN "WatchlistEntries" AS entry ON entry."WorkId" = work."Id" AND entry."ProfileId" = @ProfileId
                    LEFT JOIN LATERAL (
                        SELECT
                            (array_agg(fact."Value" ORDER BY fact."IsManual" DESC) FILTER (WHERE fact."FactKey" = 'Overview'))[1] AS "Overview",
                            (array_agg(fact."Value" ORDER BY fact."IsManual" DESC) FILTER (WHERE fact."FactKey" = 'Poster'))[1] AS "PosterUrl",
                            (array_agg(fact."Value" ORDER BY fact."IsManual" DESC) FILTER (WHERE fact."FactKey" = 'Backdrop'))[1] AS "BackdropUrl",
                            (array_agg(fact."Value" ORDER BY fact."IsManual" DESC) FILTER (WHERE fact."FactKey" = 'Genres'))[1] AS "Genres",
                            (array_agg(fact."Value" ORDER BY fact."IsManual" DESC) FILTER (WHERE fact."FactKey" = 'Trailer'))[1] AS "TrailerKey",
                            (array_agg(fact."Value" ORDER BY fact."IsManual" DESC) FILTER (WHERE fact."FactKey" = 'Studio'))[1] AS "Studio"
                        FROM
                            "WorkMetadataFacts" AS fact
                        WHERE
                            fact."WorkId" = work."Id"
                    ) AS facts ON TRUE
                    LEFT JOIN "InstanceModules" AS module ON module."Key" = 'Franchises' AND module."IsEnabled"
                    LEFT JOIN "Franchises" AS franchise ON franchise."Id" = work."FranchiseId" AND module."Key" IS NOT NULL
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
                    CASE WHEN progress."ProgressPositionTypeId" = 1 THEN progress."PositionMs" END AS "ResumePositionMs",
                    coalesce(progress."IsCompleted", FALSE) AS "IsCompleted",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "GameReleases" AS release
                            JOIN "GamePlatforms" AS platform ON platform."Id" = release."GamePlatformId"
                        WHERE
                            release."WorkVersionId" = asset."WorkVersionId"
                            AND asset."MediaAssetTypeId" = 5
                            AND platform."IgdbId" = ANY(@EmulatedPlatforms)
                    ) AS "IsEmulated",
                    (
                        SELECT
                            unitGroup."Name"
                        FROM
                            "WorkUnitGroups" AS unitGroup
                            JOIN "InstanceModules" AS module ON module."Key" = 'UnitGroups' AND module."IsEnabled"
                        WHERE
                            unitGroup."WorkId" = asset."WorkId"
                            AND (section."ParentNumber", section."Number") >= (coalesce(unitGroup."FirstParentNumber", 0), unitGroup."FirstNumber")
                            AND (section."ParentNumber", section."Number") <= (coalesce(unitGroup."LastParentNumber", 0), unitGroup."LastNumber")
                        LIMIT 1
                    ) AS "GroupName",
                    unit."Title" AS "UnitTitle"
                FROM
                    "MediaAssets" AS asset
                    LEFT JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
                    LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                    CROSS JOIN LATERAL (
                        SELECT
                            CASE WHEN unit."WorkUnitTypeId" = 4 THEN 0 ELSE coalesce(parent."Number", 0) END AS "ParentNumber",
                            CASE WHEN unit."WorkUnitTypeId" = 4 THEN parent."Number" ELSE unit."Number" END AS "Number"
                    ) AS section
                    LEFT JOIN "MediaProgress" AS progress ON progress."ProfileId" = @ProfileId
                        AND progress."WorkId" = asset."WorkId"
                        AND progress."WorkUnitId" IS NOT DISTINCT FROM asset."WorkUnitId"
                    CROSS JOIN LATERAL (
                        SELECT
                            count(*) AS "FileCount",
                            sum(file."SizeBytes")::bigint AS "SizeBytes",
                            max(file."DurationMs") AS "DurationMs",
                            coalesce(bool_or(file."MissingSince" IS NULL) FILTER (WHERE NOT file."IsSubtitle"), FALSE) AS "IsAvailable"
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
            sql.Parameters.Add(new EmulatedPlatformsData(GameRomMatching.EmulatedPlatforms));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<WorkAvailabilityV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/creators/works")]
    public sealed class ReadCreatorWorksV1 : UserService<ReadCreatorWorksParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<WorkCardV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadCreatorWorksParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadCreatorWorksParametersV1 parameters, Type resultType) => new("profile.media.read");
        protected override PageRequest GetPageRequest(ReadCreatorWorksParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Everything the profile may see credited to this author or artist the way its details show it, oldest first.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id",
                    work."MediaTypeId",
                    visible."MediaSubtypeId",
                    work."CanonicalTitle",
                    entry."WorkId" IS NOT NULL AS "IsWatchlisted",
                    work."ReleaseYear"::integer AS "ReleaseYear",
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
                    ) AS "PosterUrl"
                FROM
                    "ProfileWorks" AS visible
                    JOIN "Works" AS work ON work."Id" = visible."WorkId"
                    LEFT JOIN "WatchlistEntries" AS entry ON entry."WorkId" = work."Id" AND entry."ProfileId" = visible."ProfileId"
                WHERE
                    visible."ProfileId" = @ProfileId
                    AND NOT visible."IsHidden"
                    AND lower(@Name) = ANY(
                        SELECT
                            lower(credit)
                        FROM
                            (
                                SELECT
                                    CASE WHEN fact."FactKey" = 'Artist' THEN ARRAY[btrim(fact."Value")] ELSE regexp_split_to_array(btrim(fact."Value"), '\s*[,;]\s*') END AS "Credits"
                                FROM
                                    "WorkMetadataFacts" AS fact
                                WHERE
                                    fact."WorkId" = work."Id"
                                    AND fact."FactKey" IN ('Author', 'Artist')
                                ORDER BY
                                    fact."IsManual" DESC,
                                    fact."Id"
                                LIMIT 1
                            ) AS chosen
                            CROSS JOIN LATERAL unnest(chosen."Credits") AS credit
                    )
                ORDER BY
                    work."ReleaseYear" NULLS LAST,
                    work."CanonicalTitle",
                    work."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new CreatorData(Parameters.Name));
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<WorkCardV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/watchlist")]
    public sealed class ReadWatchlistV1 : UserService<ReadWatchlistParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<LibraryItemV1>>()];
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
                    work."CanonicalTitle" AS "Title",
                    work."ReleaseYear"::integer AS "ReleaseYear",
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
                    state."MediaStateTypeId",
                    TRUE AS "IsWatchlisted"
                FROM
                    "WatchlistEntries" AS entry
                    JOIN "ProfileWorks" AS visible ON visible."ProfileId" = entry."ProfileId" AND visible."WorkId" = entry."WorkId"
                    JOIN "Works" AS work ON work."Id" = entry."WorkId"
                    JOIN "ProfileWorkStates" AS state ON state."ProfileId" = entry."ProfileId" AND state."WorkId" = work."Id"
                WHERE
                    entry."ProfileId" = @ProfileId
                    AND NOT visible."IsHidden"
                    AND (@MediaTypeId::smallint IS NULL OR visible."MediaTypeId" = @MediaTypeId AND (@MediaSubtypeId::smallint IS NOT NULL OR visible."MediaSubtypeId" IS NULL))
                    AND (@MediaSubtypeId::smallint IS NULL OR visible."MediaSubtypeId" = @MediaSubtypeId)
                ORDER BY
                    CASE WHEN @LibrarySortTypeId = 1 THEN entry."CreatedAt" END DESC NULLS LAST,
                    CASE WHEN @LibrarySortTypeId = 3 THEN work."ReleaseYear" END DESC NULLS LAST,
                    work."CanonicalTitle",
                    work."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new LibraryFilterData(Parameters.MediaTypeId, Parameters.MediaSubtypeId, null, Parameters.LibrarySortTypeId));
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<LibraryItemV1>(sql, CancellationToken);
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

    [ServiceEndpoint("GET", "/api/v1/works/{workId:long}/credits")]
    public sealed class ReadWorkCreditsV1 : UserService<WorkParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WorkCreditV1>>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Cast before crew, each in the provider's order.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    credit."Name",
                    credit."Role",
                    credit."ImageUrl",
                    credit."IsCrew"
                FROM
                    "WorkCredits" AS credit
                WHERE
                    credit."WorkId" = @WorkId
                    AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."ProfileId" = @ProfileId AND visible."WorkId" = credit."WorkId")
                ORDER BY
                    credit."IsCrew",
                    credit."Position";
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return new BaseDataList<WorkCreditV1>(await Context.ReadSql.ReadListAsync<WorkCreditV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/works/{workId:long}/seasons")]
    public sealed class ReadWorkSeasonsV1 : UserService<WorkParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WorkSeasonV1>>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Specials after the numbered seasons; an episode counts as here when a video of it has a present file.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    season."Number"::integer AS "SeasonNumber",
                    season."Title",
                    season."ImageUrl",
                    extract(year FROM season."ReleasedOn")::integer AS "ReleaseYear",
                    count(episode."Id")::integer AS "EpisodeCount",
                    (count(episode."Id") FILTER (WHERE EXISTS (
                        SELECT
                            1
                        FROM
                            "MediaAssets" AS asset
                            JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id"
                        WHERE
                            asset."WorkUnitId" = episode."Id"
                            AND asset."MediaAssetTypeId" = 1
                            AND file."MissingSince" IS NULL
                            AND NOT file."IsSubtitle"
                    )))::integer AS "AvailableCount"
                FROM
                    "WorkUnits" AS season
                    LEFT JOIN "WorkUnits" AS episode ON episode."ParentWorkUnitId" = season."Id" AND episode."WorkUnitTypeId" = 2
                WHERE
                    season."WorkId" = @WorkId
                    AND season."WorkUnitTypeId" = 1
                    AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."ProfileId" = @ProfileId AND visible."WorkId" = season."WorkId")
                GROUP BY
                    season."Id"
                ORDER BY
                    season."Number" = 0,
                    season."Number";
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return new BaseDataList<WorkSeasonV1>(await Context.ReadSql.ReadListAsync<WorkSeasonV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/works/{workId:long}/seasons/{seasonNumber:int}/episodes")]
    public sealed class ReadWorkEpisodesV1 : UserService<ReadWorkEpisodesParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WorkEpisodeV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadWorkEpisodesParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadWorkEpisodesParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Here when a video has a present file; requested while its target searches it; not yet aired episodes are upcoming.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    episode."Id",
                    season."Number"::integer AS "SeasonNumber",
                    episode."Number",
                    episode."Title",
                    episode."Overview",
                    episode."ImageUrl",
                    episode."RuntimeMinutes"::integer AS "RuntimeMinutes",
                    episode."ReleasedOn",
                    playable."Id" AS "MediaAssetId",
                    CASE WHEN progress."ProgressPositionTypeId" = 1 THEN progress."PositionMs" END AS "ResumePositionMs",
                    playable."DurationMs",
                    coalesce(progress."IsCompleted", FALSE) AS "IsCompleted",
                    CASE
                        WHEN playable."Id" IS NOT NULL THEN 3
                        WHEN episode."IsMonitored" AND EXISTS (SELECT 1 FROM "WantedItems" AS wanted WHERE wanted."WorkId" = episode."WorkId" AND NOT wanted."IsPaused") THEN 2
                        ELSE 1
                    END::smallint AS "MediaStateTypeId",
                    coalesce(episode."ReleasedOn" > current_date, FALSE) AS "IsUpcoming",
                    (
                        SELECT
                            unitGroup."Name"
                        FROM
                            "WorkUnitGroups" AS unitGroup
                            JOIN "InstanceModules" AS module ON module."Key" = 'UnitGroups' AND module."IsEnabled"
                        WHERE
                            unitGroup."WorkId" = episode."WorkId"
                            AND (season."Number", episode."Number") >= (coalesce(unitGroup."FirstParentNumber", 0), unitGroup."FirstNumber")
                            AND (season."Number", episode."Number") <= (coalesce(unitGroup."LastParentNumber", 0), unitGroup."LastNumber")
                        LIMIT 1
                    ) AS "GroupName"
                FROM
                    "WorkUnits" AS episode
                    JOIN "WorkUnits" AS season ON season."Id" = episode."ParentWorkUnitId"
                    LEFT JOIN LATERAL (
                        SELECT
                            asset."Id",
                            max(file."DurationMs") AS "DurationMs"
                        FROM
                            "MediaAssets" AS asset
                            JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."MissingSince" IS NULL AND NOT file."IsSubtitle"
                        WHERE
                            asset."WorkUnitId" = episode."Id"
                            AND asset."MediaAssetTypeId" = 1
                        GROUP BY
                            asset."Id"
                        ORDER BY
                            asset."Id"
                        LIMIT 1
                    ) AS playable ON TRUE
                    LEFT JOIN "MediaProgress" AS progress ON progress."ProfileId" = @ProfileId
                        AND progress."WorkId" = episode."WorkId"
                        AND progress."WorkUnitId" = episode."Id"
                WHERE
                    episode."WorkId" = @WorkId
                    AND episode."WorkUnitTypeId" = 2
                    AND season."Number" = @SeasonNumber
                    AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."ProfileId" = @ProfileId AND visible."WorkId" = episode."WorkId")
                ORDER BY
                    episode."Number";
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return new BaseDataList<WorkEpisodeV1>(await Context.ReadSql.ReadListAsync<WorkEpisodeV1>(sql, CancellationToken));
        }
    }
}
