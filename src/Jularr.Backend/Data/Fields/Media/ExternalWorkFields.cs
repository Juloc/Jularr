using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    // Titles known from a provider's catalog, kept as catalog Works.
    public static class ExternalWorkFields
    {
        public static readonly StringField Namespace = new(NpgsqlDbType.Varchar) { AllowBlank = false, MaximumLength = 16 };
        public static readonly StringField Value = new(NpgsqlDbType.Varchar) { AllowBlank = false, MaximumLength = 64 };
        public static readonly StringField Title = new(NpgsqlDbType.Varchar) { AllowBlank = false, Trim = true, MaximumLength = 500 };
        public static readonly StringField OriginalTitle = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false };
        public static readonly StringField Overview = new(NpgsqlDbType.Varchar) { MaximumLength = 4000, Required = false };
        public static readonly StringField Creator = new(NpgsqlDbType.Varchar) { MaximumLength = 4000, Required = false };
        public static readonly StringField ImageUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false };
        public static readonly StringField BackdropUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false };
        public static readonly IntField TrendingRank = new() { Minimum = 1, Required = false };
        public static readonly DateTimeField FetchedAt = new();
        public static readonly DateField ListedOn = new() { Required = false };
        public static readonly DateTimeField ChangesReadAt = new() { Required = false };
        public static readonly IntField Limit = new() { Minimum = 1, Maximum = 1000 };
        public static readonly IntField Offset = new() { Minimum = 0 };
        // The place of a Discover shelf or scope in its request.
        public static readonly IntField Shelf = new() { Minimum = 0 };
        public static readonly IntField TopVotes = new() { Minimum = 0 };
        public static readonly DateField Day = new();
        public static readonly StringField Shelves = new() { AllowBlank = false };
        public static readonly StringField SearchTerms = new() { AllowBlank = false, MaximumLength = 1000 };
        public static readonly BoolField IsEnabled = new();
        public static readonly IntField RequestsPerMinute = new() { Minimum = 1, Maximum = 600 };
        public static readonly BoolField PausesWhileUsed = new();
        public static readonly IntField Cursor = new() { Minimum = 1, Required = false };
        public static readonly LongField Version = new() { Minimum = 1 };
        public static readonly TimeField StartsAt = new();
        public static readonly TimeField EndsAt = new();
        public static readonly LongField Entries = new() { Minimum = 0 };
        public static readonly LongField Pending = new() { Minimum = 0 };
    }
}
