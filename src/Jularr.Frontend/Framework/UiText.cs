using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.Public.Instance.V1;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Data.User.Instance.V1;
using Jularr.Backend.Data.User.Notification.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Backend.Service.Public.Instance.V1;
using Jularr.Backend.Service.Public.Localization.V1;
using Jularr.Backend.Service.User.Instance.V1;
using Jularr.Backend.Service.User.Notification.V1;
using Jularr.Backend.Service.User.Profile.V1;
using Microsoft.AspNetCore.Http;
using System.Text.RegularExpressions;

namespace Jularr.Frontend.Framework
{
    // Scoped page presentation.
    public sealed class UiText(ReadUiTextsV1 textsService, ReadAuthenticationV1 authenticationService,
        ReadInstancePresentationV1 presentationService, ReadInstanceCapabilitiesV1 capabilitiesService, ReadUnreadNotificationsV1 unreadService, ReadProfileV1 profileService)
    {
        private ReadUiTextsResultV1? _catalog;
        private InstancePresentationDetailsV1? _presentation;

        public string Locale => Catalog.Locale;
        public bool IsRightToLeft => Catalog.IsRightToLeft;
        public string InstanceName => _presentation?.Name ?? throw new InvalidOperationException("The instance presentation was not initialized.");
        private InstanceCapabilitiesDetailsV1? _capabilities;

        // Unread notices for the shell's bell.
        public int UnreadNotifications { get; private set; }

        // The header's avatar: whether someone is signed in and the active profile's name, when there is one.
        public bool IsAuthenticated { get; private set; }
        public string? ProfileName { get; private set; }

        // Whether the profile lets a card that lingers on a touch device play its preview by itself.
        public bool AutoplayPreviews { get; private set; } = true;

        // Whether the profile keeps the screen on while a video plays or a book is read.
        public bool KeepScreenOn { get; private set; } = true;

        // A permission inside enabled modules, as the capability service reports them; nothing for a visitor.
        public bool Can(string? permission, params string[] modules) => _capabilities is { } capabilities
            && (permission is null || capabilities.Permissions.Contains(permission, StringComparer.Ordinal))
            && modules.All(module => capabilities.Modules.Contains(module, StringComparer.Ordinal));

        public bool Has(string key) => Catalog.Texts.ContainsKey(key);

        // A genre in the viewer's language: providers hand out their names in English or in the language they were asked for, so a known English name is translated here and any other stays as stored.
        public string Genre(string name) => Catalog.Texts.TryGetValue("genre." + Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-'), out var value) ? value : name;

        // The stored comma separated genre names, each in the viewer's language.
        public string? Genres(string? names) => names is null ? null : string.Join(", ", names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Genre));

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
                IsAuthenticated = true;
                _capabilities = await capabilitiesService.ExecuteAsync<InstanceCapabilitiesDetailsV1>(new ReadInstanceCapabilitiesParametersV1(), token);
                if (authentication.ProfileId is { } profileId && this.CanReadOwnProfile())
                {
                    var profile = await profileService.ExecuteAsync<ReadProfileResultV1>(new ReadProfileParametersV1(profileId), token);
                    ProfileName = profile.DisplayName;
                    AutoplayPreviews = profile.AutoplayPreviews;
                    KeepScreenOn = profile.KeepScreenOn;
                }

                if (this.CanNotify())
                {
                    UnreadNotifications = (await unreadService.ExecuteAsync<UnreadNotificationsV1>(new NotificationsParametersV1(), token)).UnreadCount;
                }
            }
        }
    }
}
