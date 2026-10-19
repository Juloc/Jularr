using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    // Titles known from a provider's catalog (ExternalWorks).
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
    }
}
