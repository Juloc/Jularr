using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Notification;
using Jularr.Backend.Data.Fields.Notification;

namespace Jularr.Backend.Data.User.Notification.V1
{
    public sealed class ReadNotificationsParametersV1 : BaseParameters
    {
        public ReadNotificationsParametersV1(int page = 1, int pageSize = 50)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class NotificationsParametersV1 : BaseParameters
    {
    }

    public sealed class NotificationV1 : BaseResponse
    {
        public NotificationV1(long id, NotificationType notificationTypeId, string subject, string? link, int occurrences, DateTime createdAt, bool isRead)
        {
            Id = id;
            NotificationTypeId = notificationTypeId;
            Subject = subject;
            Link = link;
            Occurrences = occurrences;
            CreatedAt = createdAt;
            IsRead = isRead;
        }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.Id))]
        public long Id { get; }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.NotificationTypeId))]
        public NotificationType NotificationTypeId { get; }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.Subject))]
        public string Subject { get; }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.Link))]
        public string? Link { get; }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.Occurrences))]
        public int Occurrences { get; }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.CreatedAt))]
        public DateTime CreatedAt { get; }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.IsRead))]
        public bool IsRead { get; }
    }

    public sealed class UnreadNotificationsV1 : BaseResponse
    {
        public UnreadNotificationsV1(int unreadCount)
        {
            UnreadCount = unreadCount;
        }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.UnreadCount))]
        public int UnreadCount { get; }
    }

    public sealed class NotificationSettingV1 : BaseResponse
    {
        public NotificationSettingV1(NotificationType notificationTypeId, bool isMuted)
        {
            NotificationTypeId = notificationTypeId;
            IsMuted = isMuted;
        }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.NotificationTypeId))]
        public NotificationType NotificationTypeId { get; }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.IsMuted))]
        public bool IsMuted { get; }
    }

    public sealed class UpdateNotificationSettingDataV1 : BaseData
    {
        public UpdateNotificationSettingDataV1(NotificationType notificationTypeId, bool isMuted)
        {
            NotificationTypeId = notificationTypeId;
            IsMuted = isMuted;
        }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.NotificationTypeId))]
        public NotificationType NotificationTypeId { get; }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.IsMuted))]
        public bool IsMuted { get; }
    }
}
