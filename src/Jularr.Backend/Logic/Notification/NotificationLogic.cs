using Jularr.Backend.Data.Enums.Notification;
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
                SELECT
                    request."RequestedByProfileId" AS "ProfileId",
                    work."Id" AS "WorkId",
                    work."CanonicalTitle" AS "Subject"
                FROM
                    "AcquisitionRequests" AS request
                    JOIN "Works" AS work ON work."Id" = request."WorkId"
                WHERE
                    request."Id" = @AcquisitionRequestId
                    AND request."RequestedByProfileId" IS NOT NULL
                    AND request."RequestedByAccountId" IS DISTINCT FROM @ActorAccountId;
                """;
            sql.Parameters.Add(data);
            await ctx.NotifyWorkAsync(data.NotificationTypeId, await ctx.ReadSql.ReadListAsync<NotificationRecipientData>(sql, ct), ct);
        }

        // Everyone whose open request waits for this target.
        public static async Task NotifyRequestersAsync(this LogicContext ctx, WantedNotificationData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT DISTINCT
                    request."RequestedByProfileId" AS "ProfileId",
                    work."Id" AS "WorkId",
                    work."CanonicalTitle" AS "Subject"
                FROM
                    "RequestTargets" AS target
                    JOIN "WantedItems" AS wanted ON wanted."Id" = target."WantedItemId"
                    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                    JOIN "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId"
                WHERE
                    target."WantedItemId" = @WantedItemId
                    AND request."RequestedByProfileId" IS NOT NULL
                    -- Rejected (7) and cancelled (9) requests wait for nothing.
                    AND request."AcquisitionRequestStatusTypeId" NOT IN (7, 9);
                """;
            sql.Parameters.Add(data);
            await ctx.NotifyWorkAsync(data.NotificationTypeId, await ctx.ReadSql.ReadListAsync<NotificationRecipientData>(sql, ct), ct);
        }

        // Owners, and for staff types also media managers, through their own profiles.
        public static async Task NotifyStaffAsync(this LogicContext ctx, StaffNotificationData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id" AS "ProfileId"
                FROM
                    "Accounts" AS account
                    JOIN "Profiles" AS profile ON profile."OwnerAccountId" = account."Id"
                    JOIN "NotificationTypes" AS type ON type."Id" = @NotificationTypeId
                WHERE
                    account."IsEnabled"
                    AND (account."AccountRoleTypeId" = 1 OR NOT type."IsOwnerOnly" AND account."AccountRoleTypeId" = 3);
                """;
            sql.Parameters.Add(data);
            var recipients = await ctx.ReadSql.ReadListAsync<ProfileParameters>(sql, ct);
            await ctx.NotifyProfilesAsync(new NotificationBatchData(data.NotificationTypeId, [.. recipients.Select(recipient => recipient.ProfileId)], data.WorkId, data.Subject, data.Link), ct);
        }

        // Every recipient's row names the same Work.
        private static async Task NotifyWorkAsync(this LogicContext ctx, NotificationType type, IReadOnlyList<NotificationRecipientData> recipients, CancellationToken ct)
        {
            if (recipients.FirstOrDefault() is { } work)
            {
                await ctx.NotifyProfilesAsync(new NotificationBatchData(type, [.. recipients.Select(recipient => recipient.ProfileId)], work.WorkId, work.Subject, "/Details/" + work.WorkId), ct);
            }
        }

        // The one place for muting, the module switch and folding repeats into an unread notice.
        private static async Task NotifyProfilesAsync(this LogicContext ctx, NotificationBatchData data, CancellationToken ct)
        {
            if (data.ProfileIds.Length == 0)
            {
                return;
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "Notifications" ("ProfileId", "NotificationTypeId", "WorkId", "Subject", "Link")
                SELECT DISTINCT
                    recipient."ProfileId",
                    @NotificationTypeId,
                    @WorkId,
                    @Subject,
                    @Link
                FROM
                    unnest(@ProfileIds) AS recipient("ProfileId")
                    JOIN "InstanceModules" AS module ON module."Key" = 'Notifications' AND module."IsEnabled"
                WHERE
                    NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "MutedNotificationTypes" AS muted
                        WHERE
                            muted."ProfileId" = recipient."ProfileId"
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
