using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Shell;
using Jularr.Web.Frontend;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jularr.Tests;

/// <summary>Admin → Wanted rendered end to end through a real HTTP request.</summary>
[TestClass]
public sealed class AdminWantedPageRenderTests
{
    [TestMethod]
    public async Task WantedListsEveryMediaTypeInItsStateWithCountsActionsAndTheAdminNavigationLink()
    {
        await using var host = await WantedHost.CreateAsync();
        var animeId = await host.SeedAsync();

        var html = await host.GetHtmlAsync("/Admin/Wanted");

        foreach (var tab in new[] { "All", "Requested", "Missing", "Searching", "Failed" })
        {
            StringAssert.Contains(html, tab);
        }

        Assert.AreEqual(7, TabCount(html, "All"));
        Assert.AreEqual(2, TabCount(html, "Requested"));
        Assert.AreEqual(2, TabCount(html, "Missing"));
        Assert.AreEqual(1, TabCount(html, "Searching"));
        Assert.AreEqual(2, TabCount(html, "Failed"));

        foreach (var title in new[] { "Project Hail Mary", "Berserk", "Mushoku Tensei", "Dune Novel", "Dune: Part Two", "Frieren" })
        {
            StringAssert.Contains(html, title);
        }

        foreach (var hidden in new[] { "Pending Book", "Completed Book", "Rejected Book" })
        {
            Assert.IsFalse(html.Contains(hidden, StringComparison.Ordinal), $"{hidden} is not part of the worklist.");
        }

        StringAssert.Contains(html, "data-status=\"requested\"");
        StringAssert.Contains(html, "data-status=\"missing\"");
        StringAssert.Contains(html, "data-status=\"downloading\"");
        StringAssert.Contains(html, "data-status=\"failed\"");
        StringAssert.Contains(html, "data-kind=\"manga\"");
        StringAssert.Contains(html, "Season 1, episode 28");
        StringAssert.Contains(html, "Volume 42");
        StringAssert.Contains(html, "Deutsch");
        StringAssert.Contains(html, "Anime 1080p");
        StringAssert.Contains(html, "Upgrade wanted");
        StringAssert.Contains(html, "Failed attempts: 2");
        StringAssert.Contains(html, "No release yet.");
        StringAssert.Contains(html, "No search recorded");

        Assert.AreEqual(4, Regex.Matches(html, @"admin-icon-label"">Search now</span>").Count);
        Assert.AreEqual(1, Regex.Matches(html, @"admin-icon-label"">Retry</span>").Count, "Only the failed request can be retried.");
        Assert.AreEqual(4, Regex.Matches(html, @"admin-icon-label"">Manual search</span>").Count);
        Assert.AreEqual(2, Regex.Matches(html, @"href=""/Admin/BookManualSearch\?id=").Count, "Approved/failed Book requests expose their own manual release search.");
        StringAssert.Contains(html, "handler=SearchRequest");
        StringAssert.Contains(html, "handler=SearchAnime");
        StringAssert.Contains(html, "search=frieren");
        StringAssert.Contains(html, "#search-results");
        StringAssert.Contains(html, $"href=\"/Library/Anime/{animeId:D}\"");
        StringAssert.Contains(html, $"href=\"/Admin/Media/{animeId:D}\"");
        StringAssert.Contains(html, "name=\"returnUrl\" value=\"/Admin/Wanted\"");

        Assert.IsTrue(
            Regex.IsMatch(html, "<a class=\"admin-nav-item active\"[^>]*href=\"/Admin/Wanted\""),
            "The admin navigation links to Wanted and marks it as the current page.");
        Assert.IsFalse(html.Contains("/Acquisition#wanted", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TabsMediaTypeLanguageSearchAndSortNarrowTheListAndKeepTheOthersCounted()
    {
        await using var host = await WantedHost.CreateAsync();
        await host.SeedAsync();

        var failed = await host.GetHtmlAsync("/Admin/Wanted?tab=failed");
        StringAssert.Contains(failed, "Dune Novel");
        StringAssert.Contains(failed, "Frieren");
        Assert.IsFalse(failed.Contains("Berserk", StringComparison.Ordinal));
        Assert.AreEqual(7, TabCount(failed, "All"));

        var manga = await host.GetHtmlAsync("/Admin/Wanted?type=manga");
        StringAssert.Contains(manga, "Berserk");
        Assert.IsFalse(manga.Contains("Dune Novel", StringComparison.Ordinal));
        Assert.AreEqual(1, TabCount(manga, "All"));

        var german = await host.GetHtmlAsync("/Admin/Wanted?lang=de");
        StringAssert.Contains(german, "Berserk");
        Assert.IsFalse(german.Contains("Project Hail Mary", StringComparison.Ordinal));

        var searched = await host.GetHtmlAsync("/Admin/Wanted?q=hail");
        StringAssert.Contains(searched, "Project Hail Mary");
        Assert.IsFalse(searched.Contains("Berserk", StringComparison.Ordinal));

        var byProfile = await host.GetHtmlAsync("/Admin/Wanted?profile=anime-1080p");
        StringAssert.Contains(byProfile, "Frieren");
        Assert.IsFalse(byProfile.Contains("Berserk", StringComparison.Ordinal));

        var sorted = await host.GetHtmlAsync("/Admin/Wanted?tab=missing&sort=title");
        Assert.IsTrue(
            sorted.IndexOf("Berserk", StringComparison.Ordinal) < sorted.IndexOf("Frieren", StringComparison.Ordinal),
            "Title order lists Berserk before Frieren.");
        StringAssert.Contains(sorted, "<option value=\"title\" selected");

        var none = await host.GetHtmlAsync("/Admin/Wanted?q=zzz");
        StringAssert.Contains(none, "No wanted items match these filters.");
        StringAssert.Contains(none, "Reset filters");
    }

    [TestMethod]
    public async Task EmptyPagingAndLoadErrorStatesRenderCleanly()
    {
        await using (var empty = await WantedHost.CreateAsync())
        {
            var html = await empty.GetHtmlAsync("/Admin/Wanted");
            StringAssert.Contains(html, "Nothing is wanted right now.");
            Assert.AreEqual(0, TabCount(html, "All"));
        }

        await using (var paged = await WantedHost.CreateAsync())
        {
            var store = new AcquisitionAccessStore(paged.Db);
            for (var index = 0; index < AdminWantedQuery.PageSize + 1; index++)
            {
                await store.CreateAsync(
                    new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "test", $"m{index}", $"Movie {index:00}", null, null),
                    WantedHost.Profile,
                    AcquisitionRequestStatus.Approved,
                    "owner",
                    CancellationToken.None);
            }

            var last = await paged.GetHtmlAsync("/Admin/Wanted?p=99&sort=title");
            StringAssert.Contains(last, "21–21 of 21");
            StringAssert.Contains(last, "Movie 20");
            StringAssert.Contains(last, "aria-current=\"page\"");
            StringAssert.Contains(last, "rel=\"prev\"");
            Assert.IsFalse(last.Contains("rel=\"next\"", StringComparison.Ordinal));
        }

        await using var broken = await WantedHost.CreateAsync();
        Directory.CreateDirectory(Path.Combine(broken.DataRoot, "acquisition"));
        await File.WriteAllTextAsync(Path.Combine(broken.DataRoot, "acquisition", "monitoring.json"), "{ not json");

        var failed = await broken.GetHtmlAsync("/Admin/Wanted");
        StringAssert.Contains(failed, "role=\"alert\"");
        StringAssert.Contains(failed, "The wanted list could not be loaded.");
        StringAssert.Contains(failed, "href=\"/Admin/Wanted\"");
        Assert.IsFalse(failed.Contains("data-admin-wanted>", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OnlyMediaManagersMayOpenWanted()
    {
        await using var host = await WantedHost.CreateAsync();

        Assert.AreEqual(HttpStatusCode.OK, await host.GetStatusAsync("/Admin/Wanted", asOwner: true));
        Assert.AreEqual(HttpStatusCode.Forbidden, await host.GetStatusAsync("/Admin/Wanted", asOwner: false));
    }

    private static int TabCount(string html, string label)
    {
        var match = Regex.Match(html, @"library-type-tab[^>]*>\s*" + Regex.Escape(label) + @"\s*<span class=""admwant-count"">(\d+)</span>");
        Assert.IsTrue(match.Success, $"The {label} tab has no count.");
        return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class StubExecutor(MediaAcquisitionKind kind) : IAcquisitionRequestExecutor
    {
        public MediaAcquisitionKind Kind => kind;

        public Task<AcquisitionExecution> ExecuteAsync(AcquisitionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new AcquisitionExecution(AcquisitionRequestStatus.Approved, "Stub."));
    }

    private sealed class ForbiddenAnswerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());

        protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = (int)HttpStatusCode.Forbidden;
            return Task.CompletedTask;
        }
    }

    private sealed class WantedHost : IAsyncDisposable
    {
        public const string Profile = "test-profile";
        private const string OwnerHeader = "X-Test-Owner";

        private readonly string root;
        private readonly IHost host;
        private readonly TestServer server;

        private WantedHost(string root, AppDbContext db, AnimeMonitoringStore monitoring, IHost host, TestServer server)
        {
            this.root = root;
            Db = db;
            Monitoring = monitoring;
            this.host = host;
            this.server = server;
        }

        public AppDbContext Db { get; }

        public AnimeMonitoringStore Monitoring { get; }

        public string DataRoot => Path.Combine(root, "data");

        public static async Task<WantedHost> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"jularr-wanted-page-{Guid.NewGuid():N}");
            var data = Directory.CreateDirectory(Path.Combine(root, "data"));
            var connectionString = $"Data Source={Path.Combine(root, "jularr.db")};Foreign Keys=True";
            var monitoring = new AnimeMonitoringStore(data.FullName);
            var capabilities = new MediaCapabilityStore(data.FullName);

            var host = await new HostBuilder()
                .ConfigureWebHost(webBuilder => webBuilder
                    .UseTestServer()
                    .UseContentRoot(FindWebProjectRoot())
                    .ConfigureServices(services =>
                    {
                        services
                            .AddRazorPages()
                            .AddApplicationPart(typeof(Jularr.Web.Pages.Admin.WantedModel).Assembly);
                        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
                        services.AddHttpContextAccessor();
                        services.AddAuthorization(options => JularrPolicies.Register(options));
                        services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ForbiddenAnswerHandler>("test", _ => { });
                        services.AddLogging();
                        services.AddSingleton<ViteAssetManifest>();
                        services.AddScoped<CurrentAccountContext>();
                        services.AddSingleton(capabilities);
                        services.AddScoped<IMediaCapabilityService, MediaCapabilityService>();
                        services.AddScoped<IAppShellService, AppShellService>();
                        services.AddSingleton(new AcquisitionRequestSettingsStore(data.FullName));
                        services.AddSingleton(new QualityProfileStore(new DirectoryInfo(Path.Combine(data.FullName, "quality"))));
                        services.AddSingleton(monitoring);
                        services.AddSingleton<AnimeAcquisitionScheduler>();
                        services.AddScoped<AcquisitionAccessStore>();
                        services.AddScoped<VideoRequestWorkResolver>();
                        services.AddScoped<IAcquisitionRequestExecutor>(_ => new StubExecutor(MediaAcquisitionKind.Book));
                        services.AddScoped<IAcquisitionRequestExecutor>(_ => new StubExecutor(MediaAcquisitionKind.Manga));
                        services.AddScoped<AcquisitionRequestService>();
                        services.AddScoped<WantedListService>();
                        services.AddSingleton<IJularrEventPublisher, RecordingEventPublisher>();
                    })
                    .Configure(app =>
                    {
                        // Stand-in for cookie sign-in: the owner header makes the caller the owner, otherwise a plain user.
                        app.Use(async (context, next) =>
                        {
                            var owner = context.Request.Headers.ContainsKey(OwnerHeader);
                            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, owner ? "test-owner" : Profile) };
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

            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            return new WantedHost(root, db, monitoring, host, host.GetTestServer());
        }

        /// <summary>
        /// Five requests in the worklist (requested, missing, downloading and failed; two of them Book or Manga
        /// with an executor, one Movie without), three that are not, and two monitored anime episodes. Returns the anime id.
        /// </summary>
        public async Task<Guid> SeedAsync()
        {
            var store = new AcquisitionAccessStore(Db);

            async Task<Guid> AddAsync(
                MediaAcquisitionKind kind,
                string title,
                AcquisitionRequestStatus status,
                string? payload = null,
                string? message = null)
            {
                var request = await store.CreateAsync(
                    new AcquisitionRequestDraft(kind, "test", Guid.NewGuid().ToString("N"), title, null, null, payload),
                    Profile,
                    status,
                    "owner",
                    CancellationToken.None);
                if (message is not null)
                {
                    await store.UpdateStatusAsync(request.Id, status, message, null, null, null, CancellationToken.None);
                }

                return request.Id;
            }

            await AddAsync(MediaAcquisitionKind.Book, "Project Hail Mary", AcquisitionRequestStatus.Approved);
            await AddAsync(
                MediaAcquisitionKind.Manga,
                "Berserk",
                AcquisitionRequestStatus.Approved,
                """{"title":"Berserk","aliases":[],"requestedVolume":42,"preferredLanguages":["de"],"searches":2}""",
                "No release yet.");
            await AddAsync(MediaAcquisitionKind.LightNovel, "Mushoku Tensei", AcquisitionRequestStatus.Downloading, message: "Downloading a release.");
            await AddAsync(MediaAcquisitionKind.Book, "Dune Novel", AcquisitionRequestStatus.Failed, message: "No Usenet indexer is configured.");
            await AddAsync(MediaAcquisitionKind.Movie, "Dune: Part Two", AcquisitionRequestStatus.Approved);
            await AddAsync(MediaAcquisitionKind.Book, "Pending Book", AcquisitionRequestStatus.Pending);
            await AddAsync(MediaAcquisitionKind.Book, "Completed Book", AcquisitionRequestStatus.Completed);
            await AddAsync(MediaAcquisitionKind.Book, "Rejected Book", AcquisitionRequestStatus.Rejected);

            var anime = new Anime { Key = "frieren", Title = "Frieren" };
            Db.Anime.Add(anime);
            await Db.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            var missing = new AnimeEpisodeKey("frieren", 1, 28);
            var retrying = new AnimeEpisodeKey("frieren", 1, 27);
            await Monitoring.UpdateAsync(state =>
            {
                state.Wanted[missing.ToString()] = new WantedUnit(missing, WantedReason.Missing, now.AddDays(-2));
                state.Wanted[retrying.ToString()] = new WantedUnit(retrying, WantedReason.CutoffUnmet, now.AddDays(-3));
                state.Attempts[retrying.ToString()] = new AcquisitionAttempt(
                    retrying,
                    AcquisitionAttemptStatus.Failed,
                    "release",
                    2,
                    now.AddHours(-5),
                    now.AddHours(3));
                return state;
            });

            return anime.Id;
        }

        public async Task<string> GetHtmlAsync(string path)
        {
            using var client = Client(asOwner: true);
            using var response = await client.GetAsync(path);
            var html = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"GET {path} failed:\n{html}");

            // Razor encodes non-ASCII text as character references; assertions read the text a browser shows.
            return WebUtility.HtmlDecode(html);
        }

        public async Task<HttpStatusCode> GetStatusAsync(string path, bool asOwner)
        {
            using var client = Client(asOwner);
            using var response = await client.GetAsync(path);
            return response.StatusCode;
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
    }
}
