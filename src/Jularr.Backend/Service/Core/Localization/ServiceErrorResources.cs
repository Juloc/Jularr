using Jularr.Backend.Data.Common;

namespace Jularr.Backend.Service.Core.Localization
{
    public static class ServiceErrorResources
    {
        public static string GetKey(string code) => "errors." + code;

        internal static IReadOnlyList<UiTextResource> English { get; } =
        [
            new("errors.invalid_data", "Please check the supplied values."),
            new("errors.authentication_required", "Please sign in to continue."),
            new("errors.permission_denied", "You do not have permission to perform this action."),
            new("errors.not_found", "The requested resource was not found."),
            new("errors.invalid_antiforgery_token", "Please reload the page and try again."),
            new("errors.invalid_request", "The request could not be processed."),
            new("errors.provider_unavailable", "The external provider is currently unavailable."),
            new("errors.concurrent_change", "Another change was made at the same time. Please reload and try again."),
            new("errors.internal_error", "An unexpected error occurred."),
            new("errors.rate_limit_exceeded", "Too many requests. Please try again later."),
            new("errors.validation.required", "A value is required."),
            new("errors.validation.invalid_type", "The value has an invalid type."),
            new("errors.validation.out_of_range", "The value is outside the allowed range."),
            new("errors.validation.invalid_uuid", "The identifier is invalid."),
            new("errors.validation.invalid_text", "The text contains invalid characters."),
            new("errors.validation.too_short", "The value is too short."),
            new("errors.validation.password_mismatch", "The passwords do not match."),
            new("errors.validation.too_long", "The value is too long."),
            new("errors.validation.invalid_email", "Please enter a valid email address."),
            new("errors.validation.invalid_locale", "Please enter a language code supported by this server."),
            new("errors.validation.duplicate_value", "This value is already in use."),
            new("errors.validation.utc_required", "The timestamp must use UTC."),
            new("errors.validation.unknown_catalog_value", "The selection is invalid."),
            new("errors.validation.unknown_reference", "The referenced resource is unavailable."),
            new("errors.validation.outdated_version", "The data has changed. Please reload and try again."),
            new("errors.validation.invalid_transition", "This change is not available."),
            new("errors.validation.invalid_placeholders", "The translation must preserve its placeholders."),
            new("errors.validation.unknown_id", "The referenced item is unavailable."),
            new("errors.validation.duplicate_id", "The item was supplied more than once."),
            new("errors.validation.unknown_or_duplicate_id", "The item is unavailable or was supplied more than once.")
        ];
    }
}
