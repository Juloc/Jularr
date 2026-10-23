using Jularr.Backend.Data.Enums.Notification;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Notification
{
    public static class NotificationFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly EnumField NotificationTypeId = new(typeof(NotificationType));
        public static readonly StringField Subject = new(NpgsqlDbType.Varchar) { MaximumLength = 500, AllowBlank = false };
        public static readonly StringField Link = new(NpgsqlDbType.Varchar) { MaximumLength = 512, Required = false };
        public static readonly IntField Occurrences = new() { Minimum = 1 };
        public static readonly DateTimeField CreatedAt = new();
        public static readonly BoolField IsRead = new();
        public static readonly BoolField IsMuted = new();
        public static readonly IntField UnreadCount = new() { Minimum = 0 };
        public static readonly LongArrayField ProfileIds = new() { Minimum = 1 };
    }
}
