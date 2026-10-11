using Jularr.Service.Core;
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
builder.Services.AddJularrServices(new NpgsqlDataSourceBuilder(connectionString).Build(), typeof(ServiceRuntime).Assembly);
var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages(async status =>
    await Results.Problem(statusCode: status.HttpContext.Response.StatusCode, title: "request_failed").ExecuteAsync(status.HttpContext));
app.UseAuthentication();
app.MapJularrServices();
app.Run();
