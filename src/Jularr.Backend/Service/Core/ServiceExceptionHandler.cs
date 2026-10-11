using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Jularr.Backend.Service.Core
{
    internal sealed class ServiceExceptionHandler : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            {
                context.Response.StatusCode = 499;
                return true;
            }

            // Share sanitized error contracts.
            var error = ServiceError.FromException(exception);
            var extensions = new Dictionary<string, object?>
            {
                ["code"] = error.Code,
                ["resourceKey"] = error.ResourceKey,
                ["traceId"] = context.TraceIdentifier
            };
            if (error.Field is { } field && error.ValidationCode is { } code)
            {
                extensions["errors"] = new Dictionary<string, string[]> { [field] = [code] };
                extensions["errorResourceKeys"] = new Dictionary<string, string[]>
                {
                    [field] = [error.FieldResourceKey!]
                };
            }

            await Results.Problem(statusCode: error.StatusCode, title: error.Code, extensions: extensions).ExecuteAsync(context);
            return true;
        }
    }
}
