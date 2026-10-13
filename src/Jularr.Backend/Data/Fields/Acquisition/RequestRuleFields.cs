using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Acquisition
{
    public static class RequestRuleFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Name = new(NpgsqlDbType.Varchar) { MaximumLength = 60, AllowBlank = false, Trim = true };
        public static readonly StringField Description = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false, Trim = true };
        public static readonly IntField RequestLimit = new() { Minimum = 1, Maximum = 10000, Required = false };
        public static readonly IntField PeriodDays = new() { Minimum = 1, Maximum = 365 };
        public static readonly BoolField IsAutomaticApproval = new();
        public static readonly StringArrayField MediaKeys = new() { MaximumLength = 50 };
        public static readonly LongArrayField AcquisitionProfileIds = new() { Minimum = 1, MaximumLength = 200 };
        public static readonly BoolField IsDefault = new();
        public static readonly LongField Version = new() { Minimum = 1 };
        public static readonly LongField AccountCount = new() { Minimum = 0 };
        public static readonly BoolField IsInherited = new();
        public static readonly BoolField IsOverridden = new();
        public static readonly BoolField IsNameTaken = new();
        public static readonly BoolField AreMediaKeysKnown = new();
        public static readonly BoolField AreProfilesKnown = new();
    }
}
