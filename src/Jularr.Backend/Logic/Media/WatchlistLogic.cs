using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class WatchlistLogic
    {
        // One opt-in flow shared by local Work and external catalog favorites.
        // No implicit request for an existing favorite, disabled Requests module, or removing a favorite.
        // Executes in the caller's transaction so a request failure also rolls back the favorite.
        public static async Task<bool> UpdateFavoriteWithRequestAsync(this LogicContext ctx, WatchlistData data,
            bool isWatchlisted, long accountId, bool owner, CancellationToken ct, int? startSeasonNumber = null)
        {
            if (!isWatchlisted)
            {
                await ctx.UpdateWatchlistAsync(data, false, ct);
                return false;
            }

            var sql = new SqlStatement();
            sql.Statement = """
                SELECT coalesce((to_jsonb(profile)->>'AutoRequestOnFavorite')::boolean, FALSE) AS "AutoRequestOnFavorite", profile."UiLocaleId",
                    EXISTS (SELECT 1 FROM "WatchlistEntries" AS existing
                        WHERE existing."ProfileId" = profile."Id" AND existing."WorkId" = @WorkId) AS "AlreadyFavorite",
                    EXISTS (SELECT 1 FROM "InstanceModules" AS module
                        WHERE module."Key" = 'Requests' AND module."IsEnabled") AS "RequestsEnabled"
                FROM "Profiles" AS profile
                WHERE profile."Id" = @ProfileId
                FOR UPDATE OF profile;
                """;
            sql.Parameters.Add(data);
            var preference = await ctx.ReadRequiredAsync<FavoriteRequestPreference>(sql, ct);
            await ctx.UpdateWatchlistAsync(data, true, ct);
            if (!preference.AutoRequestOnFavorite || preference.AlreadyFavorite || !preference.RequestsEnabled)
                return false;

            // An AniList cour with no verified local TVDB season must never trigger a
            // whole-series automatic request as a fallback. The favorite still succeeds.
            if (startSeasonNumber is { } season)
            {
                sql = new SqlStatement();
                sql.Statement = """
                    SELECT EXISTS (
                        SELECT 1 FROM "WorkUnits"
                        WHERE "WorkId" = @WorkId AND "WorkUnitTypeId" = 1 AND "Number" = @Season
                    ) AS "IsValid";
                    """;
                sql.Parameters.Add(new { data.WorkId, Season = season });
                if (!(await ctx.ReadRequiredAsync<VerifiedSeason>(sql, ct)).IsValid) return false;
            }
            var (_, newlyApproved) = await ctx.CreateAcquisitionRequestAsync(new AcquisitionRequestCreationData(
                data.WorkId, data.ProfileId, accountId, preference.UiLocaleId,
                null, null, owner, startSeasonNumber), ct);
            return newlyApproved;
        }

        private sealed record FavoriteRequestPreference(bool AutoRequestOnFavorite, long UiLocaleId, bool AlreadyFavorite, bool RequestsEnabled);
        private sealed record VerifiedSeason(bool IsValid);

        public static async Task UpdateWatchlistAsync(this LogicContext ctx, WatchlistData data, bool isWatchlisted, CancellationToken ct)
        {
            // Only Works the profile may open.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id",
                    work."MediaTypeId",
                    work."MediaSubtypeId",
                    work."IsMediaSubtypeManual",
                    work."CanonicalTitle",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    work."Version"
                FROM
                    "ProfileWorks" AS visible
                    JOIN "Works" AS work ON work."Id" = visible."WorkId"
                WHERE
                    visible."ProfileId" = @ProfileId
                    AND work."Id" = @WorkId
                FOR SHARE OF work;
                """;
            sql.Parameters.Add(data);
            await ctx.ReadRequiredAsync<WorkData>(sql, ct);

            // Change personal state only.
            sql = new SqlStatement();
            if (isWatchlisted)
            {
                sql.Statement =
                    """
                    INSERT INTO "WatchlistEntries" (
                        "ProfileId",
                        "WorkId"
                    ) VALUES (
                        @ProfileId,
                        @WorkId
                    )
                    ON CONFLICT ("ProfileId", "WorkId") DO NOTHING;
                    """;
            }
            else
            {
                // A favorite that goes leaves every collection of the profile.
                var leaving = new SqlStatement();
                leaving.Statement =
                    """
                    DELETE FROM "CollectionWorks"
                    WHERE
                        "WorkId" = @WorkId
                        AND "CollectionId" IN (
                            SELECT
                                collection."Id"
                            FROM
                                "Collections" AS collection
                            WHERE
                                collection."ProfileId" = @ProfileId
                        );
                    """;
                leaving.Parameters.Add(data);
                await ctx.ExecuteAsyncForAffectedRows(leaving, ct);

                sql.Statement =
                    """
                    DELETE FROM "WatchlistEntries"
                    WHERE
                        "ProfileId" = @ProfileId
                        AND "WorkId" = @WorkId;
                    """;
            }

            sql.Parameters.Add(data);
            var affected = await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            if (affected is < 0 or > 1)
            {
                throw new InvalidOperationException("The personal list mutation affected unexpected rows.");
            }
        }
    }
}
