using System.Globalization;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Service.Public.Localization.V1;
using Microsoft.AspNetCore.Http;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Backend.Data.Public.Instance.V1;
using Jularr.Backend.Data.User.Instance.V1;
using Jularr.Backend.Service.Public.Instance.V1;
using Jularr.Backend.Service.User.Instance.V1;

namespace Jularr.Frontend.Framework
{
    // Scoped page presentation.
    public sealed class UiText(ReadUiTextsV1 textsService, ReadAuthenticationV1 authenticationService,
        ReadInstancePresentationV1 presentationService, ReadInstanceCapabilitiesV1 capabilitiesService)
    {
        private ReadUiTextsResultV1? _catalog;
        private InstancePresentationDetailsV1? _presentation;

        public string Locale => Catalog.Locale;
        public bool IsRightToLeft => Catalog.IsRightToLeft;
        public string InstanceName => _presentation?.Name ?? throw new InvalidOperationException("The instance presentation was not initialized.");
        public InstanceCapabilitiesDetailsV1? Capabilities { get; private set; }

        public string this[string key] => Catalog.Texts.TryGetValue(key, out var value)
            ? value
            : throw new InvalidOperationException($"The UI text resource '{key}' was not registered.");

        private ReadUiTextsResultV1 Catalog =>
            _catalog ?? throw new InvalidOperationException("The UI localization catalog was not initialized.");

        public async Task InitializeAsync(HttpContext context)
        {
            var token = context.RequestAborted;
            var authentication = await authenticationService.ExecuteAsync<AuthenticationResultV1>(cancellationToken: token);
            _presentation = await presentationService.ExecuteAsync<InstancePresentationDetailsV1>(cancellationToken: token);

            // Profile overrides instance defaults.
            _catalog = await textsService.ExecuteAsync<ReadUiTextsResultV1>(
                new ReadUiTextsParametersV1(authentication.UiLocaleId ?? _presentation.UiLocaleId), token);

            // Resolve navigation availability once.
            if (authentication.IsAuthenticated)
            {
                Capabilities = await capabilitiesService.ExecuteAsync<InstanceCapabilitiesDetailsV1>(new ReadInstanceCapabilitiesParametersV1(), token);
            }

            // Apply resolved culture.
            var culture = CultureInfo.GetCultureInfo(_catalog.Locale);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
        }
    }
}
