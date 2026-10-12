using Jularr.Backend.Data.Enums.Acquisition;

namespace Jularr.Backend.Data.Fields.Acquisition
{
    public static class AcquisitionRequestFields
    {
        public static readonly EnumField RequestTabTypeId = new(typeof(RequestTabType));
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly EnumField AcquisitionRequestStatusTypeId = new(typeof(AcquisitionRequestStatusType));
        public static readonly LongField Version = new() { Minimum = 1 };
        public static readonly DateTimeField CreatedAt = new();
    }

    public static class WantedItemFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly BoolField IsPaused = new();
        public static readonly LongField Version = new() { Minimum = 1 };
        public static readonly StringArrayField RequestedLanguages = new();
    }
}
