using Jularr.Backend.Data.Enums.Field;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Speech
{
    public static class SpeechFields
    {
        public static readonly StringField Id = new() { MaximumLength = 128, AllowBlank = false };
        public static readonly StringField Name = new() { MaximumLength = 128, AllowBlank = false };
        public static readonly StringField Hash = new() { MaximumLength = 64, AllowBlank = false };
        public static readonly StringArrayField Hashes = new() { MaximumLength = 64, ItemMaximumLength = 64 };
        public static readonly StringField Language = new(NpgsqlDbType.Varchar) { MaximumLength = 64, AllowBlank = false, Format = StringFormat.Locale };
        public static readonly StringField Url = new() { MaximumLength = 2048, AllowBlank = false, Format = StringFormat.HttpUrl };
        public static readonly LongField SizeBytes = new() { Minimum = 1, Maximum = 536_870_912 };
        public static readonly BoolField IsEnabled = new();
        public static readonly LongField Version = new() { Minimum = 0 };
        public static readonly DecimalField Rate = new() { Minimum = .5m, Maximum = 2.5m, Scale = 2 };
        public static readonly DecimalField Pitch = new() { Minimum = .5m, Maximum = 2m, Scale = 2 };
        public static readonly DecimalField Volume = new() { Minimum = 0m, Maximum = 1m, Scale = 2 };
        public static readonly EnumField ProviderTypeId = new(typeof(Enums.Speech.SpeechProviderType));
        public static readonly StringField ModelId = new(NpgsqlDbType.Varchar) { MaximumLength = 128, Required = false, Trim = true };
        public static readonly StringField VoiceId = new(NpgsqlDbType.Varchar) { MaximumLength = 512, Required = false, Trim = true, Format = StringFormat.SingleLine };
    }
}
