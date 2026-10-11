using NpgsqlTypes;
using Jularr.Data.Fields;

namespace Jularr.Data.Fields.Localization;

public static class UiLocaleFields
{
    public static readonly LongField Id = new() { Minimum = 1 };
    public static readonly StringField Locale = new(NpgsqlDbType.Varchar) { MaximumLength = 32, AllowBlank = false };
    public static readonly StringField Name = new();
    public static readonly BoolField IsEnabled = new();
    public static readonly BoolField IsRightToLeft = new();
}
