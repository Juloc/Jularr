using Jularr.Service.Core;
using Npgsql;

namespace Jularr.Service.User.Profile.V1;

public static class ProfileRegistration
{
    public static IServiceCollection AddProfileServices(this IServiceCollection services, NpgsqlDataSource database)
    {
        services.AddSingleton(database);
        services.AddHttpContextAccessor();
        services.AddAntiforgery();
        services.AddScoped<IServiceGate, ProfileServiceGate>();
        services.AddScoped<ServiceRuntime>();
        services.AddScoped<ReadProfileV1>();
        services.AddScoped<UpdateProfileV1>();
        return services;
    }
}
