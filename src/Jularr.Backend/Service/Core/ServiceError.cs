using System.Globalization;
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

        // When a client may try a 503 again unless the failure knows better.
        private static readonly TimeSpan RetrySoon = TimeSpan.FromSeconds(5);

        private ServiceError(int statusCode, string code, string? field = null, string? validationCode = null, TimeSpan? retryAfter = null)
        {
            StatusCode = statusCode;
            Code = code;
            Field = field;
            ValidationCode = validationCode;
            RetryAfter = statusCode == StatusCodes.Status503ServiceUnavailable ? retryAfter ?? RetrySoon : null;
        }

        public int StatusCode { get; }
        public string Code { get; }

        // Every 503 says when to try again: as long as the storage stays closed, else a few seconds.
        public TimeSpan? RetryAfter { get; }

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
                ? new ServiceError(error.StatusCode, error.Code, error.Field, error.ValidationCode, error.RetryAfter) { Detail = exception.GetType().Name + ": " + exception.Message }
                : error;
        }

        // The status of the answer, and for a 503 its Retry-After; any other status drops one an earlier failure of the same answer set.
        public void Answer(HttpResponse response)
        {
            response.StatusCode = StatusCode;
            if (RetryAfter is { } wait)
            {
                response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                response.Headers.Remove("Retry-After");
            }
        }

        // Set once the request's caller is known.
        internal static void ShowDetailTo(HttpContext context, bool administrator) => context.Items[DetailItem] = administrator;

        // One API/Razor classification.
        public static ServiceError FromException(Exception exception) => exception switch
        {
            DataValidationException validation => new(StatusCodes.Status400BadRequest, "invalid_data", validation.Field, validation.Code),
            InvalidCredentialsException credentials => new(StatusCodes.Status401Unauthorized, "invalid_credentials", credentials.Field),
            UnauthorizedAccessException => new(StatusCodes.Status401Unauthorized, "authentication_required"),
            SecurityException => new(StatusCodes.Status403Forbidden, "permission_denied"),
            KeyNotFoundException => new(StatusCodes.Status404NotFound, "not_found"),
            AntiforgeryValidationException => new(StatusCodes.Status400BadRequest, "invalid_antiforgery_token"),
            BadHttpRequestException request => new(request.StatusCode, "invalid_request"),
            ProviderUnavailableException => new(StatusCodes.Status503ServiceUnavailable, "provider_unavailable"),
            NasMountException storage => new(StatusCodes.Status503ServiceUnavailable, storage.Code, retryAfter: storage.RetryAfter),
            PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } => new(StatusCodes.Status409Conflict, "concurrent_change"),

            // A statement past its limit, a lock not granted in time or no free connection in the lane's pool.
            PostgresException { SqlState: PostgresErrorCodes.QueryCanceled or PostgresErrorCodes.LockNotAvailable } => new(StatusCodes.Status503ServiceUnavailable, "database_busy"),
            NpgsqlException { InnerException: TimeoutException } => new(StatusCodes.Status503ServiceUnavailable, "database_busy"),
            IOException => new(StatusCodes.Status503ServiceUnavailable, "storage_unavailable"),
            ServiceUnavailableException unavailable => new(StatusCodes.Status503ServiceUnavailable, unavailable.Code),
            ServiceGoneException gone => new(StatusCodes.Status410Gone, gone.Code),
            ArgumentException => new(StatusCodes.Status400BadRequest, "invalid_request"),
            _ => new(StatusCodes.Status500InternalServerError, "internal_error")
        };
    }
}
