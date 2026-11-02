using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Integrations.Data.AniList;

namespace Jularr.Backend.Logic.Media
{
    // Typed relations between Works: a sequel, an adaptation. One row says what the related title is to a Work; the other Work derives the inverse (WorkRelationLinks).
    internal static class WorkRelationLogic
    {
        // The most relations one provider names for a Work.
        public const int ProviderLimit = 100;

        // What the Work on the other side is: a sequel's other side is a prequel, an adaptation's the source material; the rest says the same from both sides.
        public static WorkRelationType Inverse(WorkRelationType type) => type switch
        {
            WorkRelationType.Sequel => WorkRelationType.Prequel,
            WorkRelationType.Prequel => WorkRelationType.Sequel,
            WorkRelationType.SideStory or WorkRelationType.SpinOff => WorkRelationType.MainStory,
            WorkRelationType.MainStory => WorkRelationType.SideStory,
            WorkRelationType.Adaptation => WorkRelationType.SourceMaterial,
            WorkRelationType.SourceMaterial => WorkRelationType.Adaptation,
            _ => type
        };

        // A relation between two Works is stored once, from the side its type names first: the sequel's Work is not stored again as a prequel.
        public static bool IsStoredForward(WorkRelationType type) => type is not (WorkRelationType.Prequel or WorkRelationType.MainStory or WorkRelationType.SourceMaterial);

        // AniList's relation types; those that say nothing about the two titles belonging together are left out.
        public static WorkRelationType? FromAniList(string relationType) => relationType switch
        {
            "SEQUEL" => WorkRelationType.Sequel,
            "PREQUEL" => WorkRelationType.Prequel,
            "SIDE_STORY" => WorkRelationType.SideStory,
            "SPIN_OFF" => WorkRelationType.SpinOff,
            "PARENT" => WorkRelationType.MainStory,
            "ADAPTATION" => WorkRelationType.Adaptation,
            "SOURCE" => WorkRelationType.SourceMaterial,
            "ALTERNATIVE" => WorkRelationType.Alternative,
            "SUMMARY" or "COMPILATION" => WorkRelationType.Summary,
            "CHARACTER" => WorkRelationType.SharedUniverse,
            "OTHER" => WorkRelationType.Other,
            _ => null
        };

        // The catalog titles AniList relates a title to; a format Discover does not show (specials, music videos) stays out.
        public static IReadOnlyList<WorkRelationTarget> AniListTargets(AniListMedia media) => [.. media.Relations
            .Select(relation => (Relation: relation, Type: FromAniList(relation.RelationType), Kind: AniListMatching.CatalogType(relation.Type, relation.Format)))
            .Where(item => item.Type is not null && item.Kind is not null)
            .Select(item => new WorkRelationTarget("anilist", item.Relation.Id.ToString(CultureInfo.InvariantCulture), item.Type!.Value, item.Kind!.Value.Type, item.Kind.Value.Subtype,
                TextLimit.Cut(item.Relation.Title, 500)!, item.Relation.Year is >= 1800 and <= 2200 ? item.Relation.Year : null, item.Relation.CoverUrl))
            .Where(target => target.Title.Length > 0)
            .DistinctBy(target => (target.Value, target.WorkRelationTypeId))
            .Take(ProviderLimit)];

        // The relations of every AniList entry a Work holds (a series holds one per season); the entries of the Work itself are not relations of it.
        public static IReadOnlyList<WorkRelationTarget> AniListTargets(IEnumerable<AniListMedia> entries, IReadOnlyCollection<int> ownIds) => [.. entries
            .SelectMany(media => AniListTargets(media))
            .Where(target => !ownIds.Contains(int.Parse(target.Value, CultureInfo.InvariantCulture)))
            .DistinctBy(target => (target.Value, target.WorkRelationTypeId))
            .Take(ProviderLimit)];

        // What a provider names replaces its earlier relations of the Work; the titles it names are kept as catalog Works, and a relation a person hid stays hidden.
        // A title that turns out to be the Work itself (one of its own seasons) is no relation.
        public static async Task SaveWorkRelationsAsync(this LogicContext ctx, WorkRelationSyncData data, CancellationToken ct)
        {
            var related = new List<WorkRelationTargetWorkData>();
            foreach (var group in data.Targets.GroupBy(target => (target.Namespace, target.MediaTypeId, target.MediaSubtypeId)))
            {
                var list = new ExternalWorkListData(group.Key.Namespace, group.Key.MediaTypeId, group.Key.MediaSubtypeId);
                var works = group.DistinctBy(target => target.Value)
                    .Select(target => new ExternalWorkData(target.Namespace, target.Value, target.Title, null, target.ReleaseYear, null, null, target.ImageUrl, null, null)).ToArray();
                var saved = (await ctx.SaveExternalWorksAsync(new ExternalWorkBatchData(list, works, false), ct)).ToDictionary(work => work.Value, StringComparer.Ordinal);
                related.AddRange(group.Where(target => saved.ContainsKey(target.Value)).Select(target => new WorkRelationTargetWorkData(saved[target.Value].WorkId, target.WorkRelationTypeId)));
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkRelations" ("WorkId", "RelatedWorkId", "WorkRelationTypeId", "SourceKey")
                SELECT DISTINCT
                    @WorkId,
                    item."WorkId",
                    item."WorkRelationTypeId",
                    @SourceKey
                FROM
                    jsonb_to_recordset(@Related::jsonb) AS item ("WorkId" bigint, "WorkRelationTypeId" smallint)
                WHERE
                    item."WorkId" <> @WorkId
                ON CONFLICT ("WorkId", "RelatedWorkId", "WorkRelationTypeId") WHERE "RelatedWorkId" IS NOT NULL DO NOTHING;

                DELETE FROM
                    "WorkRelations" AS relation
                WHERE
                    relation."WorkId" = @WorkId
                    AND relation."SourceKey" = @SourceKey
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            jsonb_to_recordset(@Related::jsonb) AS item ("WorkId" bigint, "WorkRelationTypeId" smallint)
                        WHERE
                            item."WorkId" = relation."RelatedWorkId"
                            AND item."WorkRelationTypeId" = relation."WorkRelationTypeId"
                    );
                """;
            sql.Parameters.Add(new WorkRelationSaveData(data.WorkId, data.SourceKey, related));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // A person relates a Work to another Work or to a title of Discover's catalog. Relating two Works is stored once whichever side states it; the same relation twice,
        // a Work to itself and an unknown title are refused, and a hidden relation of a provider comes back as the person's own.
        public static async Task AddWorkRelationAsync(this LogicContext ctx, WorkRelationAddData data, CancellationToken ct)
        {
            var hasExternal = data.Namespace is not null && data.ExternalId is not null;
            if (data.RelatedWorkId is null == !hasExternal)
            {
                throw new DataValidationException(nameof(data.RelatedWorkId), "invalid_value");
            }

            // The Work is locked while its relations change.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id"
                FROM
                    "Works" AS work
                WHERE
                    work."Id" = @WorkId
                FOR SHARE;
                """;
            sql.Parameters.Add(data);
            _ = await ctx.ReadOptionalAsync<WorkRelationWorkRow>(sql, ct) ?? throw new KeyNotFoundException("The work was not found.");

            // The related title is a Work; a catalog title is its anime season's series or the Work that holds its identity.
            sql = new SqlStatement();
            sql.Statement = data.RelatedWorkId is not null
                ? """
                  SELECT
                      work."Id" AS "RelatedWorkId"
                  FROM
                      "Works" AS work
                  WHERE
                      work."Id" = @RelatedWorkId
                  FOR SHARE;
                  """
                : """
                  SELECT
                      work."Id" AS "RelatedWorkId"
                  FROM
                      "Works" AS work
                  WHERE
                      work."Id" = coalesce(
                          (
                              SELECT
                                  unit."WorkId"
                              FROM
                                  "WorkUnitExternalIdentities" AS seasonIdentity
                                  JOIN "WorkUnits" AS unit ON unit."Id" = seasonIdentity."WorkUnitId"
                              WHERE
                                  seasonIdentity."Namespace" = @Namespace
                                  AND seasonIdentity."Value" = @ExternalId
                          ),
                          (SELECT identity."WorkId" FROM "WorkExternalIdentities" AS identity WHERE identity."Namespace" = @Namespace AND identity."Value" = @ExternalId)
                      )
                  FOR SHARE;
                  """;
            sql.Parameters.Add(data);
            var target = await ctx.ReadOptionalAsync<WorkRelationTargetRow>(sql, ct) ?? throw new DataValidationException(nameof(data.RelatedWorkId), "unknown_reference");
            if (target.RelatedWorkId == data.WorkId)
            {
                throw new DataValidationException(nameof(data.RelatedWorkId), "invalid_value");
            }

            // The relation is stored forward from the side that names it.
            var turned = !IsStoredForward(data.WorkRelationTypeId);
            var insert = new WorkRelationInsertData(data.WorkId, data.WorkRelationTypeId, target.RelatedWorkId, turned ? target.RelatedWorkId : data.WorkId,
                turned ? data.WorkId : target.RelatedWorkId, turned ? Inverse(data.WorkRelationTypeId) : data.WorkRelationTypeId);

            // A hidden relation of a provider is the person's now.
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WorkRelations" AS relation
                SET
                    "IsSuppressed" = FALSE,
                    "SourceKey" = 'manual'
                FROM
                    "WorkRelationLinks" AS link
                WHERE
                    link."RelationId" = relation."Id"
                    AND link."WorkId" = @WorkId
                    AND link."WorkRelationTypeId" = @WorkRelationTypeId
                    AND link."OtherWorkId" = @RelatedWorkId
                    AND link."IsSuppressed";
                """;
            sql.Parameters.Add(insert);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) > 0)
            {
                return;
            }

            // The same relation, seen from either side, is not stored twice.
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkRelations" ("WorkId", "RelatedWorkId", "WorkRelationTypeId", "SourceKey")
                SELECT
                    @StoredWorkId,
                    @StoredRelatedWorkId,
                    @StoredWorkRelationTypeId,
                    'manual'
                WHERE
                    NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "WorkRelationLinks" AS link
                        WHERE
                            link."WorkId" = @WorkId
                            AND link."WorkRelationTypeId" = @WorkRelationTypeId
                            AND link."OtherWorkId" = @RelatedWorkId
                    );
                """;
            sql.Parameters.Add(insert);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.RelatedWorkId), "duplicate_value");
            }
        }

        // The relation goes from this Work's view, whoever named it: a person's own rows are deleted, the rows of providers (also the one the other Work's provider named) stay as hidden
        // rows, so the next sync does not bring them back.
        public static async Task RemoveWorkRelationAsync(this LogicContext ctx, WorkRelationRemoveData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WorkRelations" AS relation
                SET
                    "IsSuppressed" = TRUE
                WHERE
                    relation."SourceKey" <> 'manual'
                    AND relation."Id" IN (
                        SELECT
                            same."RelationId"
                        FROM
                            "WorkRelationLinks" AS chosen
                            JOIN "WorkRelationLinks" AS same ON same."WorkId" = chosen."WorkId" AND same."WorkRelationTypeId" = chosen."WorkRelationTypeId"
                                AND same."OtherWorkId" = chosen."OtherWorkId"
                        WHERE
                            chosen."RelationId" = @WorkRelationId
                            AND chosen."WorkId" = @WorkId
                    );
                """;
            sql.Parameters.Add(data);
            var hidden = await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "WorkRelations" AS relation
                WHERE
                    relation."SourceKey" = 'manual'
                    AND relation."Id" IN (
                        SELECT
                            same."RelationId"
                        FROM
                            "WorkRelationLinks" AS chosen
                            JOIN "WorkRelationLinks" AS same ON same."WorkId" = chosen."WorkId" AND same."WorkRelationTypeId" = chosen."WorkRelationTypeId"
                                AND same."OtherWorkId" = chosen."OtherWorkId"
                        WHERE
                            chosen."RelationId" = @WorkRelationId
                            AND chosen."WorkId" = @WorkId
                    );
                """;
            sql.Parameters.Add(data);
            if (hidden + await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 0)
            {
                throw new KeyNotFoundException("The relation was not found.");
            }
        }
    }
}
