using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Enums.Notification;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Notification;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Logic.Notification
{
    internal sealed class RequestNotificationData : BaseData
    {
        public RequestNotificationData(long acquisitionRequestId, NotificationType notificationTypeId, long actorAccountId)
        {
            AcquisitionRequestId = acquisitionRequestId;
            NotificationTypeId = notificationTypeId;
            ActorAccountId = actorAccountId;
        }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.Id))]
        public long AcquisitionRequestId { get; }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.NotificationTypeId))]
        public NotificationType NotificationTypeId { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long ActorAccountId { get; }
    }

    internal sealed class WantedNotificationData : BaseData
    {
        public WantedNotificationData(long wantedItemId, NotificationType notificationTypeId)
        {
            WantedItemId = wantedItemId;
            NotificationTypeId = notificationTypeId;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.NotificationTypeId))]
        public NotificationType NotificationTypeId { get; }
    }

    internal sealed class StaffNotificationData : BaseData
    {
        public StaffNotificationData(NotificationType notificationTypeId, long? workId, string subject, string? link)
        {
            NotificationTypeId = notificationTypeId;
            WorkId = workId;
            Subject = subject;
            Link = link;
        }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.NotificationTypeId))]
        public NotificationType NotificationTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.Subject))]
        public string Subject { get; }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.Link))]
        public string? Link { get; }
    }

    internal sealed class NotificationMuteData : BaseData
    {
        public NotificationMuteData(long profileId, NotificationType notificationTypeId, bool isMuted)
        {
            ProfileId = profileId;
            NotificationTypeId = notificationTypeId;
            IsMuted = isMuted;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.NotificationTypeId))]
        public NotificationType NotificationTypeId { get; }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.IsMuted))]
        public bool IsMuted { get; }
    }

    internal sealed class FailedWorkData : BaseData
    {
        public FailedWorkData(long workId, string title)
        {
            WorkId = workId;
            Title = title;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }
    }

    // The profile and its account's role decide which kinds it can switch.
    internal sealed class NotificationAudienceData : BaseData
    {
        public NotificationAudienceData(long profileId, AccountRoleType accountRoleTypeId)
        {
            ProfileId = profileId;
            AccountRoleTypeId = accountRoleTypeId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.AccountRoleTypeId))]
        public AccountRoleType AccountRoleTypeId { get; }
    }

    // A profile a notice is meant for, with what it is about.
    internal sealed class NotificationRecipientData : BaseData
    {
        public NotificationRecipientData(long profileId, long? workId, string subject)
        {
            ProfileId = profileId;
            WorkId = workId;
            Subject = subject;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.Subject))]
        public string Subject { get; }
    }

    internal sealed class NotificationBatchData : BaseData
    {
        public NotificationBatchData(NotificationType notificationTypeId, long[] profileIds, long? workId, string subject, string? link)
        {
            NotificationTypeId = notificationTypeId;
            ProfileIds = profileIds;
            WorkId = workId;
            Subject = subject;
            Link = link;
        }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.NotificationTypeId))]
        public NotificationType NotificationTypeId { get; }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.ProfileIds))]
        public long[] ProfileIds { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.Subject))]
        public string Subject { get; }

        [DataField(typeof(NotificationFields), nameof(NotificationFields.Link))]
        public string? Link { get; }
    }
}
