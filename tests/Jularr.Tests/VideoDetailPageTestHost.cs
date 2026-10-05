using System.Net;
using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Shell;
using Jularr.Web.Features.Storage;
using Jularr.Web.Frontend;
using Jularr.Web.Pages.Library;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jularr.Tests;

/// <summary>
/// Renders the canonical Movie and Series detail pages and the Watch player through real routing, the real shell and the
/// real services over PostgreSQL. The signed-in profile is <see cref="Profile"/>; the media-capability policy decides which
/// media types it may open and request.
/// </summary>
internal sealed class VideoDetailPageTestHost : IAsyncDisposable
{
    public const string Profile = "test-profile";
    private const string OwnerHeader = "X-Test-Owner";

    private readonly string root;
    private readonly IHost host;
    private readonly TestServer server;

    private VideoDetailPageTestHost(string root, AppDbContext db, IHost host, FakeMediaProbeRunner probe, MediaCapabilityStore capabilities)
    {
        this.root = root;
        Db = db;
        this.host = host;
        Probe = probe;
        Capabilities = capabilities;
        server = host.GetTestServer();
    }

    public AppDbContext Db { get; }

    public FakeMediaProbeRunner Probe { get; }

    public MediaCapabilityStore Capabilities { get; }

    /// <summary>A directory for real media files of the Watch tests; it is the storage root those files are attached to.</summary>
    public string MediaDirectory => Path.Combine(root, "media");

    public static async Task<VideoDetailPageTestHost> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jularr-video-detail-{Guid.NewGuid():N}");
        var data = Directory.CreateDirectory(Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.Combine(root, "media"));
        var connectionString = $"Data Source={Path.Combine(root, "jularr.db")};Foreign Keys=True";
        var capabilities = new MediaCapabilityStore(data.FullName);
        var probe = new FakeMediaProbeRunner();

        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .UseContentRoot(FindWebProjectRoot())
                .ConfigureServices(services =>
                {
                    services.AddRazorPages(options => options.Conventions.AddMediaTypeGates()).AddApplicationPart(typeof(MovieDetailModel).Assembly);
                    services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
                    services.AddHttpContextAccessor();
                    services.AddAuthorization(options => JularrPolicies.Register(options));
                    services.AddLogging();
                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton<ViteAssetManifest>();
                    services.AddScoped<CurrentAccountContext>();
                    services.AddSingleton(capabilities);
                    services.AddScoped<IMediaCapabilityService, MediaCapabilityService>();
                    services.AddScoped<IAppShellService, AppShellService>();
                    services.AddSingleton(new AcquisitionRequestSettingsStore(data.FullName));
                    services.AddScoped<AcquisitionAccessStore>();
                    services.AddScoped<AcquisitionRequestService>();
                    services.AddSingleton<IJularrEventPublisher, RecordingEventPublisher>();
                    services.AddScoped<VideoProgressService>();
                    services.AddScoped<VideoDetailQuery>();
                    services.AddSingleton<IMediaProbeRunner>(probe);
                    services.AddSingleton<MediaInventoryService>();
                    services.AddScoped<CanonicalMediaStorageService>();
                    services.AddScoped<CanonicalVideoPlayerService>();
                    services.AddSingleton<StorageAvailabilityCoordinator>();
                    services.AddScoped<LibraryRootAvailabilityService>();
                    services.AddScoped<MediaAvailabilityService>();
                })
                .Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Profile) };
                        if (context.Request.Headers.ContainsKey(OwnerHeader))
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

        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return new VideoDetailPageTestHost(root, db, host, probe, capabilities);
    }

    public async Task<(HttpStatusCode Status, string Html)> GetAsync(string path, bool asOwner = false)
    {
        using var client = server.CreateClient();
        if (asOwner)
        {
            client.DefaultRequestHeaders.Add(OwnerHeader, "true");
        }

        using var response = await client.GetAsync(path);
        return (response.StatusCode, System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }

    /// <summary>The page of an existing title; fails with the response when it is not a plain 200.</summary>
    public async Task<string> GetOkAsync(string path, bool asOwner = false)
    {
        var (status, html) = await GetAsync(path, asOwner);
        Assert.AreEqual(HttpStatusCode.OK, status, $"GET {path} failed:\n{html}");
        return html;
    }

    public async ValueTask DisposeAsync()
    {
        server.Dispose();
        await host.StopAsync();
        host.Dispose();
        await Db.DisposeAsync();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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
}
