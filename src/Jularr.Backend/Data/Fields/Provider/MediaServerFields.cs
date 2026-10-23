using Jularr.Backend.Data.Enums.Provider;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Provider
{
    // Jellyfin and Emby servers, their users and the progress agreed with them.
    public static class MediaServerFields
    {
        public static readonly EnumField MediaServerTypeId = new(typeof(MediaServerType));
        public static readonly StringField Name = new() { MaximumLength = 200 };
        public static readonly StringField Version = new() { MaximumLength = 64, Required = false };
        public static readonly IntField Count = new() { Minimum = 0 };
        public static readonly LongField LinkId = new() { Minimum = 1 };
        public static readonly StringField ExternalUserId = new(NpgsqlDbType.Varchar) { MaximumLength = 64 };
        public static readonly StringField ExternalName = new(NpgsqlDbType.Varchar) { MaximumLength = 200 };
        public static readonly LongField ProfileId = new() { Minimum = 1, Required = false };
        public static readonly StringField ProfileName = new() { Required = false };
        public static readonly BoolField IsConfirmed = new();
        public static readonly StringField ExternalItemId = new(NpgsqlDbType.Varchar) { MaximumLength = 64 };
        public static readonly DateTimeField ChangedAt = new() { Required = false };

        // JSON rows handed to one statement.
        public static readonly StringField Rows = new() { AllowBlank = false };
    }
}
