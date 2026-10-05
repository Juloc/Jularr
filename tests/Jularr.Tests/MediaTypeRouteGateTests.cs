using System.Net;
using System.Security.Claims;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Shell;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jularr.Tests;

/// <summary>
/// The consumer routes of a media type a profile cannot browse answer 404 through the real
/// Razor Pages routing and conventions of Program.cs (#598), and every route the navigation
/// catalog ties to a media type carries the gate.
/// </summary>
[TestClass]
public sealed class MediaTypeRouteGateTests
{
    [TestMethod]
    [DataRow("/Library")]
    [DataRow("/Library/Anime/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("/Library/Episode/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("/Library/Movie/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("/Library/Series/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("/Library/Watch/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("/Reading")]
    [DataRow("/Novels")]
    [DataRow("/Novels/Work/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("/Novels/Read/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("/Manga")]
    [DataRow("/Manga/Series/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("/Manga/Read/7a4c0000-0000-0000-0000-000000000001")]
    public async Task ABooksOnlyProfileGetsNotFoundOnEveryOtherLibraryRoute(string path)
    {
        await using var host = await GateHost.CreateAsync();
        await host.OnlyAsync("books-only", WorkMediaType.Book);

        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync(path, "books-only"), path);
    }

    [TestMethod]
    [DataRow("/Books")]
    [DataRow("/Books/ForYou")]
    [DataRow("/Books/Library/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("/Books/Read/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("/Books/Cover/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("/Books/some-catalog-id")]
    public async Task AProfileWithoutBooksGetsNotFoundOnEveryBookRoute(string path)
    {
        await using var host = await GateHost.CreateAsync();
        await host.OnlyAsync("no-books", WorkMediaType.Anime, WorkMediaType.Manga, WorkMediaType.LightNovel);

        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync(path, "no-books"), path);
    }

    [TestMethod]
    [DataRow("/Library/Anime/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("/Library/Episode/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("/Library/Episode/7a4c0000-0000-0000-0000-000000000001/segments")]
    [DataRow("/Library/PresentationGroups/7a4c0000-0000-0000-0000-000000000001")]
    public async Task AMovieOnlyOrSeriesOnlyProfileDoesNotReachTheAnimePagesThroughTheSharedLibraryHub(string path)
    {
        // AnimeRepair and Rename carry their own role policy, which answers first; the route-table test checks their gate.
        await using var host = await GateHost.CreateAsync();
        await host.OnlyAsync("movies", WorkMediaType.Movie);
        await host.OnlyAsync("series", WorkMediaType.Series);

        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync(path, "movies"), path);
        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync(path, "series"), path);
        Assert.AreNotEqual(HttpStatusCode.NotFound, await host.GetStatusAsync("/Library", "movies"), "The hub itself stays open for Movies.");
    }

    [TestMethod]
    [DataRow("movie", "/Library/Series/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("series", "/Library/Movie/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("anime", "/Library/Movie/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("anime", "/Library/Series/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("anime", "/Library/Watch/7a4c0000-0000-0000-0000-000000000001")]
    public async Task AProfileDoesNotReachTheDetailOrPlayerPagesOfAVideoTypeItCannotBrowse(string visible, string path)
    {
        await using var host = await GateHost.CreateAsync();
        await host.OnlyAsync("viewer", WorkMediaTypes.Parse(visible)!.Value);

        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync(path, "viewer"), path);
    }

    [TestMethod]
    [DataRow("movie", "/Library/Movie/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("series", "/Library/Series/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("movie", "/Library/Watch/7a4c0000-0000-0000-0000-000000000001")]
    [DataRow("series", "/Library/Watch/7a4c0000-0000-0000-0000-000000000001")]
    public async Task AProfileReachesTheDetailAndPlayerPagesOfItsOwnVideoType(string visible, string path)
    {
        await using var host = await GateHost.CreateAsync();
        await host.OnlyAsync("viewer", WorkMediaTypes.Parse(visible)!.Value);

        // The gate lets the request through to a page model this host cannot build; only the gate answers 404.
        Assert.AreNotEqual(HttpStatusCode.NotFound, await host.GetStatusAsync(path, "viewer"), path);
    }

    [TestMethod]
    public async Task TheReadingHubIsClosedOnlyWhenBothReadingTypesAreHidden()
    {
        await using var host = await GateHost.CreateAsync();
        await host.OnlyAsync("neither", WorkMediaType.Anime, WorkMediaType.Book);

        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync("/Reading", "neither"));
        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync("/Novels", "neither"));
        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync("/Manga", "neither"));
    }

    [TestMethod]
    [DataRow("/Books", "book", false)]
    [DataRow("/Books/ForYou", "book", false)]
    [DataRow("/Reading", "lightNovel", false)]
    [DataRow("/Novels", "lightNovel", false)]
    [DataRow("/Manga", "manga", false)]
    [DataRow("/Library", "anime", false)]
    [DataRow("/Library", "movie", false)]
    [DataRow("/Library", "series", false)]
    [DataRow("/Books", "book", true)]
    [DataRow("/Library", "book", true)]
    [DataRow("/Novels", "anime", true)]
    public async Task ARouteIsReachedOnlyWhenTheProfileMayBrowseItsMediaType(string path, string visible, bool asOwner)
    {
        await using var host = await GateHost.CreateAsync();
        await host.OnlyAsync("reader", WorkMediaTypes.Parse(visible)!.Value);

        // A request the gate lets through reaches the page model; this host registers none of the
        // page dependencies, so reaching it fails there. Only the gate's own 404 means "denied".
        var gateDenied = await host.GetStatusAsync(path, "reader", asOwner) == HttpStatusCode.NotFound;

        Assert.IsFalse(gateDenied, $"{path} must be open for {visible} (owner: {asOwner}).");
    }

    [TestMethod]
    public async Task EveryPageUnderAMediaRouteCarriesExactlyItsGateAndNoOtherPageDoes()
    {
        await using var host = await GateHost.CreateAsync();
        var pages = host.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<PageActionDescriptor>()
            .ToArray();
        Assert.IsTrue(pages.Length > 50, "Expected the application's Razor Pages to be discovered.");

        var gatedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        {
            var route = new PathString("/" + page.AttributeRouteInfo!.Template!.TrimStart('/'));
            var owners = UiNavigationCatalog.MediaRoutes
                .Where(candidate => route.StartsWithSegments(candidate.Root, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var gates = page.FilterDescriptors
                .Select(descriptor => descriptor.Filter)
                .OfType<MediaTypeGateFilter>()
                .ToArray();

            if (owners.Length == 0)
            {
                Assert.AreEqual(0, gates.Length, $"{page.RelativePath} is gated but its route {route} belongs to no media type.");
                continue;
            }

            // A narrow root (an Anime page) sits inside its hub root (/Library): the page carries one gate per root.
            Assert.AreEqual(owners.Length, gates.Length, $"{page.RelativePath} ({route}) must carry one gate per media route above it.");
            foreach (var owner in owners)
            {
                Assert.IsTrue(
                    gates.Any(gate => gate.MediaTypes.Order().SequenceEqual(owner.MediaTypes.Order())),
                    $"{page.RelativePath} ({route}) is missing the gate of {owner.Root}.");
                gatedRoots.Add(owner.Root);
            }
        }

        CollectionAssert.AreEquivalent(
            UiNavigationCatalog.MediaRoutes.Select(route => route.Root).ToArray(),
            gatedRoots.ToArray(),
            "Every media route root must gate at least one real page.");
    }

    /// <summary>
    /// Real Razor Pages routing plus the production <c>AddMediaTypeGates</c> convention, with the real
    /// capability service over a temp policy store. Denied requests never reach a page model, so no
    /// page dependencies are registered.
    /// </summary>
    private sealed class GateHost : IAsyncDisposable
    {
        private const string ProfileHeader = "X-Test-Profile";
        private const string OwnerHeader = "X-Test-Owner";
        private readonly string root;
        private readonly IHost host;
        private readonly TestServer server;

        private GateHost(string root, IHost host, TestServer server, MediaCapabilityStore store)
        {
            this.root = root;
            this.host = host;
            this.server = server;
            Store = store;
        }

        public MediaCapabilityStore Store { get; }

        public IServiceProvider Services => host.Services;

        public static async Task<GateHost> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"jularr-gate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var store = new MediaCapabilityStore(root);

            var host = await new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder
                        .UseTestServer()
                        .UseContentRoot(FindWebProjectRoot())
                        .ConfigureServices(services =>
                        {
                            services
                                .AddRazorPages(options => options.Conventions.AddMediaTypeGates())
                                .AddApplicationPart(typeof(Jularr.Web.Pages.Library.AnimeModel).Assembly);
                            services.AddHttpContextAccessor();
                            services.AddAuthorization(options => JularrPolicies.Register(options));
                            services.AddSingleton(store);
                            services.AddScoped<IMediaCapabilityService, MediaCapabilityService>();
                            services.AddScoped<IAppShellService, AppShellService>();
                        })
                        .Configure(app =>
                        {
                            app.Use(async (context, next) =>
                            {
                                var profile = context.Request.Headers[ProfileHeader].ToString();
                                context.User = Principal(profile, context.Request.Headers.ContainsKey(OwnerHeader));
                                await next();
                            });
                            app.UseRouting();
                            app.UseAuthorization();
                            app.UseEndpoints(endpoints => endpoints.MapRazorPages());
                        });
                })
                .StartAsync();

            return new GateHost(root, host, host.GetTestServer(), store);
        }

        /// <summary>A plain user (role User) who can browse exactly <paramref name="visible"/>.</summary>
        public async Task OnlyAsync(string profileId, params WorkMediaType[] visible)
        {
            foreach (var type in WorkMediaTypes.All.Except(visible))
            {
                await Store.SetUserOverrideAsync(profileId, type, MediaCapability.Hidden);
            }
        }

        /// <summary>
        /// The status a request ends with. A request that gets past the gate fails inside the page model
        /// (its dependencies are not registered here) and reports 500 instead of the gate's 404.
        /// </summary>
        public async Task<HttpStatusCode> GetStatusAsync(string path, string profileId, bool asOwner = false)
        {
            using var client = server.CreateClient();
            client.DefaultRequestHeaders.Add(ProfileHeader, profileId);
            if (asOwner)
            {
                client.DefaultRequestHeaders.Add(OwnerHeader, "true");
            }

            try
            {
                using var response = await client.GetAsync(path);
                return response.StatusCode;
            }
            catch (InvalidOperationException)
            {
                return HttpStatusCode.InternalServerError;
            }
        }

        public static ClaimsPrincipal Principal(string profileId, bool asOwner = false)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, profileId) };
            if (asOwner)
            {
                claims.Add(new Claim(ClaimTypes.Role, AccountRoles.Owner));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        public async ValueTask DisposeAsync()
        {
            server.Dispose();
            await host.StopAsync();
            host.Dispose();
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
}
