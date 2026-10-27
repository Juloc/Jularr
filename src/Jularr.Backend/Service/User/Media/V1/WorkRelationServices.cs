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
            // The stored relations as this Work sees them, then the other titles of its series that no relation names; a related Work only shows to a profile that may see it.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH links AS (
                    SELECT
                        link."RelationId",
                        link."WorkRelationTypeId",
                        link."SourceKey",
                        link."OtherWorkId"
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
                        other."Id"
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
                    SELECT DISTINCT ON (links."OtherWorkId", links."WorkRelationTypeId")
                        links."RelationId",
                        links."WorkRelationTypeId",
                        links."SourceKey",
                        links."OtherWorkId"
                    FROM
                        links
                    ORDER BY
                        links."OtherWorkId",
                        links."WorkRelationTypeId",
                        links."SourceKey" = 'manual' DESC,
                        links."RelationId" NULLS LAST
                )
                SELECT
                    picked."RelationId" AS "WorkRelationId",
                    picked."WorkRelationTypeId",
                    picked."SourceKey",
                    other."Id" AS "RelatedWorkId",
                    visible."MediaTypeId",
                    visible."MediaSubtypeId",
                    visible."Title",
                    other."ReleaseYear"::integer AS "ReleaseYear",
                    coalesce((
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
                    ), other."ImageUrl") AS "PosterUrl",
                    NULL::smallint AS "AvailabilityStateId",
                    0 AS "AvailableUnits",
                    0 AS "MonitoredUnits"
                FROM
                    picked
                    JOIN "Works" AS other ON other."Id" = picked."OtherWorkId"
                    JOIN "ProfileWorks" AS visible ON visible."ProfileId" = @ProfileId AND visible."WorkId" = other."Id" AND NOT visible."IsHidden"
                WHERE
                    EXISTS (
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
                    other."ReleaseYear" NULLS LAST,
                    visible."Title",
                    picked."WorkRelationTypeId",
                    picked."RelationId" NULLS LAST;
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            var relations = await Context.ReadSql.ReadListAsync<WorkRelationV1>(sql, CancellationToken);

            // What the library and the acquisition say of the related Works: the card's own projection; a catalog title without details the profile has neither a file, a request nor a wanted entry of has none.
            var workIds = relations.Select(relation => relation.RelatedWorkId).Distinct().Order().ToArray();
            if (workIds.Length == 0) return new BaseDataList<WorkRelationV1>(relations);

            sql = new SqlStatement();
            sql.Statement = WorkCardProjection.Page(
                """
                SELECT
                    related."Id"
                FROM
                    "Works" AS related
                WHERE
                    related."Id" = ANY(@WorkIds)
                    AND (
                        related."DetailedAt" IS NOT NULL
                        OR EXISTS (
                            SELECT
                                1
                            FROM
                                "ProfileWorkStates" AS state
                            WHERE
                                state."ProfileId" = @ProfileId
                                AND state."WorkId" = related."Id"
                                AND state."MediaStateTypeId" > 1
                        )
                    )
                ORDER BY
                    related."Id"
                """, "FALSE");
            sql.Parameters.Add(new WorkRelationWorksData(workIds));
            sql.Parameters.Add(LanguageCodes.Aliases);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            var cards = (await Context.ReadSql.ReadListAsync<LibraryItemV1>(sql, CancellationToken)).ToDictionary(card => card.Id);
            return new BaseDataList<WorkRelationV1>([.. relations.Select(relation => cards.TryGetValue(relation.RelatedWorkId, out var card)
                ? new WorkRelationV1(relation.WorkRelationId, relation.WorkRelationTypeId, relation.SourceKey, relation.RelatedWorkId, relation.MediaTypeId, relation.MediaSubtypeId, relation.Title,
                    relation.ReleaseYear, relation.PosterUrl, card.AvailabilityStateId, card.AvailableUnits, card.MonitoredUnits)
                : relation)]);
        }
    }
}
