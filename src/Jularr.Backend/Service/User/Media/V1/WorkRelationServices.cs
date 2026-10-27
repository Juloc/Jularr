using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Library.V1;

namespace Jularr.Backend.Service.User.Media.V1
{
    [ServiceEndpoint("GET", "/api/v1/works/{workId:long}/relations")]
    public sealed class ReadWorkRelationsV1 : UserService<WorkParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WorkRelationV1>>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library", "Franchises");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The stored relations as this Work sees them, then the other titles of its series that no relation names; a title that is a Work only shows to a profile that may see it.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH links AS (
                    SELECT
                        link."RelationId",
                        link."WorkRelationTypeId",
                        link."SourceKey",
                        link."OtherWorkId",
                        link."ExternalWorkId"
                    FROM
                        "WorkRelationLinks" AS link
                    WHERE
                        link."WorkId" = @WorkId
                        AND NOT link."IsSuppressed"
                    UNION ALL
                    SELECT
                        NULL::bigint,
                        9::smallint,
                        coalesce(
                            CASE WHEN me."IsFranchiseManual" THEN 'manual' END,
                            (SELECT min(identity."Namespace") FROM "FranchiseExternalIdentities" AS identity WHERE identity."FranchiseId" = me."FranchiseId"),
                            'manual'
                        )::varchar(16),
                        other."Id",
                        NULL::bigint
                    FROM
                        "Works" AS me
                        JOIN "Works" AS other ON other."FranchiseId" = me."FranchiseId" AND other."Id" <> me."Id"
                    WHERE
                        me."Id" = @WorkId
                        AND NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "WorkRelationLinks" AS stored
                            WHERE
                                stored."WorkId" = me."Id"
                                AND stored."OtherWorkId" = other."Id"
                                AND NOT stored."IsSuppressed"
                        )
                ), picked AS (
                    -- One row for a title and a type, however many relations name it; a person's own wins.
                    SELECT DISTINCT ON (coalesce(links."OtherWorkId", -links."ExternalWorkId"), links."WorkRelationTypeId")
                        links."RelationId",
                        links."WorkRelationTypeId",
                        links."SourceKey",
                        links."OtherWorkId",
                        links."ExternalWorkId"
                    FROM
                        links
                    ORDER BY
                        coalesce(links."OtherWorkId", -links."ExternalWorkId"),
                        links."WorkRelationTypeId",
                        links."SourceKey" = 'manual' DESC,
                        links."RelationId" NULLS LAST
                )
                SELECT
                    picked."RelationId" AS "WorkRelationId",
                    picked."WorkRelationTypeId",
                    picked."SourceKey",
                    other."Id" AS "RelatedWorkId",
                    CASE WHEN other."Id" IS NULL THEN listed."Namespace" END AS "Namespace",
                    CASE WHEN other."Id" IS NULL THEN listed."Value" END AS "ExternalId",
                    coalesce(visible."MediaTypeId", listed."MediaTypeId") AS "MediaTypeId",
                    CASE WHEN other."Id" IS NULL THEN listed."MediaSubtypeId" ELSE visible."MediaSubtypeId" END AS "MediaSubtypeId",
                    coalesce(visible."Title", listed."Title") AS "Title",
                    coalesce(other."ReleaseYear", listed."ReleaseYear")::integer AS "ReleaseYear",
                    CASE
                        WHEN other."Id" IS NULL THEN listed."ImageUrl"
                        ELSE (
                            SELECT
                                fact."Value"
                            FROM
                                "WorkMetadataFacts" AS fact
                            WHERE
                                fact."WorkId" = other."Id"
                                AND fact."FactKey" = 'Poster'
                            ORDER BY
                                fact."IsManual" DESC
                            LIMIT 1
                        )
                    END AS "PosterUrl",
                    NULL::smallint AS "AvailabilityStateId",
                    0 AS "AvailableUnits",
                    0 AS "MonitoredUnits"
                FROM
                    picked
                    LEFT JOIN "Works" AS other ON other."Id" = picked."OtherWorkId"
                    LEFT JOIN "ProfileWorks" AS visible ON visible."ProfileId" = @ProfileId AND visible."WorkId" = other."Id" AND NOT visible."IsHidden"
                    LEFT JOIN "ExternalWorks" AS listed ON listed."Id" = picked."ExternalWorkId"
                WHERE
                    (picked."OtherWorkId" IS NULL AND listed."Id" IS NOT NULL OR visible."WorkId" IS NOT NULL)
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "ProfileWorks" AS own
                        WHERE
                            own."ProfileId" = @ProfileId
                            AND own."WorkId" = @WorkId
                            AND NOT own."IsHidden"
                    )
                ORDER BY
                    coalesce(other."ReleaseYear", listed."ReleaseYear") NULLS LAST,
                    coalesce(visible."Title", listed."Title"),
                    picked."WorkRelationTypeId",
                    picked."RelationId" NULLS LAST;
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            var relations = await Context.ReadSql.ReadListAsync<WorkRelationV1>(sql, CancellationToken);

            // What the library and the acquisition say of the related Works: the card's own projection, for those titles alone.
            var workIds = relations.Where(relation => relation.RelatedWorkId is not null).Select(relation => relation.RelatedWorkId!.Value).Distinct().Order().ToArray();
            if (workIds.Length == 0) return new BaseDataList<WorkRelationV1>(relations);

            sql = new SqlStatement();
            sql.Statement = WorkCardProjection.Page("SELECT unnest(@WorkIds)", "FALSE");
            sql.Parameters.Add(new WorkRelationWorksData(workIds));
            sql.Parameters.Add(LanguageCodes.Aliases);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            var cards = (await Context.ReadSql.ReadListAsync<LibraryItemV1>(sql, CancellationToken)).ToDictionary(card => card.Id);
            return new BaseDataList<WorkRelationV1>([.. relations.Select(relation => relation.RelatedWorkId is { } workId && cards.TryGetValue(workId, out var card)
                ? new WorkRelationV1(relation.WorkRelationId, relation.WorkRelationTypeId, relation.SourceKey, relation.RelatedWorkId, null, null, relation.MediaTypeId, relation.MediaSubtypeId, relation.Title,
                    relation.ReleaseYear, relation.PosterUrl, card.AvailabilityStateId, card.AvailableUnits, card.MonitoredUnits)
                : relation)]);
        }
    }
}
