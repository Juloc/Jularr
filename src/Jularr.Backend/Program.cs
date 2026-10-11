using Jularr.Service.User.Profile.V1;
using Microsoft.AspNetCore.Authentication.Cookies;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration["JULARR_DB"]
    ?? throw new InvalidOperationException("Set JULARR_DB explicitly for this backend. No database is created or migrated automatically.");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
    });
builder.Services.AddProfileServices(new NpgsqlDataSourceBuilder(connectionString).Build());
var app = builder.Build();
app.UseAuthentication();
app.MapProfileEndpoints();
app.Run();
