using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Notification;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Notification;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Notification.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Notification;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Notification.V1
{
    [ServiceEndpoint("GET", "/api/v1/notifications")]
    public sealed class ReadNotificationsV1 : UserService<ReadNotificationsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<NotificationV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadNotificationsParametersV1 parameters) => ModuleRequirement.All("Notifications");
        protected override ServicePermission GetPermission(ReadNotificationsParametersV1 parameters, Type resultType) => new("profile.notifications");
        protected override PageRequest GetPageRequest(ReadNotificationsParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    notification."Id",
                    notification."NotificationTypeId",
                    notification."Subject",
                    notification."Link",
                    notification."Occurrences",
                    notification."CreatedAt",
                    notification."ReadAt" IS NOT NULL AS "IsRead"
                FROM
                    "Notifications" AS notification
                WHERE
                    notification."ProfileId" = @ProfileId
                ORDER BY
                    notification."CreatedAt" DESC,
                    notification."Id" DESC
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<NotificationV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/notifications/unread")]
    public sealed class ReadUnreadNotificationsV1 : UserService<NotificationsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UnreadNotificationsV1>()];
        protected override ModuleRequirement GetInstanceModules(NotificationsParametersV1 parameters) => ModuleRequirement.All("Notifications");
        protected override ServicePermission GetPermission(NotificationsParametersV1 parameters, Type resultType) => new("profile.notifications");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    count(*)::integer AS "UnreadCount"
                FROM
                    "Notifications" AS notification
                WHERE
                    notification."ProfileId" = @ProfileId
                    AND notification."ReadAt" IS NULL;
                """;
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return await Context.ReadSql.ReadRequiredAsync<UnreadNotificationsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/notifications/read")]
    public sealed class MarkNotificationsReadV1 : UserService<NotificationsParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(NotificationsParametersV1 parameters) => ModuleRequirement.All("Notifications");
        protected override ServicePermission GetPermission(NotificationsParametersV1 parameters, Type resultType) => new("profile.notifications");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.MarkNotificationsReadAsync(new ProfileParameters(Auth.ProfileId!.Value), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/notifications/settings")]
    public sealed class ReadNotificationSettingsV1 : UserService<NotificationsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<NotificationSettingV1>>()];
        protected override ModuleRequirement GetInstanceModules(NotificationsParametersV1 parameters) => ModuleRequirement.All("Notifications");
        protected override ServicePermission GetPermission(NotificationsParametersV1 parameters, Type resultType) => new("profile.notifications");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    type."Id" AS "NotificationTypeId",
                    muted."ProfileId" IS NOT NULL AS "IsMuted"
                FROM
                    "NotificationTypes" AS type
                    LEFT JOIN "MutedNotificationTypes" AS muted ON muted."NotificationTypeId" = type."Id" AND muted."ProfileId" = @ProfileId
                WHERE
                    (NOT type."IsStaffOnly" OR @AccountRoleTypeId IN (1, 3))
                    AND (NOT type."IsOwnerOnly" OR @AccountRoleTypeId = 1)
                ORDER BY
                    type."Id";
                """;
            sql.Parameters.Add(new NotificationAudienceData(Auth.ProfileId!.Value, Auth.AccountRoleTypeId));
            return new BaseDataList<NotificationSettingV1>(await Context.ReadSql.ReadListAsync<NotificationSettingV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/notifications/settings")]
    public sealed class UpdateNotificationSettingV1 : UserService<NotificationsParametersV1, UpdateNotificationSettingDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(NotificationsParametersV1 parameters) => ModuleRequirement.All("Notifications");
        protected override ServicePermission GetPermission(NotificationsParametersV1 parameters, Type resultType) => new("profile.notifications");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.MuteNotificationTypeAsync(new NotificationMuteData(Auth.ProfileId!.Value, Data.NotificationTypeId, Data.IsMuted), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
