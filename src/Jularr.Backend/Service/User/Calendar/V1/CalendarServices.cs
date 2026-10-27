using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Calendar.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Calendar.V1
{
    [ServiceEndpoint("GET", "/api/v1/calendar")]
    public sealed class ReadCalendarV1 : UserService<ReadCalendarParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<CalendarEntryV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadCalendarParametersV1 parameters) => ModuleRequirement.All("Library", "Calendar");
        protected override ServicePermission GetPermission(ReadCalendarParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Episodes, volumes and tracks, or the Work itself without units, of the profile's Works: watchlisted, requested by it or already in the library.
            // Both parts read only those Works (tied), so units and release dates of catalog titles are never scanned.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH tied AS MATERIALIZED (
                    SELECT entry."WorkId" FROM "WatchlistEntries" AS entry WHERE entry."ProfileId" = @ProfileId
                    UNION
                    SELECT request."WorkId" FROM "AcquisitionRequests" AS request WHERE request."RequestedByProfileId" = @ProfileId
                    UNION
                    SELECT asset."WorkId" FROM "MediaAssets" AS asset
                )
                (
                SELECT
                    unit."ReleasedOn" AS "Day",
                    work."Id" AS "WorkId",
                    work."MediaTypeId",
                    visible."MediaSubtypeId",
                    visible."Title" AS "Title",
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
                    unit."Title" AS "UnitTitle",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "MediaAssets" AS asset
                            JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                        WHERE
                            asset."WorkId" = work."Id"
                            AND asset."WorkUnitId" = unit."Id"
                    ) AS "IsAvailable",
                    entry."WorkId" IS NOT NULL AS "IsWatchlisted",
                    EXISTS (SELECT 1 FROM "AcquisitionRequests" AS asked WHERE asked."WorkId" = work."Id" AND asked."RequestedByProfileId" = @ProfileId) AS "IsRequested"
                FROM
                    "WorkUnits" AS unit
                    JOIN "Works" AS work ON work."Id" = unit."WorkId"
                    JOIN "ProfileWorks" AS visible ON visible."ProfileId" = @ProfileId AND visible."WorkId" = work."Id" AND NOT visible."IsHidden"
                    LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                    LEFT JOIN "WatchlistEntries" AS entry ON entry."ProfileId" = @ProfileId AND entry."WorkId" = work."Id"
                WHERE
                    unit."WorkId" = ANY(ARRAY(SELECT tied."WorkId" FROM tied))
                    AND visible."WorkId" = ANY(ARRAY(SELECT tied."WorkId" FROM tied))
                    AND unit."ReleasedOn" >= @From
                    AND unit."ReleasedOn" < @From + @Days
                    AND NOT EXISTS (SELECT 1 FROM "WorkUnits" AS child WHERE child."ParentWorkUnitId" = unit."Id")
                    AND (
                        entry."WorkId" IS NOT NULL
                        OR EXISTS (SELECT 1 FROM "AcquisitionRequests" AS request WHERE request."WorkId" = work."Id" AND request."RequestedByProfileId" = @ProfileId)
                        OR EXISTS (SELECT 1 FROM "MediaAssets" AS asset WHERE asset."WorkId" = work."Id")
                    )
                )
                UNION ALL
                (
                SELECT
                    work."ReleasedOn" AS "Day",
                    work."Id" AS "WorkId",
                    work."MediaTypeId",
                    visible."MediaSubtypeId",
                    visible."Title" AS "Title",
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
                    NULL AS "WorkUnitTypeId",
                    NULL AS "UnitNumber",
                    NULL AS "ParentUnitNumber",
                    NULL AS "UnitTitle",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "MediaAssets" AS asset
                            JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                        WHERE
                            asset."WorkId" = work."Id"
                    ) AS "IsAvailable",
                    entry."WorkId" IS NOT NULL AS "IsWatchlisted",
                    EXISTS (SELECT 1 FROM "AcquisitionRequests" AS asked WHERE asked."WorkId" = work."Id" AND asked."RequestedByProfileId" = @ProfileId) AS "IsRequested"
                FROM
                    "Works" AS work
                    JOIN "ProfileWorks" AS visible ON visible."ProfileId" = @ProfileId AND visible."WorkId" = work."Id" AND NOT visible."IsHidden"
                    LEFT JOIN "WatchlistEntries" AS entry ON entry."ProfileId" = @ProfileId AND entry."WorkId" = work."Id"
                WHERE
                    visible."WorkId" = ANY(ARRAY(SELECT tied."WorkId" FROM tied))
                    AND work."ReleasedOn" >= @From
                    AND work."ReleasedOn" < @From + @Days
                    AND NOT EXISTS (SELECT 1 FROM "WorkUnits" AS unit WHERE unit."WorkId" = work."Id")
                    AND (
                        entry."WorkId" IS NOT NULL
                        OR EXISTS (SELECT 1 FROM "AcquisitionRequests" AS request WHERE request."WorkId" = work."Id" AND request."RequestedByProfileId" = @ProfileId)
                        OR EXISTS (SELECT 1 FROM "MediaAssets" AS asset WHERE asset."WorkId" = work."Id")
                    )
                )
                ORDER BY
                    "Day",
                    "Title",
                    "ParentUnitNumber" NULLS FIRST,
                    "UnitNumber" NULLS FIRST
                LIMIT 500;
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return new BaseDataList<CalendarEntryV1>(await Context.ReadSql.ReadListAsync<CalendarEntryV1>(sql, CancellationToken));
        }
    }
}
