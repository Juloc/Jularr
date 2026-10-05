using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Collections;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Metadata;
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

/// <summary>
/// The request pages (#597) rendered end to end through a real HTTP request: the per-user history at
/// <c>/Requests</c>, the request-with-options form at <c>/Requests/New</c> and the owner's
/// <c>/Admin/Requests</c> with its auto-approval rules.
/// </summary>
[TestClass]
public sealed class RequestPagesRenderTests
{
    [TestMethod]
    public async Task HistoryShowsTheSignedInProfilesRequestsWithTheirStateOptionsAndWithdrawAction()
    {
        await using var host = await RequestPagesHost.CreateAsync();
        var store = new AcquisitionAccessStore(host.Db);
        var options = new AcquisitionRequestOptions
        {
            Scope = RequestScope.Seasons,
            Seasons = [1, 2],
            AudioLanguage = "ja",
            SubtitleLanguage = "de"
        }.Validate();
        await store.CreateAsync(
            Anime("1", "Frieren", options),
            RequestPagesHost.Profile,
            AcquisitionRequestStatus.Pending,
            null,
            CancellationToken.None);
        await store.CreateAsync(
            Anime("2", "Dungeon Meshi"),
            RequestPagesHost.Profile,
            AcquisitionRequestStatus.Completed,
            AcquisitionAutoApproval.DecidedBy("rule1"),
            CancellationToken.None);
        await store.CreateAsync(Anime("3", "Someone Elses Show"), "another-profile", AcquisitionRequestStatus.Pending, null, CancellationToken.None);

        var all = await host.GetHtmlAsync("/Requests", asOwner: false);

        StringAssert.Contains(all, "My requests");
        StringAssert.Contains(all, "Frieren");
        StringAssert.Contains(all, "Dungeon Meshi");
        Assert.IsFalse(all.Contains("Someone Elses Show", StringComparison.Ordinal), "Another profile's request is not shown.");
        StringAssert.Contains(all, "Seasons 1-2");
        StringAssert.Contains(all, "Audio: 日本語");
        StringAssert.Contains(all, "Subtitles: Deutsch");
        StringAssert.Contains(all, "Approved automatically");
        StringAssert.Contains(all, "Withdraw");
        StringAssert.Contains(all, "request-status-pending");
        StringAssert.Contains(all, "request-status-completed");
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(all, "class=\"button\" type=\"submit\">Withdraw").Count, "Only the pending request can be withdrawn.");

        var finished = await host.GetHtmlAsync("/Requests?show=finished", asOwner: false);
        StringAssert.Contains(finished, "Dungeon Meshi");
        Assert.IsFalse(finished.Contains("Frieren", StringComparison.Ordinal));

        var empty = await host.GetHtmlAsync("/Requests", asOwner: true);
        StringAssert.Contains(empty, "No requests to show.");
    }

    [TestMethod]
    public async Task RequestFormOffersTheOptionsAndTheSameRequestActionForEveryCapability()
    {
        await using var host = await RequestPagesHost.CreateAsync();
        await host.Settings.SetRequesterQualityProfilesAsync([AnimeQualityProfiles.DefaultAnime1080pId]);
        const string path = "/Requests/New?externalId=154587&title=Frieren&subtitle=TV";

        var user = await host.GetHtmlAsync(path, asOwner: false);

        StringAssert.Contains(user, "Request with options");
        StringAssert.Contains(user, "Whole series");
        StringAssert.Contains(user, "Selected seasons");
        StringAssert.Contains(user, "Selected episodes");
        StringAssert.Contains(user, "name=\"Seasons\"");
        StringAssert.Contains(user, "name=\"Episodes\"");
        StringAssert.Contains(user, "日本語");
        StringAssert.Contains(user, "No subtitles");
        StringAssert.Contains(user, "Anime 1080p");
        StringAssert.Matches(user, new System.Text.RegularExpressions.Regex(@"type=""submit"">\s*Request\s*</button>"));

        // Only the profiles the owner opened are offered to a requester; the owner sees the same Request action.
        var noProfiles = await host.GetHtmlAsync(path, asOwner: false, mediaCapability: MediaCapability.Request, openProfiles: false);
        Assert.IsFalse(noProfiles.Contains("name=\"QualityProfileId\"", StringComparison.Ordinal));
        var owner = await host.GetHtmlAsync(path, asOwner: true);
        StringAssert.Contains(owner, "name=\"QualityProfileId\"");
        StringAssert.Matches(owner, new System.Text.RegularExpressions.Regex(@"type=""submit"">\s*Request\s*</button>"));
        Assert.IsFalse(owner.Contains(">Add<", StringComparison.Ordinal), "Instant approval never becomes another action.");

        var refused = await host.GetStatusAsync(path, asOwner: false, MediaCapability.Browse);
        Assert.AreEqual(HttpStatusCode.Forbidden, refused);
        Assert.AreEqual(HttpStatusCode.BadRequest, await host.GetStatusAsync("/Requests/New?title=NoId", asOwner: false));
    }

    [TestMethod]
    public async Task AdminQueueShowsAutoApprovalRulesOptionsAndWhoApproved()
    {
        await using var host = await RequestPagesHost.CreateAsync();
        var store = new AcquisitionAccessStore(host.Db);
        var rule = AutoApprovalRule.Create("Trusted friends", [MediaAcquisitionKind.Anime], [RequestPagesHost.Profile], new AutoApprovalQuota(3, 7));
        await host.Settings.AddRuleAsync(rule);
        await host.Settings.AddRuleAsync(AutoApprovalRule.Create("Off for now", [], [], null) with { Enabled = false });
        await store.CreateAsync(
            Anime("1", "Frieren", new AcquisitionRequestOptions { Scope = RequestScope.Episodes, Episodes = [new RequestEpisode(1, 3)] }.Validate()),
            RequestPagesHost.Profile,
            AcquisitionRequestStatus.Downloading,
            AcquisitionAutoApproval.DecidedBy(rule.Id),
            CancellationToken.None);

        var html = await host.GetHtmlAsync("/Admin/Requests", asOwner: true);

        StringAssert.Contains(html, "Auto-approval");
        StringAssert.Contains(html, "Trusted friends");
        StringAssert.Contains(html, "3 per 7 days");
        StringAssert.Contains(html, "Off for now");
        StringAssert.Contains(html, "Switch on");
        StringAssert.Contains(html, "Add rule");
        StringAssert.Contains(html, "Episodes S01E03");
        StringAssert.Contains(html, "Approved automatically");
        StringAssert.Contains(html, "Manual adding");
        StringAssert.Contains(html, "Quality profiles for requests");
        Assert.IsFalse(html.Contains("Adding from search", StringComparison.Ordinal), "The request-versus-instant rule now lives in the capability matrix.");
        StringAssert.Contains(html, "/Admin/Capabilities");
    }

    [TestMethod]
    public async Task AdminQueueOffersTabsFiltersAndTheActionOfEachState()
    {
        await using var host = await RequestPagesHost.CreateAsync();
        var store = new AcquisitionAccessStore(host.Db);
        var german = new AcquisitionRequestOptions { AudioLanguage = "de" }.Validate();
        await store.CreateAsync(Anime("1", "Pending Show", german), RequestPagesHost.Profile, AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        await store.CreateAsync(Anime("2", "Downloading Show"), RequestPagesHost.Profile, AcquisitionRequestStatus.Downloading, "owner", CancellationToken.None);
        await store.CreateAsync(Anime("3", "Rejected Show"), RequestPagesHost.Profile, AcquisitionRequestStatus.Rejected, "owner", CancellationToken.None);

        var all = await host.GetHtmlAsync("/Admin/Requests", asOwner: true);

        foreach (var tab in new[] { "All", "Open", "Approved", "In progress", "Done", "Rejected" })
        {
            StringAssert.Contains(all, tab);
        }

        StringAssert.Contains(all, "Pending Show");
        StringAssert.Contains(all, "Deutsch");
        StringAssert.Contains(all, "data-status=\"pending\"");
        StringAssert.Contains(all, "data-status=\"downloading\"");
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(all, "button-primary\" type=\"submit\">\\s*Approve\\s*</button>").Count, "Only the pending request can be approved.");
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(all, "type=\"submit\">Reject</button>").Count);
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(all, "type=\"submit\">Reopen</button>").Count);
        StringAssert.Contains(all, "class=\"admin-menu-item\" href=\"/Admin/Wanted\"");
        Assert.IsFalse(all.Contains("/Acquisition#wanted", StringComparison.Ordinal));

        var rejected = await host.GetHtmlAsync("/Admin/Requests?tab=rejected", asOwner: true);
        StringAssert.Contains(rejected, "Rejected Show");
        Assert.IsFalse(rejected.Contains("Pending Show", StringComparison.Ordinal));

        var byLanguage = await host.GetHtmlAsync("/Admin/Requests?lang=de", asOwner: true);
        StringAssert.Contains(byLanguage, "Pending Show");
        Assert.IsFalse(byLanguage.Contains("Downloading Show", StringComparison.Ordinal));

        var none = await host.GetHtmlAsync("/Admin/Requests?q=zzz", asOwner: true);
        StringAssert.Contains(none, "No requests match these filters.");
    }

    [TestMethod]
    public async Task LibraryCardsShowTheAvailabilityBadgeOnlyWhereItAddsSomething()
    {
        await using var host = await RequestPagesHost.CreateAsync();
        var seed = new LibraryCanonicalSeed(host.Db);
        await seed.AddAnimeAsync("Playable Show", [(1, 1, true)]);
        await seed.AddAnimeAsync("Empty Show", [(1, 1, false)]);
        var requested = await seed.AddAnimeAsync("Requested Show", [(1, 1, false)]);
        host.Db.AnimeMetadata.Add(new AnimeMetadata
        {
            AnimeId = requested.Anime.Id,
            Provider = "anilist",
            ExternalId = "42",
            PreferredTitle = requested.Anime.Title
        });
        await host.Db.SaveChangesAsync();        await new AcquisitionAccessStore(host.Db).CreateAsync(
            Anime("42", "Requested Show"),
            "someone",
            AcquisitionRequestStatus.Downloading,
            "owner",
            CancellationToken.None);

        var html = await host.GetHtmlAsync("/Library", asOwner: false);

        var cards = html.Split("<article class=\"lib-card", StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        Assert.AreEqual(3, cards.Length);
        string CardOf(string title) => cards.Single(card => card.Contains(title, StringComparison.Ordinal));
        Assert.IsFalse(CardOf("Playable Show").Contains("lib-state", StringComparison.Ordinal), "A complete title carries no marker.");
        StringAssert.Contains(CardOf("Empty Show"), "lib-state-missing");
        StringAssert.Contains(CardOf("Empty Show"), "Not available");
        StringAssert.Contains(CardOf("Requested Show"), "lib-state-requested");
        StringAssert.Contains(CardOf("Requested Show"), "Getting media");
    }

    private static AcquisitionRequestDraft Anime(string id, string title, AcquisitionRequestOptions? options = null) =>
        new(
            MediaAcquisitionKind.Anime,
            "anilist",
            id,
            title,
            null,
            null,
            options?.ToPayloadJson());

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

    private sealed class RequestPagesHost : IAsyncDisposable
    {
        public const string Profile = "test-profile";
        private const string OwnerHeader = "X-Test-Owner";
        private const string CapabilityHeader = "X-Test-Capability";

        private readonly string root;
        private readonly IHost host;
        private readonly TestServer server;

        private RequestPagesHost(string root, AppDbContext db, AcquisitionRequestSettingsStore settings, IHost host, TestServer server)
        {
            this.root = root;
            Db = db;
            Settings = settings;
            this.host = host;
            this.server = server;
        }

        public AppDbContext Db { get; }
        public AcquisitionRequestSettingsStore Settings { get; }

        public static async Task<RequestPagesHost> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"jularr-request-pages-{Guid.NewGuid():N}");
            var data = Directory.CreateDirectory(Path.Combine(root, "data"));
            var connectionString = $"Data Source={Path.Combine(root, "jularr.db")};Foreign Keys=True";
            var capabilities = new MediaCapabilityStore(data.FullName);
            var settings = new AcquisitionRequestSettingsStore(data.FullName);

            var host = await new HostBuilder()
                .ConfigureWebHost(webBuilder => webBuilder
                    .UseTestServer()
                    .UseContentRoot(FindWebProjectRoot())
                    .ConfigureServices(services =>
                    {
                        services
                            .AddRazorPages()
                            .AddApplicationPart(typeof(Jularr.Web.Pages.Requests.IndexModel).Assembly);
                        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
                        services.AddHttpContextAccessor();
                        services.AddAuthorization(options => JularrPolicies.Register(options));
                        // Forbid() needs a scheme that can answer it; the test answers with 403.
                        services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ForbiddenAnswerHandler>("test", _ => { });
                        services.AddLogging();
                        services.AddSingleton<ViteAssetManifest>();
                        services.AddScoped<CurrentAccountContext>();
                        services.AddSingleton(capabilities);
                        services.AddScoped<IMediaCapabilityService, MediaCapabilityService>();
                        services.AddScoped<IAppShellService, AppShellService>();
                        services.AddSingleton(settings);
                        services.AddSingleton(new QualityProfileStore(new DirectoryInfo(Path.Combine(data.FullName, "quality"))));
                        services.AddScoped<AcquisitionAccessStore>();
                        services.AddScoped<VideoRequestWorkResolver>();
                        // The Library page's Collections view.
                        services.AddScoped<Jularr.Web.Features.MediaFacts.MediaFactsService>();
                        services.AddScoped<Jularr.Web.Features.Franchises.FranchiseStore>();
                        services.AddCollections();
                        services.AddScoped<RequestHistoryQuery>();
                        services.AddScoped<AcquisitionRequestService>();
                        services.AddSingleton<IJularrEventPublisher, RecordingEventPublisher>();
                    })
                    .Configure(app =>
                    {
                        // Stand-in for cookie sign-in: headers choose owner or user and the anime capability.
                        app.Use(async (context, next) =>
                        {
                            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, context.Request.Headers.ContainsKey(OwnerHeader) ? "test-owner" : Profile) };
                            if (context.Request.Headers.ContainsKey(OwnerHeader))
                            {
                                claims.Add(new Claim(ClaimTypes.Role, AccountRoles.Owner));
                            }
                            else if (context.Request.Headers.TryGetValue(CapabilityHeader, out var value)
                                     && MediaCapabilityNames.TryParse(value) is { } capability)
                            {
                                await capabilities.SetRoleDefaultAsync(
                                    AccountRole.User,
                                    Jularr.Web.Features.MediaCore.WorkMediaType.Anime,
                                    capability);
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
            return new RequestPagesHost(root, db, settings, host, host.GetTestServer());
        }

        public async Task<string> GetHtmlAsync(
            string path,
            bool asOwner,
            MediaCapability mediaCapability = MediaCapability.Request,
            bool openProfiles = true)
        {
            if (!openProfiles)
            {
                await Settings.SetRequesterQualityProfilesAsync([]);
            }

            using var client = Client(asOwner, mediaCapability);
            using var response = await client.GetAsync(path);
            var html = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"GET {path} (owner={asOwner}) failed:\n{html}");

            // Razor encodes non-ASCII text as character references; assertions read the text a browser shows.
            return WebUtility.HtmlDecode(html);
        }

        public async Task<HttpStatusCode> GetStatusAsync(
            string path,
            bool asOwner,
            MediaCapability mediaCapability = MediaCapability.Request)
        {
            using var client = Client(asOwner, mediaCapability);
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

        private HttpClient Client(bool asOwner, MediaCapability mediaCapability)
        {
            var client = server.CreateClient();
            if (asOwner)
            {
                client.DefaultRequestHeaders.Add(OwnerHeader, "true");
            }
            else
            {
                client.DefaultRequestHeaders.Add(CapabilityHeader, MediaCapabilityNames.ToStorage(mediaCapability));
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
