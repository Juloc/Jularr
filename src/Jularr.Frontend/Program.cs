using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.Core.Localization;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Localization;
using Jularr.Frontend.Pages.Shared;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.RazorPages;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AuthorizeFolder("/Admin", ServiceApi.AdminPolicyName);
    options.Conventions.AuthorizePage("/Admin/Requests", ServiceApi.RequestsReadPolicyName);
    options.Conventions.AuthorizePage("/Admin/Wanted", ServiceApi.WantedReadPolicyName);
    options.Conventions.AuthorizePage("/Admin/Works", ServiceApi.MediaReadPolicyName);
    options.Conventions.AuthorizePage("/Admin/Work", ServiceApi.MediaReadPolicyName);
    options.Conventions.AuthorizePage("/Admin/Library", ServiceApi.StorageReadPolicyName);
    options.Conventions.AuthorizePage("/Admin/Rules", ServiceApi.RulesReadPolicyName);
    options.Conventions.AuthorizePage("/Admin/Users", ServiceApi.RulesReadPolicyName);
    options.Conventions.AuthorizePage("/Admin/Localization", ServiceApi.LocalizationReadPolicyName);
    options.Conventions.AuthorizePage("/Admin/Languages", ServiceApi.LocalizationReadPolicyName);
    options.Conventions.AuthorizePage("/Admin/Accounts", ServiceApi.AccountsReadPolicyName);
    options.Conventions.AuthorizePage("/Admin/Providers", ServiceApi.ProvidersReadPolicyName);
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/ShellPreview");
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => options.LoginPath = "/Account/Login");
builder.Services.AddAuthorization();

builder.Services.AddJularrServices(builder.Configuration);
builder.Services.AddJularrLocalization(UiResources.English);
builder.Services.AddScoped<UiText>();
builder.Services.AddSingleton<ShellNavigation>();
builder.Services.AddSingleton<FrontendAssets>();

var app = builder.Build();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        // Content fingerprints make these public assets immutable across releases.
        // Other resources (if any) must revalidate, never use the immutable policy.
        var immutable = context.Context.Request.Path.StartsWithSegments("/build/assets")
            && Regex.IsMatch(context.File.Name, @"-[A-Za-z0-9_-]{8,}\.");
        context.Context.Response.Headers.CacheControl = immutable
            ? "public,max-age=31536000,immutable"
            : "public,no-cache";
    }
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Resolve the full DB-backed language pack once per Razor request, after authorization.
// Static resources and API service requests do not pass through localization rendering.
app.Use(async (context, next) =>
{
    if (context.GetEndpoint()?.Metadata.GetMetadata<PageActionDescriptor>() is not null)
    {
        context.Response.Headers.CacheControl = "private,no-store";
        await context.RequestServices.GetRequiredService<UiText>()
            .InitializeAsync(context);
    }

    await next(context);
});

app.MapJularrServices();
app.MapRazorPages();
app.Run();

public partial class Program;
