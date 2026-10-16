using Jularr.Backend.Data.Public.Localization.V1;
using Microsoft.AspNetCore.Http;

namespace Jularr.Frontend.Framework
{
    // A new account's language: the browser's where the instance offers it, else the instance's.
    public static class LocalePreference
    {
        public static long Preferred(HttpRequest request, IReadOnlyList<UiLocaleDetailsV1> locales, long instanceLocaleId)
        {
            foreach (var language in request.GetTypedHeaders().AcceptLanguage.OrderByDescending(language => language.Quality ?? 1).Select(language => language.Value.Value ?? ""))
            {
                var match = locales.FirstOrDefault(locale => string.Equals(locale.Locale, language, StringComparison.OrdinalIgnoreCase))
                    ?? locales.FirstOrDefault(locale => string.Equals(locale.Locale.Split('-')[0], language.Split('-')[0], StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match.Id;
                }
            }

            return instanceLocaleId;
        }
    }
}
