using Jularr.Backend.Data.Logic.Notification;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Notification
{
    // Notices go only to profiles that have not muted their type, and only while the module is on.
    internal static class NotificationLogic
    {
        // The requesting profile hears about someone else's decision.
        public static async Task NotifyRequestAsync(this LogicContext ctx, RequestNotificationData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "Notifications" ("ProfileId", "NotificationTypeId", "WorkId", "Subject", "Link")
                SELECT
                    request."RequestedByProfileId",
                    @NotificationTypeId,
                    work."Id",
                    work."CanonicalTitle",
                    '/Details/' || work."Id"
                FROM
                    "AcquisitionRequests" AS request
                    JOIN "Works" AS work ON work."Id" = request."WorkId"
                    JOIN "InstanceModules" AS module ON module."Key" = 'Notifications' AND module."IsEnabled"
                WHERE
                    request."Id" = @AcquisitionRequestId
                    AND request."RequestedByProfileId" IS NOT NULL
                    AND request."RequestedByAccountId" IS DISTINCT FROM @ActorAccountId
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "MutedNotificationTypes" AS muted
                        WHERE
                            muted."ProfileId" = request."RequestedByProfileId"
                            AND muted."NotificationTypeId" = @NotificationTypeId
                    )
                ON CONFLICT ("ProfileId", "NotificationTypeId", (coalesce("WorkId", 0)), "Subject") WHERE "ReadAt" IS NULL DO UPDATE
                SET
                    "Occurrences" = "Notifications"."Occurrences" + 1,
                    "CreatedAt" = now();
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // Everyone whose open request waits for this target.
        public static async Task NotifyRequestersAsync(this LogicContext ctx, WantedNotificationData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "Notifications" ("ProfileId", "NotificationTypeId", "WorkId", "Subject", "Link")
                SELECT DISTINCT
                    request."RequestedByProfileId",
                    @NotificationTypeId,
                    work."Id",
                    work."CanonicalTitle",
                    '/Details/' || work."Id"
                FROM
                    "RequestTargets" AS target
                    JOIN "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId"
                    JOIN "Works" AS work ON work."Id" = request."WorkId"
                    JOIN "InstanceModules" AS module ON module."Key" = 'Notifications' AND module."IsEnabled"
                WHERE
                    target."WantedItemId" = @WantedItemId
                    AND request."RequestedByProfileId" IS NOT NULL
                    AND request."AcquisitionRequestStatusTypeId" NOT IN (7, 9)
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "MutedNotificationTypes" AS muted
                        WHERE
                            muted."ProfileId" = request."RequestedByProfileId"
                            AND muted."NotificationTypeId" = @NotificationTypeId
                    )
                ON CONFLICT ("ProfileId", "NotificationTypeId", (coalesce("WorkId", 0)), "Subject") WHERE "ReadAt" IS NULL DO UPDATE
                SET
                    "Occurrences" = "Notifications"."Occurrences" + 1,
                    "CreatedAt" = now();
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // Owners, and for media problems also media managers, through their own profiles.
        public static async Task NotifyStaffAsync(this LogicContext ctx, StaffNotificationData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "Notifications" ("ProfileId", "NotificationTypeId", "WorkId", "Subject", "Link")
                SELECT DISTINCT
                    profile."Id",
                    @NotificationTypeId,
                    @WorkId,
                    @Subject,
                    @Link
                FROM
                    "Accounts" AS account
                    JOIN "Profiles" AS profile ON profile."OwnerAccountId" = account."Id"
                    JOIN "InstanceModules" AS module ON module."Key" = 'Notifications' AND module."IsEnabled"
                WHERE
                    account."IsEnabled"
                    AND profile."IsEnabled"
                    AND (account."AccountRoleTypeId" = 1 OR NOT @OwnersOnly AND account."AccountRoleTypeId" = 3)
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "MutedNotificationTypes" AS muted
                        WHERE
                            muted."ProfileId" = profile."Id"
                            AND muted."NotificationTypeId" = @NotificationTypeId
                    )
                ON CONFLICT ("ProfileId", "NotificationTypeId", (coalesce("WorkId", 0)), "Subject") WHERE "ReadAt" IS NULL DO UPDATE
                SET
                    "Occurrences" = "Notifications"."Occurrences" + 1,
                    "CreatedAt" = now();
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task MarkNotificationsReadAsync(this LogicContext ctx, ProfileParameters data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Notifications"
                SET
                    "ReadAt" = now()
                WHERE
                    "ProfileId" = @ProfileId
                    AND "ReadAt" IS NULL;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task MuteNotificationTypeAsync(this LogicContext ctx, NotificationMuteData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement = data.IsMuted
                ? """
                  INSERT INTO
                      "MutedNotificationTypes" ("ProfileId", "NotificationTypeId")
                  VALUES
                      (@ProfileId, @NotificationTypeId)
                  ON CONFLICT DO NOTHING;
                  """
                : """
                  DELETE FROM
                      "MutedNotificationTypes"
                  WHERE
                      "ProfileId" = @ProfileId
                      AND "NotificationTypeId" = @NotificationTypeId;
                  """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
