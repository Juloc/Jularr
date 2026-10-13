using NpgsqlTypes;
using Jularr.Backend.Data.Fields;

namespace Jularr.Backend.Data.Fields.Provider
{
    public static class ProviderFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Key = new();
        public static readonly BoolField IsEnabled = new();
        public static readonly ByteArrayField ProtectedSecret = new() { Required = false };
        public static readonly StringField BaseUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false, Trim = true, Format = Enums.Field.StringFormat.HttpUrl };
        public static readonly StringField ExternalId = new(NpgsqlDbType.Varchar) { MaximumLength = 128, Required = false, Trim = true };
        public static readonly DateTimeField SyncedAt = new() { Required = false };
    }

    // Plex Media Server contents linked to Works.
    public static class PlexFields
    {
        public static readonly StringField Items = new() { AllowBlank = false };
        public static readonly StringArrayField RatingKeys = new();
        public static readonly StringArrayField Sections = new();
        public static readonly IntField LinkedCount = new() { Minimum = 0 };
        public static readonly IntField CreatedCount = new() { Minimum = 0 };
        public static readonly IntField SkippedCount = new() { Minimum = 0 };
        public static readonly StringField SectionTitle = new() { MaximumLength = 200 };
        public static readonly StringField SectionKind = new() { MaximumLength = 16 };
        public static readonly StringField Version = new() { MaximumLength = 64, Required = false };
        public static readonly StringField Url = new() { MaximumLength = 1000, Required = false };
    }

    // Write-only credential input and its presence.
    public static class ProviderCredentialFields
    {
        public static readonly StringField Secret = new() { MaximumLength = 1024, Required = false, Trim = true, Format = Enums.Field.StringFormat.SingleLine };
        public static readonly BoolField HasSecret = new();
    }
}
