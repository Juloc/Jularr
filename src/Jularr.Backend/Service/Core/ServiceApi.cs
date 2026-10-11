using Jularr.Data.Enums.Service;
using System.Reflection;
using Jularr.Data.Common;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Jularr.Service.Core;

public static class ServiceApi
{
    public static IServiceCollection AddJularrServices(this IServiceCollection services, NpgsqlDataSource database, params Assembly[] assemblies)
    {
        var serviceTypes = assemblies.Distinct().SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsVisible && !type.IsAbstract && !type.ContainsGenericParameters && typeof(Service).IsAssignableFrom(type))
            .Distinct().ToArray();
        services.AddSingleton(database);
        services.AddSingleton(new ServiceCatalog(serviceTypes));
        services.AddHttpContextAccessor();
        services.AddAntiforgery();
        services.AddProblemDetails();
        services.AddExceptionHandler<ServiceExceptionHandler>();
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddScoped<IServiceGate, ServiceGate>();
        services.AddScoped<ServiceRuntime>();
        foreach (var type in serviceTypes)
        {
            services.AddScoped(type, provider =>
            {
                var service = (Service)ActivatorUtilities.CreateInstance(provider, type);
                service.BindRuntime(provider.GetRequiredService<ServiceRuntime>());
                return service;
            });
        }

        return services;
    }

    public static IEndpointRouteBuilder MapJularrServices(this IEndpointRouteBuilder endpoints)
    {
        var catalog = endpoints.ServiceProvider.GetRequiredService<ServiceCatalog>();
        using var scope = endpoints.ServiceProvider.CreateScope();
        var routes = new HashSet<(string Method, string Route)>();
        foreach (var type in catalog.ServiceTypes)
        {
            var definition = (IServiceDefinition)scope.ServiceProvider.GetRequiredService(type);
            definition.ValidateDefinition();
            var endpoint = type.GetCustomAttribute<ServiceEndpointAttribute>();
            if (endpoint is null)
            {
                continue;
            }

            var method = definition.OperationType switch
            {
                ServiceOperationType.Read => "GET",
                ServiceOperationType.Create or ServiceOperationType.Execute => "POST",
                ServiceOperationType.Update => "PUT",
                ServiceOperationType.Delete => "DELETE",
                _ => throw new InvalidOperationException("Unknown service operation.")
            };
            if (definition.ServiceArea == ServiceArea.System || endpoint.Method != method
                || !endpoint.Route.StartsWith("/api/", StringComparison.Ordinal)
                || !routes.Add((method, endpoint.Route.ToUpperInvariant())))
            {
                throw new InvalidOperationException("Invalid or duplicate service endpoint declaration.");
            }

            var contract = type.BaseType;
            while (contract is not null && (!contract.IsGenericType || contract.GetGenericTypeDefinition() != typeof(Service<,>)))
            {
                contract = contract.BaseType;
            }

            var arguments = contract?.GetGenericArguments() ?? throw new InvalidOperationException("An API service must use the shared service base.");
            var noData = arguments[1] == typeof(NoData);
            if (!typeof(BaseData).IsAssignableFrom(arguments[0])
                || !noData && !typeof(BaseData).IsAssignableFrom(arguments[1])
                || !typeof(BaseData).IsAssignableFrom(definition.DefaultResultType))
            {
                throw new InvalidOperationException("API parameters, data and results must use BaseData contracts.");
            }

            if (definition.OperationType == ServiceOperationType.Read && !noData)
            {
                throw new InvalidOperationException("A read endpoint cannot require a request body.");
            }

            var factory = typeof(ServiceApi).GetMethod(noData ? nameof(CreateParameterHandler) : nameof(CreateBodyHandler), BindingFlags.Static | BindingFlags.NonPublic)!;
            var handler = (Delegate)factory.MakeGenericMethod([type, .. arguments]).Invoke(null, null)!;
            endpoints.MapMethods(endpoint.Route, [method], handler)
                .WithName(type.FullName!)
                .WithMetadata(endpoint)
                .Produces(StatusCodes.Status200OK, definition.DefaultResultType)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        return endpoints;
    }

    private static Delegate CreateParameterHandler<TService, TParameters, TData>() where TService : Service<TParameters, TData> =>
        async ([AsParameters] TParameters parameters, [FromServices] TService service, HttpContext http,
            [FromServices] IAntiforgery antiforgery, CancellationToken cancellationToken) =>
        {
            if (service.OperationType != ServiceOperationType.Read)
            {
                await antiforgery.ValidateRequestAsync(http);
            }

            var result = await service.ExecuteAsync(parameters, default!, cancellationToken);
            if (service.OperationType == ServiceOperationType.Read)
            {
                http.Response.Headers["RequestVerificationToken"] = antiforgery.GetAndStoreTokens(http).RequestToken;
            }

            return Results.Ok<object>(result);
        };

    private static Delegate CreateBodyHandler<TService, TParameters, TData>() where TService : Service<TParameters, TData> =>
        async ([AsParameters] TParameters parameters, [FromBody] TData data, [FromServices] TService service,
            HttpContext http, [FromServices] IAntiforgery antiforgery, CancellationToken cancellationToken) =>
        {
            await antiforgery.ValidateRequestAsync(http);
            return Results.Ok<object>(await service.ExecuteAsync(parameters, data, cancellationToken));
        };

    private sealed record ServiceCatalog(Type[] ServiceTypes);
}
