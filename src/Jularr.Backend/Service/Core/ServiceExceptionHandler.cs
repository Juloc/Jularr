using System.Security;
using Jularr.Data.Common;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics;

namespace Jularr.Service.Core;

public sealed class ServiceExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = 499;
            return true;
        }

        var (status, title) = exception switch
        {
            DataValidationException => (StatusCodes.Status400BadRequest, "invalid_data"),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "authentication_required"),
            SecurityException => (StatusCodes.Status403Forbidden, "permission_denied"),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "not_found"),
            AntiforgeryValidationException => (StatusCodes.Status400BadRequest, "invalid_antiforgery_token"),
            BadHttpRequestException badRequest => (badRequest.StatusCode, "invalid_request"),
            ArgumentException => (StatusCodes.Status400BadRequest, "invalid_request"),
            _ => (StatusCodes.Status500InternalServerError, "internal_error")
        };
        var extensions = new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier };
        if (exception is DataValidationException validation)
        {
            extensions["errors"] = new Dictionary<string, string[]> { [validation.Field] = [validation.Code] };
        }

        await Results.Problem(statusCode: status, title: title, extensions: extensions).ExecuteAsync(context);
        return true;
    }
}
