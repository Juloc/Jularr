using Jularr.Backend.Data.Enums.Field;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class WorkMetadataFields
    {
        public static readonly StringField Query = new() { MaximumLength = 200, AllowBlank = false, Trim = true, Format = StringFormat.SingleLine };
        public static readonly IntField TmdbId = new() { Minimum = 1 };
        public static readonly IntField TvdbId = new() { Minimum = 1 };
        public static readonly IntField AniListId = new() { Minimum = 1 };
        public static readonly StringField ExternalId = new() { MaximumLength = 64, AllowBlank = false, Format = Enums.Field.StringFormat.SingleLine };
        public static readonly StringArrayField ExternalIds = new() { ItemMaximumLength = 64, MaximumLength = 500 };
        public static readonly StringField Author = new() { MaximumLength = 4000, Required = false };
        public static readonly StringField OriginalTitle = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false };
        public static readonly StringField Overview = new() { MaximumLength = 4000, Required = false };
        public static readonly StringField ExternalIdentities = new() { AllowBlank = false };
        public static readonly StringField Units = new() { AllowBlank = false };
        public static readonly StringField Facts = new() { AllowBlank = false };
        public static readonly StringField Translations = new() { AllowBlank = false };
        public static readonly StringField OriginalLanguage = new(NpgsqlDbType.Varchar) { MaximumLength = 2, Required = false };
        public static readonly StringField People = new() { AllowBlank = false };
        public static readonly StringField Editions = new() { AllowBlank = false };
        public static readonly StringArrayField EditionTitles = new() { ItemMaximumLength = 500 };
        public static readonly StringArrayField Isbns = new() { ItemMaximumLength = 64 };
        public static readonly StringField PersonNamespace = new() { MaximumLength = 16, AllowBlank = false };
        public static readonly StringField Fact = new() { MaximumLength = 4000, Required = false };
        public static readonly StringField TrailerKey = new() { MaximumLength = 11, Required = false };
        public static readonly StringField Platforms = new() { AllowBlank = false };
        public static readonly StringField ImageUrl = new() { MaximumLength = 500, Required = false };
        public static readonly StringArrayField ExternalIdentityList = new() { ItemMaximumLength = 200 };
        public static readonly StringField Namespace = new(NpgsqlDbType.Varchar) { MaximumLength = 16, AllowBlank = false };
        public static readonly StringField Value = new(NpgsqlDbType.Varchar) { MaximumLength = 64, AllowBlank = false };
    }
}
