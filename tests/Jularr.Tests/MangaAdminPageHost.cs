using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Monitoring;
using Jularr.Web.Features.Shell;
using Jularr.Web.Frontend;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Env = Jularr.Tests.BookPdfAcquisitionTests.BookAcquisitionEnvironment;

namespace Jularr.Tests;

internal sealed class MangaAdminPageHost : IAsyncDisposable
{
    private const string OwnerHeader = "X-Test-Owner";

    private readonly IHost host;
    private readonly TestServer server;

    private MangaAdminPageHost(Env environment, InstanceModuleStore modules, IHost host)
    {
        Environment = environment;
        Modules = modules;
        this.host = host;
        server = host.GetTestServer();
    }

    public Env Environment { get; }

    public InstanceModuleStore Modules { get; }

    public static async Task<MangaAdminPageHost> CreateAsync(Env environment)
    {
        var dataRoot = Path.Combine(environment.Root, "data");
        var modules = new InstanceModuleStore(dataRoot);
        var services = environment.Services;
        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .UseContentRoot(FindWebProjectRoot())
                .ConfigureServices(collection =>
                {
                    collection.AddRazorPages().AddApplicationPart(typeof(Jularr.Web.Pages.Admin.Manga.WorkModel).Assembly);
                    collection.AddSingleton(environment.Db);
                    collection.AddHttpContextAccessor();
                    collection.AddAuthorization(options => JularrPolicies.Register(options));
                    collection.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, SignedOutHandler>("test", _ => { });
                    collection.AddLogging();
                    collection.AddSingleton<ViteAssetManifest>();
                    collection.AddSingleton<IInstanceModuleService>(modules);
                    collection.AddSingleton(new MediaCapabilityStore(dataRoot));
                    collection.AddScoped<CurrentAccountContext>();
                    collection.AddScoped<IMediaCapabilityService, MediaCapabilityService>();
                    collection.AddScoped<IAppShellService, AppShellService>();
                    collection.AddSingleton(TimeProvider.System);
                    collection.AddSingleton<IJularrEventPublisher, RecordingEventPublisher>();
                    collection.AddSingleton(services.GetRequiredService<AcquisitionAccessStore>());
                    collection.AddSingleton(services.GetRequiredService<AcquisitionRequestService>());
                    collection.AddSingleton(services.GetRequiredService<QualityProfileStore>());
                    collection.AddSingleton(services.GetRequiredService<MonitoringResolver>());
                    collection.AddSingleton(services.GetRequiredService<MonitoringCommands>());
                    collection.AddSingleton(services.GetRequiredService<WantedReconciler>());
                    collection.AddSingleton(services.GetRequiredService<Jularr.Web.Features.ReadingAcquisition.ReadingStructureService>());
                    collection.AddSingleton(services.GetRequiredService<Jularr.Web.Features.ReadingAcquisition.MangaVersionSelector>());
                    collection.AddSingleton(services.GetRequiredService<Jularr.Web.Features.ReadingAcquisition.ReadingCoverageService>());
                    collection.AddScoped<Jularr.Web.Features.ReadingAcquisition.MangaWorkAdminQuery>();
                })
                .Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        var owner = context.Request.Headers.ContainsKey(OwnerHeader);
                        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, owner ? "owner" : "plain-user") };
                        if (owner)
                        {
                            claims.Add(new Claim(ClaimTypes.Role, AccountRoles.Owner));
                        }

                        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
                        await next();
                    });
                    app.UseRouting();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapRazorPages());
                }))
            .StartAsync();
        return new MangaAdminPageHost(environment, modules, host);
    }

    public async Task<string> GetHtmlAsync(string path, bool asOwner = true)
    {
        using var client = Client(asOwner);
        using var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"GET {path} failed:\n{html}");
        return WebUtility.HtmlDecode(html);
    }

    public async Task<HttpStatusCode> GetStatusAsync(string path, bool asOwner = true)
    {
        using var client = Client(asOwner);
        using var response = await client.GetAsync(path);
        return response.StatusCode;
    }

    public async Task<HttpStatusCode> PostAsync(string formPage, string handlerPath, IEnumerable<KeyValuePair<string, string>> fields, bool asOwner = true)
    {
        var form = fields.ToList();
        using var client = Client(asOwner);
        using var page = await client.GetAsync(formPage);
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.IsTrue(token.Length > 0, $"{formPage} offers no anti-forgery token (status {page.StatusCode}).");
        form.Add(new("__RequestVerificationToken", token));
        var cookies = page.Headers.TryGetValues("Set-Cookie", out var values)
            ? string.Join("; ", values.Select(value => value.Split(';')[0]).Where(value => value.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal)))
            : "";
        client.DefaultRequestHeaders.Add("Cookie", cookies);
        using var response = await client.PostAsync(handlerPath, new FormUrlEncodedContent(form));
        return response.StatusCode;
    }

    public async ValueTask DisposeAsync()
    {
        server.Dispose();
        await host.StopAsync();
        host.Dispose();
    }

    private HttpClient Client(bool asOwner)
    {
        var client = server.CreateClient();
        if (asOwner)
        {
            client.DefaultRequestHeaders.Add(OwnerHeader, "true");
        }

        return client;
    }

    private static string FindWebProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                return Path.Combine(directory.FullName, "src", "Jularr.Web");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }

    private sealed class SignedOutHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
}
