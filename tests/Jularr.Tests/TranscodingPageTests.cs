using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Playback.Transcoding;
using Jularr.Web.Features.Shell;
using Jularr.Web.Frontend;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jularr.Tests;

/// <summary>
/// The real Admin &gt; Transcoding page behind real routing, authorization and anti-forgery: only the Owner may open or
/// save it, a forged post is rejected, and validation (path traversal above all) is answered next to the field.
/// </summary>
[TestClass]
public sealed class TranscodingPageTests
{
    [TestMethod]
    public void PageUsesTheAdminSystemPolicy()
    {
        var authorize = typeof(Jularr.Web.Pages.Admin.TranscodingModel)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        Assert.AreEqual(JularrPolicies.AdminSystem, authorize.Policy, "Transcoding limits and the cache folder are owner-only server settings.");
    }

    [TestMethod]
    public async Task OwnerSeesTheStoredSettings()
    {
        await using var host = await TranscodingPageHost.CreateAsync();
        await host.Kit.Settings.SaveAsync(PlaybackTranscodingSettings.Default with { HardwareVideoSessions = 7, HlsCachePath = host.CachePath });

        var html = await host.GetHtmlAsync();

        StringAssert.Contains(html, $"value=\"{host.CachePath}\"");
        Assert.IsTrue(Regex.IsMatch(html, "name=\"HardwareVideoSessions\"[^>]*value=\"7\"|value=\"7\"[^>]*name=\"HardwareVideoSessions\""), "The hardware video limit is shown.");
        StringAssert.Contains(html, "name=\"__RequestVerificationToken\"");
    }

    [TestMethod]
    public async Task NonOwnersAreForbiddenToOpenOrSave()
    {
        await using var host = await TranscodingPageHost.CreateAsync();

        Assert.AreEqual(HttpStatusCode.Forbidden, await host.GetStatusAsync(asOwner: false));
        var status = await host.PostAsync(host.ValidForm(), asOwner: false, withToken: false);

        Assert.AreEqual(HttpStatusCode.Forbidden, status);
        Assert.AreEqual(PlaybackTranscodingSettings.Default, host.Kit.Settings.Current);
    }

    [TestMethod]
    public async Task APostWithoutTheAntiForgeryTokenIsRejected()
    {
        await using var host = await TranscodingPageHost.CreateAsync();

        var status = await host.PostAsync(host.ValidForm(), asOwner: true, withToken: false);

        Assert.AreEqual(HttpStatusCode.BadRequest, status);
        Assert.AreEqual(PlaybackTranscodingSettings.Default, host.Kit.Settings.Current);
    }

    [TestMethod]
    public async Task ValidSettingsAreSavedAndApplyToTheSlotsImmediately()
    {
        await using var host = await TranscodingPageHost.CreateAsync();

        var status = await host.PostAsync(host.ValidForm(("SoftwareVideoSessions", "1"), ("CacheBudgetGiB", "20")));

        Assert.AreEqual(HttpStatusCode.Redirect, status);
        var saved = host.Kit.Settings.Current;
        Assert.AreEqual(1, saved.SoftwareVideoSessions);
        Assert.AreEqual(20L * PlaybackTranscodingSettings.BytesPerGiB, saved.CacheBudgetBytes);
        Assert.AreEqual(host.CachePath, saved.HlsCachePath);
        Assert.IsFalse(saved.TranscodingEnabled, "An unchecked box is off.");
        Assert.AreEqual(1, host.Kit.Slots.Capacity(PlaybackCostClass.SoftwareVideo));
    }

    [TestMethod]
    public async Task APathTraversalIsRefusedNextToTheFieldAndNothingIsSaved()
    {
        await using var host = await TranscodingPageHost.CreateAsync();

        var response = await host.PostForHtmlAsync(host.ValidForm(("HlsCachePath", "/data/playback-cache/../../etc")));

        Assert.AreEqual(HttpStatusCode.OK, response.Status);
        StringAssert.Contains(response.Html, "parent-folder segments");
        Assert.AreEqual(PlaybackTranscodingSettings.Default, host.Kit.Settings.Current);
    }

    [TestMethod]
    public async Task OutOfRangeLimitsAreRefusedAndNeverOverflowIntoValidValues()
    {
        await using var host = await TranscodingPageHost.CreateAsync();

        var response = await host.PostForHtmlAsync(host.ValidForm(("RemuxSessions", "65"), ("CacheBudgetGiB", "9223372036854775807"), ("FreeSpaceFloorGiB", "-3")));

        Assert.AreEqual(HttpStatusCode.OK, response.Status);
        StringAssert.Contains(response.Html, "between 0 and 64");
        StringAssert.Contains(response.Html, "The size limit must be between 1 and 10240 GiB.");
        StringAssert.Contains(response.Html, "The free space to keep must be between 0 and 10240 GiB.");
        Assert.AreEqual(PlaybackTranscodingSettings.Default, host.Kit.Settings.Current);
    }

    /// <summary>The page in front of real routing, with per-request sign-in (owner header or a plain user) and real anti-forgery.</summary>
    private sealed class TranscodingPageHost : IAsyncDisposable
    {
        private const string OwnerHeader = "X-Test-Owner";
        private const string PagePath = "/Admin/Transcoding";

        private readonly IHost _host;
        private readonly TestServer _server;

        private TranscodingPageHost(PlaybackServerTestKit kit, IHost host)
        {
            Kit = kit;
            _host = host;
            _server = host.GetTestServer();
        }

        public PlaybackServerTestKit Kit { get; }

        public string CachePath => Path.Combine(Kit.DataRoot, "hls").Replace('\\', '/');

        public static async Task<TranscodingPageHost> CreateAsync()
        {
            var kit = PlaybackServerTestKit.Create();
            Directory.CreateDirectory(kit.DataRoot);
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(kit.DataRoot, "jularr.db")};Foreign Keys=True").Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            var modules = new InstanceModuleStore(kit.DataRoot);

            var host = await new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .UseContentRoot(FindWebProjectRoot())
                    .ConfigureServices(services =>
                    {
                        services.AddRazorPages().AddApplicationPart(typeof(Jularr.Web.Pages.Admin.TranscodingModel).Assembly);
                        services.AddSingleton(db);
                        services.AddHttpContextAccessor();
                        services.AddAuthorization(options => JularrPolicies.Register(options));
                        services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ForbiddenAnswerHandler>("test", _ => { });
                        services.AddLogging();
                        services.AddSingleton<ViteAssetManifest>();
                        services.AddSingleton<IInstanceModuleService>(modules);
                        services.AddSingleton(new MediaCapabilityStore(kit.DataRoot));
                        services.AddScoped<CurrentAccountContext>();
                        services.AddScoped<IMediaCapabilityService, MediaCapabilityService>();
                        services.AddScoped<IAppShellService, AppShellService>();
                        services.AddSingleton(TimeProvider.System);
                        services.AddSingleton(kit.Settings);
                        services.AddSingleton(kit.Slots);
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

            return new TranscodingPageHost(kit, host);
        }

        /// <summary>The fields the browser would post for the current defaults, with the given overrides.</summary>
        public List<KeyValuePair<string, string>> ValidForm(params (string Name, string Value)[] overrides)
        {
            var fields = new Dictionary<string, string>
            {
                ["SoftwareVideoSessions"] = "2",
                ["HardwareVideoSessions"] = "4",
                ["RemuxSessions"] = "6",
                ["AudioOnlySessions"] = "8",
                ["HlsCachePath"] = CachePath,
                ["CacheBudgetGiB"] = "10",
                ["FreeSpaceFloorGiB"] = "5"
            };
            foreach (var (name, value) in overrides)
            {
                fields[name] = value;
            }

            return [.. fields];
        }

        public async Task<string> GetHtmlAsync()
        {
            using var client = Client(asOwner: true);
            using var response = await client.GetAsync(PagePath);
            var html = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, html);
            return WebUtility.HtmlDecode(html);
        }

        public async Task<HttpStatusCode> GetStatusAsync(bool asOwner)
        {
            using var client = Client(asOwner);
            using var response = await client.GetAsync(PagePath);
            return response.StatusCode;
        }

        public async Task<HttpStatusCode> PostAsync(List<KeyValuePair<string, string>> fields, bool asOwner = true, bool withToken = true) =>
            (await PostForHtmlAsync(fields, asOwner, withToken)).Status;

        public async Task<(HttpStatusCode Status, string Html)> PostForHtmlAsync(List<KeyValuePair<string, string>> fields, bool asOwner = true, bool withToken = true)
        {
            using var client = Client(asOwner);
            if (withToken)
            {
                using var page = await client.GetAsync(PagePath);
                var form = await page.Content.ReadAsStringAsync();
                var token = Regex.Match(form, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
                Assert.IsTrue(token.Length > 0, "The page offers an anti-forgery token.");
                fields.Add(new("__RequestVerificationToken", token));
                var cookies = page.Headers.TryGetValues("Set-Cookie", out var values)
                    ? string.Join("; ", values.Select(value => value.Split(';')[0]).Where(value => value.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal)))
                    : "";
                client.DefaultRequestHeaders.Add("Cookie", cookies);
            }

            using var response = await client.PostAsync(PagePath, new FormUrlEncodedContent(fields));
            return (response.StatusCode, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        }

        public async ValueTask DisposeAsync()
        {
            _server.Dispose();
            await _host.StopAsync();
            _host.Dispose();
            Directory.Delete(Kit.DataRoot, recursive: true);
        }

        private HttpClient Client(bool asOwner)
        {
            var client = _server.CreateClient();
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

            throw new DirectoryNotFoundException("Could not locate the Jularr repository root.");
        }

        private sealed class ForbiddenAnswerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
        {
            protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

            protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
            {
                Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return Task.CompletedTask;
            }
        }
    }
}
