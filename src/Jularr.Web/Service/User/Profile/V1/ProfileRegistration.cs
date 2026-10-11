using Jularr.Service.Core;
using Npgsql;

namespace Jularr.Service.User.Profile.V1;

public static class ProfileRegistration
{
    public static IServiceCollection AddProfileReferenceServices(this IServiceCollection services, NpgsqlDataSource database)
    {
        services.AddSingleton(database);
        services.AddHttpContextAccessor();
        services.AddAntiforgery();
        services.AddScoped<IServiceGate, ProfileServiceGate>();
        services.AddScoped<ServiceRuntime>();
        services.AddScoped<GetProfile>();
        services.AddScoped<RenameProfile>();
        return services;
    }
}
