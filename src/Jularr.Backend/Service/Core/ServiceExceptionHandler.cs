using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.Core
{
    internal sealed class ServiceExceptionHandler(ILogger<ServiceExceptionHandler> logger) : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            {
                context.Response.StatusCode = 499;
                return true;
            }

            // One error contract; only administrators read an unknown error's message.
            var error = ServiceError.Describe(exception, context, logger);
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
                    [field] = [error.FieldResourceKey ?? error.ResourceKey]
                };
            }

            await Results.Problem(statusCode: error.StatusCode, title: error.Code, detail: error.Detail, extensions: extensions).ExecuteAsync(context);
            return true;
        }
    }
}
