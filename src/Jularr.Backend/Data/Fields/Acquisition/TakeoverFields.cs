using Jularr.Backend.Data.Enums.Acquisition;

namespace Jularr.Backend.Data.Fields.Acquisition
{
    public static class TakeoverFields
    {
        public static readonly EnumField TakeoverSourceTypeId = new(typeof(TakeoverSourceType));
        public static readonly EnumField TakeoverItemTypeId = new(typeof(TakeoverItemType));
        public static readonly BoolField Apply = new();
        public static readonly StringField Name = new() { MaximumLength = 500 };
        public static readonly StringField OutcomeKey = new() { MaximumLength = 64 };
    }
}
