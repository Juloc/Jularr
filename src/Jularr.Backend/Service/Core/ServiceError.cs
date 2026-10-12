using System.Security;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Core.Localization;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.Nas;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Jularr.Backend.Service.Core
{
    public sealed class ServiceError
    {
        private ServiceError(int statusCode, string code, string? field = null, string? validationCode = null)
        {
            StatusCode = statusCode;
            Code = code;
            Field = field;
            ValidationCode = validationCode;
        }

        public int StatusCode { get; }
        public string Code { get; }
        public string ResourceKey => ServiceErrorResources.GetKey(Code);
        public string? Field { get; }
        public string? ValidationCode { get; }
        public string? FieldResourceKey => ValidationCode is null ? null : ServiceErrorResources.GetKey("validation." + ValidationCode);

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
            NasMountException storage => new(storage.Code is "storage_busy" or "storage_configuration_changed" ? StatusCodes.Status409Conflict : StatusCodes.Status503ServiceUnavailable, storage.Code),
            PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } => new(StatusCodes.Status409Conflict, "concurrent_change"),
            ArgumentException => new(StatusCodes.Status400BadRequest, "invalid_request"),
            _ => new(StatusCodes.Status500InternalServerError, "internal_error")
        };
    }
}
