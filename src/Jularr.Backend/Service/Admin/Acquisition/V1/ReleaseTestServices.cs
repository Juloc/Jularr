using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    // A release name tried against every video profile with the formats, group ratings and switches a search would use; nothing is searched or stored.
    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/release-test")]
    public sealed class TestReleaseV1 : AdminService<TestReleaseParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReleaseTestV1>()];
        protected override ModuleRequirement GetInstanceModules(TestReleaseParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(TestReleaseParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."Name",
                    (SELECT min(applies."AcquisitionMediaKindId") FROM "AcquisitionProfileMediaKinds" AS applies WHERE applies."AcquisitionProfileId" = profile."Id") AS "MediaKind",
                    profile."Definition"::text AS "Definition",
                    coalesce((
                        SELECT jsonb_agg(jsonb_build_object('Id', format."Id", 'Name', format."Name", 'IncludeWhenRenaming', format."IncludeWhenRenaming", 'Conditions', format."Conditions")
                            ORDER BY format."Id")
                        FROM "CustomFormats" AS format
                    ), '[]')::text AS "Formats",
                    coalesce((
                        SELECT jsonb_object_agg(rated."Name", rated."ReleaseGroupRatingId")
                        FROM "ReleaseGroups" AS rated
                        WHERE rated."ReleaseGroupRatingId" IS NOT NULL
                    ), '{}')::text AS "ReleaseGroupRatings"
                FROM
                    "AcquisitionProfiles" AS profile
                WHERE
                    EXISTS (SELECT 1 FROM "AcquisitionProfileMediaKinds" AS applies WHERE applies."AcquisitionProfileId" = profile."Id" AND applies."AcquisitionMediaKindId" IN (1, 2, 3))
                ORDER BY
                    lower(profile."Name"),
                    profile."Id";
                """;
            var rows = await Context.ReadSql.ReadListAsync<ReleaseTestProfileData>(sql, CancellationToken);
            var release = ReleaseParser.Parse(Parameters.Title);
            var profiles = rows.Select(row =>
            {
                var (type, subtype) = row.MediaKind switch
                {
                    AcquisitionMediaKind.Series => (MediaType.Series, (MediaSubtype?)null),
                    AcquisitionMediaKind.Anime => (MediaType.Series, MediaSubtype.Anime),
                    _ => (MediaType.Movie, (MediaSubtype?)null)
                };
                var target = new WantedTargetData(0, 0, type, subtype, release.SeriesTitle, null, [], row.Id, row.Name, row.Definition, [], false, false, false, 0, [], [], null, null, null,
                    formats: row.Formats, releaseGroupRatings: row.ReleaseGroupRatings);
                var score = ReleaseScorer.Score(AcquisitionSelectionLogic.ReadProfile(target), new ReleaseCandidate(release));
                return new ReleaseTestProfileV1(row.Id, row.Name, score.Accepted, score.FormatScore, score.RejectionReasons.FirstOrDefault()?.Code,
                    new(score.FormatPoints.Select(point => new ReleaseScorePointV1(point.Code, point.Value, point.Points))));
            }).ToList();
            var quality = ReleaseQuality.GetKey(release, true) ?? "Unknown";
            return new ReleaseTestV1(quality, release.ReleaseGroup, new(profiles));
        }
    }
}
