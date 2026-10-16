using System.Security;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Core.Localization;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.Nas;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jularr.Backend.Service.Core
{
    public sealed class ServiceError
    {
        private const string DetailItem = "Jularr.ServiceErrorDetail";
        private const string LoggedItem = "Jularr.ServiceErrorLogged";

        private ServiceError(int statusCode, string code, string? field = null, string? validationCode = null)
        {
            StatusCode = statusCode;
            Code = code;
            Field = field;
            ValidationCode = validationCode;
        }

        public int StatusCode { get; }
        public string Code { get; }

        // A code without text reads as an unknown error.
        public bool IsDefined => ServiceErrorResources.IsDefined(ServiceErrorResources.GetKey(Code))
            && (ValidationCode is null || ServiceErrorResources.IsDefined(ServiceErrorResources.GetKey("validation." + ValidationCode)));
        public string ResourceKey => IsDefined ? ServiceErrorResources.GetKey(Code) : ServiceErrorResources.GetKey("internal_error");
        public string? Field { get; }
        public string? ValidationCode { get; }
        public string? FieldResourceKey => ValidationCode is not null && ServiceErrorResources.IsDefined(ServiceErrorResources.GetKey("validation." + ValidationCode))
            ? ServiceErrorResources.GetKey("validation." + ValidationCode)
            : null;

        // The original message of an unknown error, for an Owner or Media Manager only.
        public string? Detail { get; private init; }

        // Unknown errors are logged; administrators also read what happened.
        public static ServiceError Describe(Exception exception, HttpContext context, ILogger logger)
        {
            var error = FromException(exception);
            var logged = context.Items.TryGetValue(LoggedItem, out var previous) && ReferenceEquals(previous, exception);
            context.Items[LoggedItem] = exception;
            if (error.IsDefined && error.Code != "internal_error")
            {
                // A known error keeps the cause it wraps in the log.
                if (!logged && exception.InnerException is { } cause)
                {
                    logger.LogWarning(cause, "{Code} on {Path}.", error.Code, context.Request.Path);
                }

                return error;
            }

            if (!logged)
            {
                logger.LogError(exception, error.IsDefined ? "{Code} on {Path}." : "{Code} on {Path} has no error text.", error.Code, context.Request.Path);
            }

            return context.Items.TryGetValue(DetailItem, out var shows) && shows is true
                ? new ServiceError(error.StatusCode, error.Code, error.Field, error.ValidationCode) { Detail = exception.GetType().Name + ": " + exception.Message }
                : error;
        }

        // Set once the request's caller is known.
        internal static void ShowDetailTo(HttpContext context, bool administrator) => context.Items[DetailItem] = administrator;

        // One API/Razor classification.
        public static ServiceError FromException(Exception exception) => exception switch
        {
            DataValidationException validation => new(StatusCodes.Status400BadRequest, "invalid_data", validation.Field, validation.Code),
            UnauthorizedAccessException => new(StatusCodes.Status401Unauthorized, "authentication_required"),
            SecurityException => new(StatusCodes.Status403Forbidden, "permission_denied"),
            KeyNotFoundException => new(StatusCodes.Status404NotFound, "not_found"),
            AntiforgeryValidationException => new(StatusCodes.Status400BadRequest, "invalid_antiforgery_token"),
            BadHttpRequestException request => new(request.StatusCode, "invalid_request"),
            ProviderUnavailableException => new(StatusCodes.Status503ServiceUnavailable, "provider_unavailable"),
            NasMountException storage => new(StatusCodes.Status503ServiceUnavailable, storage.Code),
            PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } => new(StatusCodes.Status409Conflict, "concurrent_change"),
            IOException => new(StatusCodes.Status503ServiceUnavailable, "storage_unavailable"),
            ServiceUnavailableException unavailable => new(StatusCodes.Status503ServiceUnavailable, unavailable.Code),
            ArgumentException => new(StatusCodes.Status400BadRequest, "invalid_request"),
            _ => new(StatusCodes.Status500InternalServerError, "internal_error")
        };
    }
}
