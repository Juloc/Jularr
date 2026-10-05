using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Shell;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Subtitles;
using Jularr.Web.Features.Vocabulary;
using Jularr.Web.Infrastructure;
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
    private const string ProfileHeader = "X-Test-Profile";

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

    /// <summary>The host's services, for tests that act as a background job against the same install.</summary>
    public IServiceProvider Services => host.Services;

    public FakeMediaProbeRunner Probe { get; }

    public MediaCapabilityStore Capabilities { get; }

    /// <summary>A directory for real media files of the Watch tests; it is the storage root those files are attached to.</summary>
    public string MediaDirectory => Path.Combine(root, "media");

    /// <param name="sharedDatabasePath">The database path of another test host: both then serve the same PostgreSQL database, as one install does.</param>
    public static async Task<VideoDetailPageTestHost> CreateAsync(string? sharedDatabasePath = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"jularr-video-detail-{Guid.NewGuid():N}");
        var data = Directory.CreateDirectory(Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.Combine(root, "media"));
        var connectionString = $"Data Source={sharedDatabasePath ?? Path.Combine(root, "jularr.db")};Foreign Keys=True";
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
                    // Only the trickplay routes use it; none of them is called, so its generator and queue are not built.
                    services.AddSingleton<CanonicalPlayerNavigationAssetService>(_ => null!);
                    var playbackServer = PlaybackServerTestKit.Create();
                    services.AddSingleton(playbackServer.Slots);
                    services.AddSingleton(playbackServer.Capabilities);
                    services.AddSingleton(playbackServer.Admission);
                    services.AddSingleton(playbackServer.Hls(_ => throw new InvalidOperationException("The detail page tests never start ffmpeg.")));
                    services.AddSingleton<PlaybackStreamSessionStore>();
                    services.AddScoped<ActiveSessionService>();
                    services.AddScoped<PlaybackPlanService>();
                    services.AddSingleton<IJapaneseMorphology, NoMorphology>();
                    services.AddScoped<PlaybackCueProjector>();
                    services.AddSingleton<MediaProcessRunner>();
                    services.AddScoped<EmbeddedSubtitleExtractor>();
                    services.AddScoped<PlaybackService>();
                })
                .Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        var profile = context.Request.Headers.TryGetValue(ProfileHeader, out var header) ? header.ToString() : Profile;
                        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, profile) };
                        if (context.Request.Headers.ContainsKey(OwnerHeader))
                        {
                            claims.Add(new Claim(ClaimTypes.Role, AccountRoles.Owner));
                        }

                        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
                        await next();
                    });
                    app.UseRouting();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapRazorPages();
                        endpoints.MapClientApiPlaybackPlanV1();
                    });
                }))
            .StartAsync();

        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return new VideoDetailPageTestHost(root, db, host, probe, capabilities);
    }

    public async Task<(HttpStatusCode Status, string Html)> GetAsync(string path, bool asOwner = false, string? profile = null)
    {
        using var client = server.CreateClient();
        if (asOwner)
        {
            client.DefaultRequestHeaders.Add(OwnerHeader, "true");
        }

        if (profile is not null)
        {
            client.DefaultRequestHeaders.Add(ProfileHeader, profile);
        }

        using var response = await client.GetAsync(path);
        return (response.StatusCode, System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }

    /// <summary>Attaches a real (tiny) file the fake probe describes with <paramref name="probeJson"/>, exactly as an import does.</summary>
    public async Task AttachVideoAsync(Work work, WorkEpisode? episode, string fileName, string probeJson = MediaProbeFixtures.H264Stereo)
    {
        if (!Db.LibraryRoots.Any())
        {
            Db.LibraryRoots.Add(new LibraryRoot { Name = "Media", Path = MediaDirectory });
            await Db.SaveChangesAsync();
        }

        var path = Path.Combine(MediaDirectory, fileName);
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        Probe.Returns(path, probeJson);
        await new CanonicalMediaStorageService(Db).AttachVideoAsync(work.Id, episode?.Id, path, MediaDirectory, CancellationToken.None);
    }

    /// <summary>Sends a JSON request to the client API as the signed-in profile (or the owner).</summary>
    public async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, object? body = null, bool asOwner = false, string? profile = null)
    {
        using var client = server.CreateClient();
        if (asOwner)
        {
            client.DefaultRequestHeaders.Add(OwnerHeader, "true");
        }

        if (profile is not null)
        {
            client.DefaultRequestHeaders.Add(ProfileHeader, profile);
        }

        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The page of an existing title; fails with the response when it is not a plain 200.</summary>
    public async Task<string> GetOkAsync(string path, bool asOwner = false, string? profile = null)
    {
        var (status, html) = await GetAsync(path, asOwner, profile);
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

    private sealed class NoMorphology : IJapaneseMorphology
    {
        public IReadOnlyList<JapaneseMorphToken> Analyze(string text) => [];
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
