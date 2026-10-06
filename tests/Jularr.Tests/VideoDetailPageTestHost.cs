using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.InstantPlay;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
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

    private VideoDetailPageTestHost(string root, AppDbContext db, IHost host, FakeMediaProbeRunner probe, MediaCapabilityStore capabilities, InstanceModuleStore modules, IndexerStore indexerStore, DownloadClientStore clientStore)
    {
        this.root = root;
        Db = db;
        this.host = host;
        Probe = probe;
        Capabilities = capabilities;
        Modules = modules;
        IndexerStore = indexerStore;
        ClientStore = clientStore;
        server = host.GetTestServer();
    }

    public AppDbContext Db { get; }

    /// <summary>The host's services, for tests that act as a background job against the same install.</summary>
    public IServiceProvider Services => host.Services;

    public FakeMediaProbeRunner Probe { get; }

    public MediaCapabilityStore Capabilities { get; }

    /// <summary>The instance module switches the pages and API read; flip Playback to test a manager-only instance.</summary>
    public InstanceModuleStore Modules { get; }

    public IndexerStore IndexerStore { get; }

    public DownloadClientStore ClientStore { get; }

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
        var acquisitionDirectory = Directory.CreateDirectory(Path.Combine(root, "acquisition"));
        var protection = new EphemeralDataProtectionProvider();
        var indexerStore = new IndexerStore(protection, acquisitionDirectory);
        var clientStore = new DownloadClientStore(protection, acquisitionDirectory);
        var health = new AcquisitionHealthStore(acquisitionDirectory);
        var registry = new MediaAcquisitionRegistry([new MovieAcquisitionRegistration(), new TvAcquisitionRegistration()]);
        var modules = new InstanceModuleStore(data.FullName);

        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .UseContentRoot(FindWebProjectRoot())
                .ConfigureServices(services =>
                {
                    services.AddRazorPages(options =>
                    {
                        options.Conventions.AddMediaTypeGates();
                        options.Conventions.ConfigureFilter(new Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryTokenAttribute());
                    }).AddApplicationPart(typeof(MovieDetailModel).Assembly);
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
                    services.AddScoped<WorkMetadataStore>();
                    var noDownloads = new WorkMetadataFixture.StubHttpClientFactory(new WorkMetadataFixture.StubHandler(_ => throw new InvalidOperationException("The page tests never download artwork.")));
                    services.AddSingleton(new Jularr.Web.Features.Artwork.WorkArtworkCache(Path.Combine(root, "artwork"), [], noDownloads));
                    services.AddSingleton<IInstanceModuleService>(modules);
                    services.AddSingleton(indexerStore);
                    services.AddSingleton(clientStore);
                    services.AddSingleton(new IndexerSearchCoordinator(new Dictionary<IndexerType, IIndexer>(), indexerStore, health, NullLogger<IndexerSearchCoordinator>.Instance));
                    services.AddSingleton(registry);
                    services.AddSingleton(new QualityProfileStore(new DirectoryInfo(Path.Combine(root, "quality-profiles")), registry));
                    services.AddSingleton<IDownloadClient>(new SabnzbdDownloadClient(new FakeSabnzbdClient()));
                    services.AddSingleton(new DownloadClientSelector(clientStore, health));
                    services.AddScoped<DownloadClientSubmissionService>();
                    services.AddScoped<ReleaseRequestTracker>();
                    services.AddScoped<VideoRequestWorkResolver>();
                    services.AddScoped<VideoAcquisitionEngine>();
                    services.AddInstantPlay();
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
                    app.UseInstanceModuleGates();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapRazorPages();
                        endpoints.MapClientApiPlaybackPlanV1();
                        endpoints.MapClientApiPlaybackIntentsV1();
                    });
                }))
            .StartAsync();

        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return new VideoDetailPageTestHost(root, db, host, probe, capabilities, modules, indexerStore, clientStore);
    }

    /// <summary>A client of the signed-in profile, for tests that need the response itself (headers, content type).</summary>
    public HttpClient CreateClient() => server.CreateClient();

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

    /// <summary>Posts a form to a page handler as the signed-in profile (or the owner) without following the redirect; antiforgery is not part of what these tests cover.</summary>
    public async Task<(HttpStatusCode Status, string? Location, IReadOnlyList<string> Cookies)> PostFormAsync(string path, IReadOnlyDictionary<string, string> fields, bool asOwner = false, string? profile = null)
    {
        using var client = new HttpClient(server.CreateHandler()) { BaseAddress = server.BaseAddress };
        if (asOwner)
        {
            client.DefaultRequestHeaders.Add(OwnerHeader, "true");
        }

        if (profile is not null)
        {
            client.DefaultRequestHeaders.Add(ProfileHeader, profile);
        }

        using var response = await client.PostAsync(path, new FormUrlEncodedContent(fields));
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];
        return (response.StatusCode, response.Headers.Location?.OriginalString, cookies);
    }

    /// <summary>The page of an existing title; fails with the response when it is not a plain 200.</summary>
    public async Task<string> GetOkAsync(string path, bool asOwner = false, string? profile = null)
    {
        var (status, html) = await GetAsync(path, asOwner, profile);
        Assert.AreEqual(HttpStatusCode.OK, status, $"GET {path} failed:\n{html}");
        return html;
    }

    /// <summary>Configures one indexer and one download client, so acquisition is ready and a playback intent may acquire missing media.</summary>
    public async Task MakeAcquisitionReadyAsync()
    {
        await IndexerStore.SaveAsync(new IndexerEntry(Guid.NewGuid(), "Video test indexer", IndexerType.Newznab, Enabled: true, Priority: 1, new IndexerSettings("https://indexer.invalid", [2000, 5000], [], 100), "indexer-key"));
        var settings = new DownloadClientSettings("http://sabnzbd:8080", new Dictionary<MediaAcquisitionKind, string?>());
        await ClientStore.SaveAsync(new DownloadClientEntry(Guid.NewGuid(), "SABnzbd", DownloadClientType.Sabnzbd, Enabled: true, Priority: 1, settings, "secret-key"));
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
