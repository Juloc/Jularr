using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.Core.Localization;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Localization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/ShellPreview");
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => options.LoginPath = "/Account/Login");
builder.Services.AddAuthorization();

var connectionString = builder.Configuration["JULARR_DB"]
    ?? throw new InvalidOperationException("JULARR_DB must be configured explicitly.");
builder.Services.AddJularrServices(
    new NpgsqlDataSourceBuilder(connectionString).Build(), typeof(ServiceRuntime).Assembly);
builder.Services.AddJularrLocalization(UiResources.English);
builder.Services.AddScoped<UiText>();
builder.Services.AddSingleton<ShellNavigation>();

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Resolve the full DB-backed language pack once per Razor request, after authorization.
// Static resources and API service requests do not pass through localization rendering.
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api"))
    {
        await context.RequestServices.GetRequiredService<UiText>()
            .InitializeAsync(context);
    }

    await next(context);
});

app.MapJularrServices();
app.MapRazorPages();
app.Run();

public partial class Program;
