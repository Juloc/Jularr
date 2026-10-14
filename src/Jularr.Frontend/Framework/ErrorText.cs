using Jularr.Backend.Service.Core;

namespace Jularr.Frontend.Framework
{
    // The error's text, and for administrators the original message of an unknown one.
    public static class ErrorText
    {
        public static IUiComponent[] Of(ServiceError error) => error.Detail is { } detail
            ? [new Text(error.FieldResourceKey ?? error.ResourceKey), new ValueText(" " + detail)]
            : [new Text(error.FieldResourceKey ?? error.ResourceKey)];
    }
}
