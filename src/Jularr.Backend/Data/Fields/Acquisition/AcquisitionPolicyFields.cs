using Jularr.Backend.Data.Enums.Acquisition;

namespace Jularr.Backend.Data.Fields.Acquisition
{
    public static class CustomFormatFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Name = new() { MaximumLength = 128, AllowBlank = false, Trim = true };
        public static readonly BoolField IncludeWhenRenaming = new();
        public static readonly StringField Conditions = new() { MaximumLength = 131072, AllowBlank = false };
        public static readonly LongField Version = new() { Minimum = 1 };
    }

    public static class FormatConditionFields
    {
        public static readonly StringField Name = new() { MaximumLength = 128, AllowBlank = false, Trim = true };
        public static readonly EnumField Kind = new(typeof(FormatConditionKind));
        public static readonly BoolField Negate = new();
        public static readonly BoolField Required = new();
        public static readonly StringField Value = new() { MaximumLength = 2000, Required = false };
        public static readonly BoolField ExceptLanguage = new();
        public static readonly IntField Resolution = new() { Minimum = 0, Maximum = 4320, Required = false };
        public static readonly DecimalField Minimum = new() { Minimum = 0, Maximum = 1000000, Scale = 6, Required = false };
        public static readonly DecimalField Maximum = new() { Minimum = 0, Maximum = 1000000, Scale = 6, Required = false };
    }

    public static class FormatScoreFields
    {
        public static readonly LongArrayField FormatIds = new() { Minimum = 1, MaximumLength = 1000, IsSet = true };
        public static readonly IntField Score = new() { Minimum = -1000000, Maximum = 1000000 };
        public static readonly IntField MinimumUpgrade = new() { Minimum = 1, Maximum = 1000000 };
        public static readonly StringField ImportJson = new() { MaximumLength = 1048576, AllowBlank = false, Trim = true };
        public static readonly StringField ImportRows = new() { MaximumLength = 2097152, AllowBlank = false };
        public static readonly IntField SourceId = new() { Minimum = 1 };
        public static readonly IntField SourceMetadataProfileId = new() { Minimum = 0 };
    }

    public static class AcquisitionPolicyFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Name = new() { MaximumLength = 128, AllowBlank = false, Trim = true };
        public static readonly LongField Version = new() { Minimum = 1 };
        public static readonly BoolField IsEnabled = new();
        public static readonly StringArrayField Terms = new() { MaximumLength = 100, ItemMaximumLength = 500 };
        public static readonly LongArrayField TagIds = new() { Minimum = 1, MaximumLength = 100, IsSet = true };
        public static readonly DecimalField MinimumSize = new() { Minimum = 0, Maximum = 999999, Scale = 2 };
        public static readonly DecimalField OptionalSize = new() { Minimum = 0, Maximum = 999999, Scale = 2, Required = false };
        public static readonly StringField Rows = new() { MaximumLength = 131072, AllowBlank = false };
        public static readonly StringField Quality = new() { MaximumLength = 64, AllowBlank = false, Trim = true };
        public static readonly StringField GroupName = new() { MaximumLength = 128, AllowBlank = false, Trim = true };
    }

    public static class AcquisitionTagFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Label = new() { MaximumLength = 64, AllowBlank = false, Trim = true };
        public static readonly LongField Version = new() { Minimum = 1 };
    }
}
