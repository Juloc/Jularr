namespace Jularr.Web.Features.ClientApi;

public static class ClientApiOfflinePackageEndpoints
{
    public static IEndpointRouteBuilder MapClientApiOfflinePackagesV1(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ClientApiContract.BasePath).RequireAuthorization();

        group.MapPost(ClientApiOfflinePackageRoutes.OptionsPath, async (
            ClientOfflinePackageOptionsRequest request,
            ClientApiOfflinePackageOptionsService service,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await service.GetOptionsAsync(request.Target, request.Intent, cancellationToken);
            httpContext.Response.Headers.CacheControl = "no-store";
            return ToHttpResult(result);
        });

        group.MapPost(ClientApiOfflinePackageRoutes.PreviewPath, async (
            ClientOfflinePackagePreviewRequest request,
            ClientApiOfflinePackageOptionsService service,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await service.PreviewAsync(request.Target, request.Intent, request.Selection, cancellationToken);
            httpContext.Response.Headers.CacheControl = "no-store";
            return ToHttpResult(result);
        });

        return endpoints;
    }

    private static IResult ToHttpResult<T>(ClientOfflinePackageQueryResult<T> result)
        where T : class =>
        result.Status switch
        {
            ClientOfflinePackageQueryStatus.Success when result.Value is not null => Results.Ok(result.Value),
            ClientOfflinePackageQueryStatus.Invalid => Results.BadRequest(new ClientErrorResponse(result.ErrorCode ?? "invalid_offline_request", result.ErrorMessage ?? "The Offline request is invalid.")),
            ClientOfflinePackageQueryStatus.NotFound => Results.NotFound(new ClientErrorResponse(result.ErrorCode ?? "offline_target_not_found", result.ErrorMessage ?? "The Offline target was not found.")),
            ClientOfflinePackageQueryStatus.Unavailable => Results.Conflict(new ClientErrorResponse(result.ErrorCode ?? "offline_not_supported", result.ErrorMessage ?? "The Offline target is not available.")),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
        };
}
