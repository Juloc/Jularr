using System.Security;
using Jularr.Data.User.Profile.V1;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Service.User.Profile.V1;

public static class ProfileEndpoints
{
    public static IEndpointRouteBuilder MapProfileReferenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/profiles").AddEndpointFilter(async (invocation, next) =>
        {
            try
            {
                return await next(invocation);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "profile_access_denied");
            }
            catch (SecurityException)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "profile_permission_denied");
            }
            catch (KeyNotFoundException)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "profile_not_found");
            }
            catch (ArgumentException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_profile_input");
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_antiforgery_token");
            }
        });

        group.MapGet("/{publicId:guid}", async (Guid publicId, GetProfile service, HttpContext http, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
        {
            var result = await service.ExecuteAsync<ProfileDetails>(new ProfileParameters(publicId), cancellationToken);
            http.Response.Headers["RequestVerificationToken"] = antiforgery.GetAndStoreTokens(http).RequestToken;
            return Results.Ok(result);
        });

        group.MapPut("/{publicId:guid}/name", async (Guid publicId, [FromBody] RenameProfileData data, RenameProfile service, HttpContext http, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
        {
            await antiforgery.ValidateRequestAsync(http);
            return Results.Ok(await service.ExecuteAsync<ProfileDetails>(new ProfileParameters(publicId), data, cancellationToken));
        });

        return endpoints;
    }
}
