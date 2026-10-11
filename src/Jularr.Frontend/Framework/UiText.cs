using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Service.Public.Localization.V1;
using Microsoft.AspNetCore.Http;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Service.Public.Authentication.V1;

namespace Jularr.Frontend.Framework
{
    // One scoped, server-side localization snapshot. No per-label SQL or JS requests.
    public sealed class UiText(ReadUiLocalesV1 localesService, ReadUiTextsV1 textsService, ReadAuthenticationV1 authenticationService)
    {
        private ReadUiTextsResultV1? _catalog;

        public string Locale => Catalog.Locale;
        public bool IsRightToLeft => Catalog.IsRightToLeft;

        public string this[string key] => Catalog.Texts.TryGetValue(key, out var value)
            ? value
            : throw new InvalidOperationException($"The UI text resource '{key}' was not registered.");

        private ReadUiTextsResultV1 Catalog =>
            _catalog ?? throw new InvalidOperationException("The UI localization catalog was not initialized.");

        public async Task InitializeAsync(HttpContext context)
        {
            var token = context.RequestAborted;
            var locales = await localesService.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(
                new ReadUiLocalesParametersV1(), token);
            var authentication = await authenticationService.ExecuteAsync<AuthenticationResultV1>(cancellationToken: token);

            var requested = context.Request.GetTypedHeaders().AcceptLanguage?
                .Where(item => (item.Quality ?? 1d) > 0d)
                .OrderByDescending(item => item.Quality ?? 1d)
                .Select(item => item.Value.ToString())
                .Where(item => item.Length > 0)
                .ToArray() ?? [];

            // Profile preference precedes browser hints.
            var matched = locales.FirstOrDefault(item => item.Id == authentication.UiLocaleId);
            foreach (var language in authentication.UiLocaleId is null ? requested : [])
            {
                if (language == "*") continue;
                var candidate = language;
                while (candidate.Length > 0)
                {
                    matched = locales.FirstOrDefault(item =>
                        string.Equals(item.Locale, candidate, StringComparison.OrdinalIgnoreCase));
                    if (matched is not null) break;
                    var separator = candidate.LastIndexOf('-');
                    candidate = separator > 0 ? candidate[..separator] : "";
                }

                if (matched is not null) break;
            }

            matched ??= locales.FirstOrDefault(item =>
                string.Equals(item.Locale, "en", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The English UI locale is unavailable.");

            _catalog = await textsService.ExecuteAsync<ReadUiTextsResultV1>(
                new ReadUiTextsParametersV1(matched.Id), token);

            // Locale selection is data-driven. Culture formatting follows the resolved locale.
            var culture = CultureInfo.GetCultureInfo(_catalog.Locale);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
        }
    }
}
