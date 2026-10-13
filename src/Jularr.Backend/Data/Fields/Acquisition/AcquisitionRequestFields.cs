using Jularr.Backend.Data.Enums.Acquisition;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Acquisition
{
    public static class AcquisitionRequestFields
    {
        public static readonly EnumField RequestTabTypeId = new(typeof(RequestTabType));
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly EnumField AcquisitionRequestStatusTypeId = new(typeof(AcquisitionRequestStatusType));
        public static readonly LongField Version = new() { Minimum = 1 };
        public static readonly DateTimeField CreatedAt = new();
        public static readonly BoolField IsGame = new();
        public static readonly BoolField IsPlatformListed = new();
    }

    public static class WantedItemFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly BoolField IsPaused = new();
        public static readonly LongField Version = new() { Minimum = 1 };
        public static readonly StringArrayField RequestedLanguages = new();
        public static readonly IntField SearchCount = new() { Minimum = 0 };
        public static readonly DateTimeField LastSearchedAt = new() { Required = false };
        public static readonly DateTimeField NextSearchAt = new() { Required = false };
        public static readonly StringField OutcomeKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly BoolField HasMedia = new();
        public static readonly BoolField IsDownloading = new();
        public static readonly BoolField IsAvailable = new();
        public static readonly BoolField IsCovered = new();
        public static readonly BoolField IsComplete = new();
    }
}
