using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
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

namespace Jularr.Tests;

/// <summary>
/// The real Admin Razor pages (Wanted, Requests, Media) in front of a <see cref="VideoAcquisitionTestHost"/>: the same database,
/// executors and Wanted pass, with per-request sign-in (owner or plain user), real anti-forgery and the instance module switches.
/// </summary>
internal sealed class VideoAdminPageHost : IAsyncDisposable
{
    private const string OwnerHeader = "X-Test-Owner";

    private readonly IHost host;
    private readonly TestServer server;

    private VideoAdminPageHost(VideoAcquisitionTestHost video, InstanceModuleStore modules, IHost host)
    {
        Video = video;
        Modules = modules;
        this.host = host;
        server = host.GetTestServer();
    }

    public VideoAcquisitionTestHost Video { get; }

    public InstanceModuleStore Modules { get; }

    public static async Task<VideoAdminPageHost> CreateAsync(VideoAcquisitionTestHost video)
    {
        var dataRoot = video.Environment.Directory.FullName;
        var modules = new InstanceModuleStore(dataRoot);
        var db = video.Environment.Db;

        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .UseContentRoot(FindWebProjectRoot())
                .ConfigureServices(services =>
                {
                    services.AddRazorPages().AddApplicationPart(typeof(Jularr.Web.Pages.Admin.WantedModel).Assembly);
                    services.AddSingleton(db);
                    services.AddHttpContextAccessor();
                    services.AddAuthorization(options => JularrPolicies.Register(options));
                    services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ForbiddenAnswerHandler>("test", _ => { });
                    services.AddLogging();
                    services.AddSingleton<ViteAssetManifest>();
                    services.AddSingleton<IInstanceModuleService>(modules);
                    services.AddSingleton(new MediaCapabilityStore(dataRoot));
                    services.AddScoped<CurrentAccountContext>();
                    services.AddScoped<IMediaCapabilityService, MediaCapabilityService>();
                    services.AddScoped<IAppShellService, AppShellService>();
                    services.AddSingleton(video.Get<AcquisitionRequestSettingsStore>());
                    services.AddSingleton(video.Get<QualityProfileStore>());
                    services.AddSingleton(video.Get<AnimeMonitoringStore>());
                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton(video.Get<VideoAcquisitionEngine>());
                    services.AddSingleton<IAcquisitionRequestExecutor>(video.Get<IAcquisitionRequestExecutor>());
                    services.AddSingleton<IJularrEventPublisher, RecordingEventPublisher>();
                    services.AddSingleton<AnimeAcquisitionScheduler>();
                    services.AddScoped<AcquisitionAccessStore>();
                    services.AddScoped<AcquisitionRequestService>();
                    services.AddMonitoringForTests();
                    services.AddScoped<VideoRequestWorkResolver>();
                    services.AddMediaCore();
                    services.AddScoped<RequestWorkBinder>();
                    services.AddScoped<RequestProfileAssignment>();
                    services.AddScoped<RequestArtworkResolver>();
                    services.AddScoped<VideoMonitoringService>();
                    services.AddScoped<AdminVideoMediaService>();
                    services.AddSingleton<IMediaProbeRunner, FakeMediaProbeRunner>();
                    services.AddSingleton<MediaInventoryService>();
                    services.AddScoped<Jularr.Web.Features.Operations.OperationRunner>();
                    services.AddScoped<MediaFileReanalysisService>();
                    services.AddScoped<WantedListService>();
                    services.AddSingleton(video.Get<Jularr.Web.Features.Acquisition.Release.MediaAcquisitionRegistry>());
                    services.AddScoped<Jularr.Web.Features.Music.MusicQuery>();
                    services.AddSingleton<Jularr.Web.Features.Music.IMusicMetadataProvider>(new MusicLibraryTests.FakeMusicProvider());
                    services.AddScoped<Jularr.Web.Features.MediaCore.WorkService>();
                    services.AddScoped<Jularr.Web.Features.Music.MusicLibraryService>();
                    services.AddScoped<Jularr.Web.Features.Music.MusicAcquisitionEngine>();
                    services.AddScoped<Jularr.Web.Features.Music.MusicManualSearchService>();
                    services.AddSingleton(video.Get<Jularr.Web.Features.Acquisition.Indexers.IndexerSearchCoordinator>());
                    services.AddSingleton(video.Get<Jularr.Web.Features.Acquisition.Indexers.IndexerStore>());
                    services.AddSingleton(video.Get<Jularr.Web.Features.Acquisition.DownloadClients.DownloadClientStore>());
                    services.AddSingleton(video.Get<Jularr.Web.Features.Acquisition.DownloadClients.DownloadClientSubmissionService>());
                    services.AddSingleton(video.Get<ReleaseRequestTracker>());
                    services.AddSingleton(video.Get<Jularr.Web.Features.Acquisition.Core.AcquisitionCore>());
                    services.AddScoped<Jularr.Web.Features.ReadingAcquisition.ReadingAcquisitionEngine>();
                    services.AddScoped<Jularr.Web.Features.Acquisition.ManualSearch.ManualGrabCoordinator>();
                    services.AddScoped<Jularr.Web.Features.ReadingAcquisition.ReadingManualSearchService>();
                })
                .Configure(app =>
                {
                    // Stand-in for cookie sign-in: the owner header makes the caller the owner, otherwise a plain user.
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

        return new VideoAdminPageHost(video, modules, host);
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

    /// <summary>
    /// Posts a form the way a browser does: the anti-forgery token and cookie come from <paramref name="formPage"/>. A successful action answers
    /// with the redirect back to the page, which is not followed.
    /// </summary>
    public async Task<HttpStatusCode> PostAsync(string formPage, string handlerPath, IEnumerable<KeyValuePair<string, string>> fields, bool asOwner = true, bool withToken = true) =>
        (await SendAsync(formPage, handlerPath, fields, asOwner, withToken)).Status;

    /// <summary>Posts a form the way <see cref="PostAsync"/> does and returns the page the post answered with (a handler that renders instead of redirecting).</summary>
    public async Task<string> PostHtmlAsync(string formPage, string handlerPath, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var (status, html) = await SendAsync(formPage, handlerPath, fields, true, true);
        Assert.AreEqual(HttpStatusCode.OK, status, $"POST {handlerPath} failed:\n{html}");
        return WebUtility.HtmlDecode(html);
    }

    private async Task<(HttpStatusCode Status, string Html)> SendAsync(string formPage, string handlerPath, IEnumerable<KeyValuePair<string, string>> fields, bool asOwner, bool withToken)
    {
        var form = fields.ToList();
        using var client = Client(asOwner);
        if (withToken)
        {
            using var page = await client.GetAsync(formPage);
            var html = await page.Content.ReadAsStringAsync();
            var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            Assert.IsTrue(token.Length > 0, $"{formPage} offers no anti-forgery token.");
            form.Add(new("__RequestVerificationToken", token));
            var cookies = page.Headers.TryGetValues("Set-Cookie", out var values)
                ? string.Join("; ", values.Select(value => value.Split(';')[0]).Where(value => value.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal)))
                : "";
            client.DefaultRequestHeaders.Add("Cookie", cookies);
        }

        using var response = await client.PostAsync(handlerPath, new FormUrlEncodedContent(form));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
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

    private sealed class ForbiddenAnswerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

        protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = (int)HttpStatusCode.Forbidden;
            return Task.CompletedTask;
        }
    }
}
