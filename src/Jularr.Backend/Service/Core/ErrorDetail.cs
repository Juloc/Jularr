using System.Text.RegularExpressions;
using Jularr.Backend.Data.Common;

namespace Jularr.Backend.Service.Core
{
    // The cause of a failure as a person can read it on the Jobs and Logs pages: the exception's type and message, never a stack trace or a secret.
    // A wrapper such as a time limit names what it wraps; addresses lose their query and sign-in, and anything that looks like a key, token or password is hidden.
    internal static partial class ErrorDetail
    {
        internal const int MaximumLength = 300;

        public static string? Of(Exception exception)
        {
            var cause = exception is ServiceUnavailableException { InnerException: { } inner } ? inner : exception;
            var message = cause.Message.ReplaceLineEndings(" ").Trim();
            message = UrlSecret().Replace(message, "$1");
            message = UrlQuery().Replace(message, "$1?…");
            message = NamedSecret().Replace(message, "$1=…");
            var detail = message.Length == 0 ? cause.GetType().Name : cause.GetType().Name + ": " + message;
            return detail.Length <= MaximumLength ? detail : detail[..(MaximumLength - 1)] + "…";
        }

        // user:password@ in an address.
        [GeneratedRegex(@"(\b[a-z][a-z0-9+.-]*://)[^/\s@]+@", RegexOptions.IgnoreCase)]
        private static partial Regex UrlSecret();

        // The query of an address, where API keys and signed tokens travel.
        [GeneratedRegex(@"(\b[a-z][a-z0-9+.-]*://[^\s?#""']+)\?[^\s""']*", RegexOptions.IgnoreCase)]
        private static partial Regex UrlQuery();

        // key=value, token: value, Password=... as in connection strings and headers.
        [GeneratedRegex(@"\b((?:api[_-]?key|apikey|access[_-]?token|token|secret|password|passwd|pwd|authorization|cookie|session|key)\s*)[=:]\s*[^\s;,&""']+", RegexOptions.IgnoreCase)]
        private static partial Regex NamedSecret();
    }
}
