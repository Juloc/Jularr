using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Library.V1
{
    [ServiceEndpoint("GET", "/api/v1/library")]
    public sealed class ReadLibraryV1 : UserService<ReadLibraryParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<LibraryItemV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadLibraryParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadLibraryParametersV1 parameters, Type resultType) => new("profile.media.read");
        protected override PageRequest GetPageRequest(ReadLibraryParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Works with media, a Plex item or an acquisition target; search stays local.
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
                    CASE WHEN available."AddedAt" IS NOT NULL OR plex."Value" IS NOT NULL THEN 3 ELSE 2 END AS "MediaStateTypeId",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "WatchlistEntries" AS entry
                        WHERE
                            entry."WorkId" = work."Id"
                            AND entry."ProfileId" = @ProfileId
                    ) AS "IsWatchlisted"
                FROM
                    "Works" AS work
                    LEFT JOIN LATERAL (
                        SELECT
                            max(file."CreatedAt") AS "AddedAt"
                        FROM
                            "MediaAssets" AS asset
                            JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."MissingSince" IS NULL
                        WHERE
                            asset."WorkId" = work."Id"
                    ) AS available ON TRUE
                    LEFT JOIN "WorkExternalIdentities" AS plex ON plex."WorkId" = work."Id" AND plex."Namespace" = 'plex'
                WHERE
                    (available."AddedAt" IS NOT NULL OR plex."Value" IS NOT NULL OR EXISTS (
                        SELECT
                            1
                        FROM
                            "WantedItems" AS wanted
                        WHERE
                            wanted."WorkId" = work."Id"
                    ))
                    AND (@MediaTypeId::smallint IS NULL OR work."MediaTypeId" = @MediaTypeId)
                    AND (@Query::text IS NULL OR EXISTS (
                        SELECT
                            1
                        FROM
                            "WorkTitles" AS title
                        WHERE
                            title."WorkId" = work."Id"
                            AND title."Title" ILIKE '%' || replace(replace(replace(@Query, '\', '\\'), '%', '\%'), '_', '\_') || '%'
                    ))
                ORDER BY
                    CASE WHEN @LibrarySortTypeId = 1 THEN available."AddedAt" END DESC NULLS LAST,
                    CASE WHEN @LibrarySortTypeId = 3 THEN work."ReleaseYear" END DESC NULLS LAST,
                    work."CanonicalTitle",
                    work."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new LibraryFilterData(Parameters.MediaTypeId, Parameters.Query, Parameters.LibrarySortTypeId));
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<LibraryItemV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/continue")]
    public sealed class ReadContinueV1 : UserService<ReadContinueParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<ContinueItemV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadContinueParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(ReadContinueParametersV1 parameters, Type resultType) => new("profile.playback");

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
                    work."CanonicalTitle" AS "Title",
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
                    progress."PositionMs",
                    progress."DurationMs"
                FROM
                    "MediaProgress" AS progress
                    JOIN "Works" AS work ON work."Id" = progress."WorkId"
                    JOIN LATERAL (
                        SELECT
                            candidate."Id"
                        FROM
                            "MediaAssets" AS candidate
                            JOIN "StoredFiles" AS available ON available."MediaAssetId" = candidate."Id" AND available."MissingSince" IS NULL
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
                    AND progress."PositionMs" > 0
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
