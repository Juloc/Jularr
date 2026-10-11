using Jularr.Data.Fields;

namespace Jularr.Data.Fields.Provider;

public static class ProviderFields
{
    public static readonly LongField Id = new() { Minimum = 1 };
    public static readonly StringField Key = new();
    public static readonly BoolField IsEnabled = new();
}
