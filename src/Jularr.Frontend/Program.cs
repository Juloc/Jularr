using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.Core.Localization;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Localization;
using Jularr.Frontend.Modules.Live;
using Jularr.Frontend.Modules.Pwa;
using Jularr.Frontend.Pages.Shared;
using Jularr.Workers;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.StaticFiles;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages(options => options.Conventions.AddJularrPages());

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => options.LoginPath = "/Account/Login");
builder.Services.AddAuthorization();

builder.Services.AddJularrServices(builder.Configuration);
builder.Services.AddJularrWorkers();
builder.Services.AddJularrLocalization(UiResources.English, UiCatalogs.Load());
builder.Services.AddScoped<UiText>();
builder.Services.AddHostedService<LanDiscoveryService>();
builder.Services.AddScoped<AdminMenuCounters>();
builder.Services.AddSingleton<ShellNavigation>();
builder.Services.AddSingleton<FrontendAssets>();
builder.Services.AddJularrCompression();

// The status page hands its addresses to the application at the moment the application's own server is about to bind them.
var handover = new StartupHandover();
handover.Install(builder);
var app = builder.Build();

// The configured addresses answer from here on with a status page (503, "Jularr wird aktualisiert") while the backup and the migrations run;
// the application takes them over when the database is current. A failed update was rolled back and leaves the maintenance page only.
var startup = await StartupStatusServer.StartAsync(args);
DatabaseUpgradeFailure? upgradeFailure;
try
{
    upgradeFailure = await DatabaseUpgrade.RunAsync(app.Services, startup.Stopping, startup.Status.Report);
}
catch (OperationCanceledException) when (startup.Stopping.IsCancellationRequested)
{
    // Stopped while updating: nothing is left running.
    await app.DisposeAsync();
    await startup.DisposeAsync();
    return;
}

if (upgradeFailure is not null)
{
    startup.Status.Fail(upgradeFailure);
    await app.DisposeAsync();
    await startup.WaitForShutdownAsync();
    await startup.DisposeAsync();
    return;
}

startup.Status.Starting();
handover.Arm(async () =>
{
    await startup.StopAsync();
    await startup.DisposeAsync();
});

app.UseExceptionHandler();
app.UseTrustedProxies();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseResponseCompression();
// EmulatorJS cores are .data files; pdf.js loads character maps, standard fonts and colour profiles.
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".data"] = "application/octet-stream";
contentTypes.Mappings[".bcmap"] = "application/octet-stream";
contentTypes.Mappings[".pfb"] = "application/octet-stream";
contentTypes.Mappings[".icc"] = "application/octet-stream";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
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
app.UseJularrErrorPages();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseJularrSetupGate();
app.UseAuthorization();

// Every page's server time against its budget; the shell and the page handler report their parts.
app.UseJularrPageTimings();

// Resolve the full DB-backed language pack once per Razor request, after authorization.
// Static resources and API service requests do not pass through localization rendering.
app.Use(async (context, next) =>
{
    if (context.GetEndpoint()?.Metadata.GetMetadata<PageActionDescriptor>() is not null)
    {
        // The same values the antiforgery token sets itself: any other value is overridden by it and logged as a warning on every page with a form.
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers.Pragma = "no-cache";
        var texts = context.RequestServices.GetRequiredService<UiText>();
        await texts.InitializeAsync(context);

        // Culture covers downstream rendering.
        var culture = CultureInfo.GetCultureInfo(texts.Locale);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    await next(context);
});

if (app.Environment.IsDevelopment())
{
    // The end-to-end tests force an unhandled exception here to see the 500 page.
    IResult Fail() => throw new InvalidOperationException("Forced failure with a secret detail.");
    app.MapGet("/Development/Fail", Fail).AllowAnonymous();
}

app.MapJularrServices();
app.MapJularrLive();
app.MapJularrPwa();
app.MapRazorPages();
app.Run();

public partial class Program;
