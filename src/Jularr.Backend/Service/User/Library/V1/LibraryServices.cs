using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Logic.Media;

namespace Jularr.Backend.Service.User.Library.V1
{
    [ServiceEndpoint("GET", "/api/v1/library")]
    public sealed class ReadLibraryV1 : UserService<ReadLibraryParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<LibraryItemV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadLibraryParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadLibraryParametersV1 parameters, Type resultType) => new("profile.media.read");
        protected override PageRequest GetPageRequest(ReadLibraryParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override Task<IServiceOutput> OnRead() => ReadAsync(Context.ReadSql, Parameters, Auth.ProfileId!.Value, Page, false, CancellationToken);

        internal static async Task<IServiceOutput> ReadAsync(SqlContext.SqlReadCommands readSql, ReadLibraryParametersV1 parameters, long profileId, PageRequest? page, bool options, CancellationToken cancellationToken)
        {
            // Cards and filter options share the same visibility and availability boundary.
            var scope = """
                SELECT
                    work."Id",
                    visible."Title",
                    work."ReleaseYear",
                    available."AddedAt"
                FROM
                    "ProfileWorks" AS visible
                    JOIN "Works" AS work ON work."Id" = visible."WorkId"
                    LEFT JOIN (
                        SELECT
                            asset."WorkId",
                            max(file."CreatedAt") AS "AddedAt"
                        FROM
                            "MediaAssets" AS asset
                            JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."MissingSince" IS NULL AND NOT file."IsSubtitle"
                        GROUP BY
                            asset."WorkId"
                    ) AS available ON available."WorkId" = work."Id"
                WHERE
                    visible."ProfileId" = @ProfileId
                    AND NOT visible."IsHidden"
                    AND (
                        1 = ANY(@AvailabilityTypeIds::smallint[]) AND available."AddedAt" IS NOT NULL
                        OR EXISTS (
                            SELECT 1 FROM "AcquisitionRequests" AS request
                            WHERE request."WorkId" = work."Id" AND request."AcquisitionRequestStatusTypeId" BETWEEN 1 AND 5
                                AND (2 = ANY(@AvailabilityTypeIds::smallint[]) AND request."RequestedByProfileId" = @ProfileId
                                    OR 3 = ANY(@AvailabilityTypeIds::smallint[]) AND request."RequestedByProfileId" IS DISTINCT FROM @ProfileId)
                        )
                    )
                    AND (cardinality(@MediaTypeIds::smallint[]) + cardinality(@MediaSubtypeIds::smallint[]) = 0
                        OR visible."MediaSubtypeId" IS NULL AND visible."MediaTypeId" = ANY(@MediaTypeIds::smallint[])
                        OR visible."MediaSubtypeId" = ANY(@MediaSubtypeIds::smallint[]))
                    AND (@ReleaseYearFrom::integer IS NULL OR work."ReleaseYear" >= @ReleaseYearFrom)
                    AND (@ReleaseYearTo::integer IS NULL OR work."ReleaseYear" <= @ReleaseYearTo)
                    AND (@Query::text IS NULL OR EXISTS (
                        SELECT 1 FROM "WorkTitles" AS title WHERE title."WorkId" = work."Id"
                            AND title."Title" ILIKE '%' || replace(replace(replace(@Query, '\', '\\'), '%', '\%'), '_', '\_') || '%'
                    ))
                """;

            var genres = """
                SELECT
                    btrim(genre) AS "Genre"
                FROM unnest(string_to_array((
                    SELECT fact."Value" FROM "WorkMetadataFacts" AS fact
                    WHERE fact."WorkId" = work."Id" AND fact."FactKey" = 'Genres'
                    ORDER BY fact."IsManual" DESC LIMIT 1
                ), ',')) AS genre
                WHERE btrim(genre) <> ''
                """;

            var tracks = """
                SELECT
                    asset."WorkId",
                    track."Language",
                    track."MediaTrackTypeId"
                FROM
                    "MediaAssets" AS asset
                    JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."MissingSince" IS NULL AND NOT file."IsSubtitle"
                    JOIN "MediaTracks" AS track ON track."StoredFileId" = file."Id"
                WHERE
                    track."Language" IS NOT NULL
                    AND btrim(track."Language") <> ''
                """;

            var sql = new SqlStatement();
            sql.Parameters.Add(new LibraryPageFilterData(parameters.MediaTypeIds, parameters.MediaSubtypeIds, parameters.ReleaseYearFrom, parameters.ReleaseYearTo,
                parameters.Query, parameters.AvailabilityTypeIds, parameters.ProgressStateId, parameters.Genres, parameters.AudioLanguages, parameters.SubtitleLanguages, parameters.MinimumRating));
            sql.Parameters.Add(new ProfileParameters(profileId));
            sql.Parameters.Add(LanguageCodes.Aliases);
            sql.DisableJit = true;
            if (options)
            {
                sql.Statement = $$"""
                WITH scoped AS MATERIALIZED ({{scope}}), tracks AS MATERIALIZED (
                    SELECT
                        track."Language",
                        track."MediaTrackTypeId"
                    FROM
                        ({{tracks}}) AS track
                        JOIN scoped AS work ON work."Id" = track."WorkId"
                )
                SELECT
                    ARRAY(SELECT DISTINCT genre."Genre" FROM scoped AS work CROSS JOIN LATERAL ({{genres}}) AS genre ORDER BY genre."Genre" LIMIT 100) AS "Genres",
                    ARRAY(SELECT DISTINCT "Language" FROM tracks WHERE "MediaTrackTypeId" = 2 ORDER BY "Language") AS "AudioLanguages",
                    ARRAY(SELECT DISTINCT "Language" FROM tracks WHERE "MediaTrackTypeId" = 3 ORDER BY "Language") AS "SubtitleLanguages"
                """;
                return await readSql.ReadRequiredAsync<LibraryFilterOptionsV1>(sql, cancellationToken);
            }
            var orderBy = parameters.LibrarySortTypeId switch
            {
                LibrarySortType.Year => "work.\"ReleaseYear\" DESC NULLS LAST, work.\"Title\", work.\"Id\"",
                LibrarySortType.Progress => "workProgress.\"ProgressPercent\" DESC NULLS LAST, work.\"Title\", work.\"Id\"",
                LibrarySortType.Title => "work.\"Title\", work.\"Id\"",
                _ => "work.\"AddedAt\" DESC NULLS LAST, work.\"Title\", work.\"Id\""
            };
            var useProgress = parameters.LibrarySortTypeId == LibrarySortType.Progress || parameters.ProgressStateId is not null;

            sql.Statement = WorkCardProjection.Page(
                $$"""
                SELECT
                    work."Id"
                FROM
                    ({{scope}}) AS work
                    {{(useProgress ? WorkCardProjection.PageProgressJoin : "")}}
                WHERE TRUE
                    {{(parameters.ProgressStateId is null ? "" : "AND workProgress.\"ProgressStateId\" = @ProgressStateId")}}
                    {{(parameters.Genres.Length == 0 ? "" : "AND EXISTS (SELECT 1 FROM (" + genres + ") AS genres WHERE genres.\"Genre\" = ANY(@Genres::text[]))")}}
                    {{(parameters.AudioLanguages.Length == 0 ? "" : "AND work.\"Id\" IN (SELECT tracks.\"WorkId\" FROM (" + tracks + ") AS tracks WHERE tracks.\"MediaTrackTypeId\" = 2 AND tracks.\"Language\" = ANY(@AudioLanguages::text[]))")}}
                    {{(parameters.SubtitleLanguages.Length == 0 ? "" : "AND work.\"Id\" IN (SELECT tracks.\"WorkId\" FROM (" + tracks + ") AS tracks WHERE tracks.\"MediaTrackTypeId\" = 3 AND tracks.\"Language\" = ANY(@SubtitleLanguages::text[]))")}}
                    {{(parameters.MinimumRating is null ? "" : "AND (" + WorkCardProjection.Rating + ") >= @MinimumRating")}}
                ORDER BY
                    {{orderBy}}
                LIMIT @PageSize
                OFFSET @Offset
                """,
                """
                EXISTS (SELECT 1 FROM "WatchlistEntries" AS entry WHERE entry."WorkId" = work."Id" AND entry."ProfileId" = @ProfileId)
                """);
            sql.Page = page;

            return await readSql.ReadPageAsync<LibraryItemV1>(sql, cancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/library/filter-options")]
    public sealed class ReadLibraryFilterOptionsV1 : UserService<ReadLibraryParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LibraryFilterOptionsV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadLibraryParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadLibraryParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override Task<IServiceOutput> OnRead() => ReadLibraryV1.ReadAsync(Context.ReadSql, Parameters, Auth.ProfileId!.Value, null, true, CancellationToken);
    }

    [ServiceEndpoint("GET", "/api/v1/continue")]
    public sealed class ReadContinueV1 : UserService<ReadContinueParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<ContinueItemV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadContinueParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadContinueParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Started, unfinished items with a file that can be played now.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    progress."WorkId",
                    asset."Id" AS "MediaAssetId",
                    work."MediaTypeId",
                    visible."MediaSubtypeId",
                    visible."Title" AS "Title",
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
                    unit."WorkUnitTypeId",
                    unit."Number" AS "UnitNumber",
                    parent."Number" AS "ParentUnitNumber",
                    coalesce(progress."PositionMs", 0) AS "PositionMs",
                    progress."DurationMs",
                    progress."Progression",
                    asset."MediaAssetTypeId"
                FROM
                    "MediaProgress" AS progress
                    JOIN "ProfileWorks" AS visible ON visible."ProfileId" = progress."ProfileId" AND visible."WorkId" = progress."WorkId" AND NOT visible."IsHidden"
                    JOIN "Works" AS work ON work."Id" = progress."WorkId"
                    JOIN LATERAL (
                        SELECT
                            candidate."Id",
                            candidate."MediaAssetTypeId"
                        FROM
                            "MediaAssets" AS candidate
                            JOIN "StoredFiles" AS available ON available."MediaAssetId" = candidate."Id" AND available."MissingSince" IS NULL
                            JOIN "InstanceModules" AS module ON module."Key" = CASE
                                WHEN candidate."MediaAssetTypeId" IN (3, 4) THEN 'Reading'
                                WHEN candidate."MediaAssetTypeId" IN (1, 2) THEN 'Playback'
                            END AND module."IsEnabled"
                        WHERE
                            candidate."WorkId" = progress."WorkId"
                            AND candidate."WorkUnitId" IS NOT DISTINCT FROM progress."WorkUnitId"
                        ORDER BY
                            candidate."Id"
                        LIMIT 1
                    ) AS asset ON TRUE
                    LEFT JOIN "WorkUnits" AS unit ON unit."Id" = progress."WorkUnitId"
                    LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                WHERE
                    progress."ProfileId" = @ProfileId
                    AND NOT progress."IsCompleted"
                    AND (progress."PositionMs" > 0 OR progress."Locator" IS NOT NULL)
                ORDER BY
                    progress."UpdatedAt" DESC,
                    progress."Id" DESC
                LIMIT 20;
                """;
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return new BaseDataList<ContinueItemV1>(await Context.ReadSql.ReadListAsync<ContinueItemV1>(sql, CancellationToken));
        }
    }
}
