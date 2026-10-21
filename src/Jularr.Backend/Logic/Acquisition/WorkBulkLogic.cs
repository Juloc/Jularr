using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    // The mass editor's changes to the targets of several titles: the rules are those of the title page, applied to a bounded set in one transaction with a report per title.
    internal static class WorkBulkLogic
    {
        // A title is monitored while its target is not paused; a game is monitored per platform on its own page.
        public static async Task<IReadOnlyList<WorkOutcomeData>> SetWorksMonitoringAsync(this LogicContext ctx, WorkMonitoringData data, CancellationToken ct)
        {
            // State
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id",
                    work."CanonicalTitle",
                    work."MediaTypeId",
                    targets."Targets",
                    targets."Matching",
                    TRUE AS "Fits"
                FROM
                    "Works" AS work
                    CROSS JOIN LATERAL (
                        SELECT
                            count(*)::integer AS "Targets",
                            (count(*) FILTER (WHERE target."IsPaused" = NOT @IsMonitored))::integer AS "Matching"
                        FROM
                            "WantedItems" AS target
                        WHERE
                            target."WorkId" = work."Id"
                    ) AS targets
                WHERE
                    work."Id" = ANY(@WorkIds)
                ORDER BY
                    work."Id";
                """;
            sql.Parameters.Add(data);
            var states = await ctx.ReadSql.ReadListAsync<WorkTargetStateData>(sql, ct);
            var outcomes = Report(data.WorkIds, states, state => state.MediaTypeId == MediaType.Game ? "per_platform" : state.Targets == 0 ? "not_wanted" : state.Matching == state.Targets ? "unchanged" : "done");

            // Change: a resumed target is searched in the next cycle, not after its old backoff.
            var changed = outcomes.Where(outcome => outcome.OutcomeKey == "done").Select(outcome => outcome.Id).ToArray();
            if (changed.Length == 0) return outcomes;
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WantedItems" AS wanted
                SET
                    "IsPaused" = NOT @IsMonitored,
                    "NextSearchAt" = CASE WHEN wanted."IsPaused" AND @IsMonitored THEN NULL ELSE wanted."NextSearchAt" END,
                    "UpdatedAt" = now(),
                    "Version" = wanted."Version" + 1
                WHERE
                    wanted."WorkId" = ANY(@WorkIds)
                    AND wanted."IsPaused" = @IsMonitored;
                """;
            sql.Parameters.Add(new WorkMonitoringData(changed, data.IsMonitored));
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) < changed.Length)
            {
                throw new InvalidOperationException("The monitoring of the titles was not saved.");
            }

            return outcomes;
        }

        // Every target of a title takes the profile when it suits the title's type; none lets the default of the type apply again.
        public static async Task<IReadOnlyList<WorkOutcomeData>> SetWorksProfileAsync(this LogicContext ctx, WorkTargetChoiceData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id",
                    work."CanonicalTitle",
                    work."MediaTypeId",
                    targets."Targets",
                    targets."Matching",
                    @AcquisitionProfileId::bigint IS NULL OR EXISTS (
                        SELECT
                            1
                        FROM
                            "AcquisitionProfiles" AS profile
                        WHERE
                            profile."Id" = @AcquisitionProfileId
                            AND profile."MediaTypeId" = work."MediaTypeId"
                            AND (profile."MediaSubtypeId" IS NULL OR profile."MediaSubtypeId" = work."MediaSubtypeId")
                    ) AS "Fits"
                FROM
                    "Works" AS work
                    CROSS JOIN LATERAL (
                        SELECT
                            count(*)::integer AS "Targets",
                            (count(*) FILTER (WHERE target."AcquisitionProfileId" IS NOT DISTINCT FROM @AcquisitionProfileId))::integer AS "Matching"
                        FROM
                            "WantedItems" AS target
                        WHERE
                            target."WorkId" = work."Id"
                    ) AS targets
                WHERE
                    work."Id" = ANY(@WorkIds)
                ORDER BY
                    work."Id";
                """;
            sql.Parameters.Add(data);
            var states = await ctx.ReadSql.ReadListAsync<WorkTargetStateData>(sql, ct);
            var outcomes = Report(data.WorkIds, states, state => state.Targets == 0 ? "not_wanted" : !state.Fits ? "profile_mismatch" : state.Matching == state.Targets ? "unchanged" : "done");
            var changed = outcomes.Where(outcome => outcome.OutcomeKey == "done").Select(outcome => outcome.Id).ToArray();
            if (changed.Length == 0) return outcomes;
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WantedItems" AS wanted
                SET
                    "AcquisitionProfileId" = @AcquisitionProfileId,
                    "UpdatedAt" = now(),
                    "Version" = wanted."Version" + 1
                WHERE
                    wanted."WorkId" = ANY(@WorkIds)
                    AND wanted."AcquisitionProfileId" IS DISTINCT FROM @AcquisitionProfileId;
                """;
            sql.Parameters.Add(new WorkTargetChoiceData(changed, data.AcquisitionProfileId, null));
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) < changed.Length)
            {
                throw new InvalidOperationException("The profile of the titles was not saved.");
            }

            return outcomes;
        }

        // The library new files go to; what is filed stays where it is.
        public static async Task<IReadOnlyList<WorkOutcomeData>> SetWorksLibraryAsync(this LogicContext ctx, WorkTargetChoiceData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id",
                    work."CanonicalTitle",
                    work."MediaTypeId",
                    targets."Targets",
                    targets."Matching",
                    @LibraryRootId::bigint IS NULL OR EXISTS (
                        SELECT
                            1
                        FROM
                            "LibraryRoots" AS root
                        WHERE
                            root."Id" = @LibraryRootId
                            AND root."MediaTypeId" = work."MediaTypeId"
                            AND (root."MediaSubtypeId" IS NULL OR root."MediaSubtypeId" = work."MediaSubtypeId")
                    ) AS "Fits"
                FROM
                    "Works" AS work
                    CROSS JOIN LATERAL (
                        SELECT
                            count(*)::integer AS "Targets",
                            (count(*) FILTER (WHERE target."LibraryRootId" IS NOT DISTINCT FROM @LibraryRootId))::integer AS "Matching"
                        FROM
                            "WantedItems" AS target
                        WHERE
                            target."WorkId" = work."Id"
                    ) AS targets
                WHERE
                    work."Id" = ANY(@WorkIds)
                ORDER BY
                    work."Id";
                """;
            sql.Parameters.Add(data);
            var states = await ctx.ReadSql.ReadListAsync<WorkTargetStateData>(sql, ct);
            var outcomes = Report(data.WorkIds, states, state => state.Targets == 0 ? "not_wanted" : !state.Fits ? "library_mismatch" : state.Matching == state.Targets ? "unchanged" : "done");
            var changed = outcomes.Where(outcome => outcome.OutcomeKey == "done").Select(outcome => outcome.Id).ToArray();
            if (changed.Length == 0) return outcomes;
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WantedItems" AS wanted
                SET
                    "LibraryRootId" = @LibraryRootId,
                    "UpdatedAt" = now(),
                    "Version" = wanted."Version" + 1
                WHERE
                    wanted."WorkId" = ANY(@WorkIds)
                    AND wanted."LibraryRootId" IS DISTINCT FROM @LibraryRootId;
                """;
            sql.Parameters.Add(new WorkTargetChoiceData(changed, null, data.LibraryRootId));
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) < changed.Length)
            {
                throw new InvalidOperationException("The library of the titles was not saved.");
            }

            return outcomes;
        }

        // The chosen titles with their first target, in the order of the request.
        public static async Task<IReadOnlyList<WorkSearchTargetData>> ReadWorkBulkTargetsAsync(this LogicContext ctx, WorkTargetChoiceData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id",
                    work."CanonicalTitle",
                    work."MediaTypeId",
                    (SELECT target."Id" FROM "WantedItems" AS target WHERE target."WorkId" = work."Id" ORDER BY target."Id" LIMIT 1) AS "WantedItemId"
                FROM
                    "Works" AS work
                WHERE
                    work."Id" = ANY(@WorkIds)
                ORDER BY
                    work."Id";
                """;
            sql.Parameters.Add(data);
            return await ctx.ReadSql.ReadListAsync<WorkSearchTargetData>(sql, ct);
        }

        // One outcome for every title that was asked for, in the order of the request; a title that does not exist is reported, not skipped.
        private static List<WorkOutcomeData> Report(long[] workIds, IReadOnlyList<WorkTargetStateData> states, Func<WorkTargetStateData, string> decide)
        {
            var byId = states.ToDictionary(state => state.Id);
            return [.. workIds.Select(id => byId.TryGetValue(id, out var state) ? new WorkOutcomeData(id, state.CanonicalTitle, decide(state)) : new WorkOutcomeData(id, "#" + id, "not_found"))];
        }
    }
}
