using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.ManualSearch;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Shell;
using Jularr.Web.Frontend;
using Jularr.Web.Pages.Admin;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jularr.Tests;

/// <summary>The shared Admin Manual Search page rendered for Movie and TV: states, tags, explanations, authorization and the select-and-download action.</summary>
[TestClass]
public sealed class ManualSearchPageTests
{
    private const string OwnerHeader = "X-Test-Owner";

    private static Task<VideoAcquisitionTestHost> MovieHostAsync() =>
        VideoAcquisitionTestHost.CreateAsync(
            MediaAcquisitionKind.Movie,
            "Dune",
            2021,
            "438631",
            "Dune.2021.1080p.WEB-DL.x264-GROUP",
            "Dune.2021.720p.WEB-DL.x264-MID",
            moreReleases: ["Dune.2021.480p.WEB-DL.x264-LOW", "Arrival.2016.1080p.WEB-DL.x264-GROUP"]);

    private static async Task<string> IdentityOfAsync(VideoAcquisitionTestHost video, Guid requestId, string title)
    {
        var result = await video.Get<VideoManualSearchService>().SearchAsync(requestId, null, refresh: true, CancellationToken.None);
        return result!.Candidates.Single(candidate => candidate.Title == title).Identity;
    }

    [TestMethod]
    public async Task OwnerSeesEveryCandidateWithItsScoreTagAndReasonAndNeverADownloadUrl()
    {
        await using var video = await MovieHostAsync();
        var request = await video.CreateApprovedAsync();
        await using var page = await PageHost.CreateAsync(video);

        var html = await page.GetHtmlAsync($"/Admin/ManualSearch?id={request.Id}");

        foreach (var title in new[] { "Dune.2021.1080p.WEB-DL.x264-GROUP", "Dune.2021.720p.WEB-DL.x264-MID", "Dune.2021.480p.WEB-DL.x264-LOW", "Arrival.2016.1080p.WEB-DL.x264-GROUP" })
        {
            StringAssert.Contains(html, title);
        }

        StringAssert.Contains(html, "Matches target");
        StringAssert.Contains(html, "Lower quality");
        StringAssert.Contains(html, "Rejected by profile");
        StringAssert.Contains(html, "Wrong title");
        StringAssert.Contains(html, "data-verdict=\"eligible\"");
        StringAssert.Contains(html, "data-verdict=\"warning\"");
        StringAssert.Contains(html, "data-verdict=\"rejected\"");
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(html, "<h1[ >]").Count, "One page heading.");
        Assert.IsFalse(html.Contains("indexer.invalid", StringComparison.Ordinal), "Provider download URLs never reach the browser.");
        Assert.IsFalse(html.Contains("Select and download", StringComparison.Ordinal), "Nothing is selected yet, so nothing can be downloaded.");
        Assert.AreEqual(0, video.Environment.Client.Grabs.Count);
    }

    [TestMethod]
    public async Task InspectingAnEligibleCandidateOffersTheSeparateDownloadActionAndARejectedOneDoesNot()
    {
        await using var video = await MovieHostAsync();
        var request = await video.CreateApprovedAsync();
        await using var page = await PageHost.CreateAsync(video);
        var eligible = await IdentityOfAsync(video, request.Id, "Dune.2021.1080p.WEB-DL.x264-GROUP");
        var rejected = await IdentityOfAsync(video, request.Id, "Arrival.2016.1080p.WEB-DL.x264-GROUP");

        var chosen = await page.GetHtmlAsync($"/Admin/ManualSearch?id={request.Id}&release={Uri.EscapeDataString(eligible)}");
        var refused = await page.GetHtmlAsync($"/Admin/ManualSearch?id={request.Id}&release={Uri.EscapeDataString(rejected)}");

        StringAssert.Contains(chosen, "Select and download");
        StringAssert.Contains(chosen, "handler=Grab");
        StringAssert.Contains(chosen, "name=\"releaseIdentity\"");
        StringAssert.Contains(chosen, "Why this result");
        Assert.IsFalse(refused.Contains("Select and download", StringComparison.Ordinal), "A rejected identity has no download action.");
        StringAssert.Contains(refused, "Wrong title");
    }

    [TestMethod]
    public async Task ASelectedCandidateOpensOneDrawerAndOneExpandedCardThatCloseWithoutDownloading()
    {
        await using var video = await MovieHostAsync();
        var request = await video.CreateApprovedAsync();
        await using var page = await PageHost.CreateAsync(video);
        var eligible = await IdentityOfAsync(video, request.Id, "Dune.2021.1080p.WEB-DL.x264-GROUP");

        var none = await page.GetHtmlAsync($"/Admin/ManualSearch?id={request.Id}");
        var chosen = await page.GetHtmlAsync($"/Admin/ManualSearch?id={request.Id}&release={Uri.EscapeDataString(eligible)}");

        Assert.IsFalse(none.Contains("bms-detail-drawer", StringComparison.Ordinal), "Nothing is selected, so the table has the whole width.");
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(chosen, "<aside class=\"bms-detail bms-detail-drawer\"").Count);
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(chosen, "class=\"bms-card-detail\"").Count);
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(chosen, "class=\"bms-detail-action\"").Count, "The phone card leaves the download action to the sticky bar.");
        StringAssert.Contains(chosen, "aria-label=\"Close details\"");
        StringAssert.Contains(chosen, "data-verdict=\"eligible\"");
        Assert.AreEqual(0, video.Environment.Client.Grabs.Count, "Selecting never downloads.");
    }

    [TestMethod]
    public async Task OnlyAdminsOpenManualSearchAndUnknownRequestsAreNotFound()
    {
        await using var video = await MovieHostAsync();
        var request = await video.CreateApprovedAsync();
        await using var page = await PageHost.CreateAsync(video);

        Assert.AreEqual(HttpStatusCode.Forbidden, await page.GetStatusAsync($"/Admin/ManualSearch?id={request.Id}", asOwner: false));
        Assert.AreEqual(HttpStatusCode.NotFound, await page.GetStatusAsync($"/Admin/ManualSearch?id={Guid.NewGuid()}", asOwner: true));
        Assert.AreEqual(0, video.Environment.Client.Grabs.Count);
    }

    [TestMethod]
    public async Task ACurrentAndHistoryTabShowTheTargetAndItsAcquisitionHistory()
    {
        await using var video = await MovieHostAsync();
        var request = await video.StartAsync();
        await using var page = await PageHost.CreateAsync(video);

        var current = await page.GetHtmlAsync($"/Admin/ManualSearch?id={request.Id}&tab=current");
        var history = await page.GetHtmlAsync($"/Admin/ManualSearch?id={request.Id}&tab=history");
        var search = await page.GetHtmlAsync($"/Admin/ManualSearch?id={request.Id}&tab=search");

        StringAssert.Contains(current, "Movies 1080p");
        StringAssert.Contains(current, "Local file");
        StringAssert.Contains(history, "Request created");
        StringAssert.Contains(history, "/Admin/Operation/");
        StringAssert.Contains(search, "not waiting for a release", "A request that already downloads is not searched; the tab explains why.");
        Assert.IsFalse(search.Contains("bms-table", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ATvRequestOffersTheMissingEpisodesAndSearchesTheChosenOne()
    {
        await using var video = await VideoAcquisitionTestHost.CreateAsync(
            MediaAcquisitionKind.Tv,
            "Severance",
            2022,
            "95396",
            "Severance.S01E02.1080p.WEB-DL.x264-GROUP",
            "Severance.S01.1080p.WEB-DL.x264-PACK",
            addEpisode: true,
            addSecondEpisode: true,
            moreReleases: ["Severance.S01E03.1080p.WEB-DL.x264-WRONGEP"]);
        var request = await video.CreateApprovedAsync();
        await using var page = await PageHost.CreateAsync(video);

        var html = await page.GetHtmlAsync($"/Admin/ManualSearch?id={request.Id}&unit={video.SecondEpisodeId}");

        StringAssert.Contains(html, "S01E01");
        StringAssert.Contains(html, "S01E02");
        StringAssert.Contains(html, "name=\"unit\"");
        StringAssert.Contains(html, "Season pack");
        StringAssert.Contains(html, "Contains target");
        StringAssert.Contains(html, "Wrong episode");
    }

    [TestMethod]
    public async Task AStaleEpisodeLinkShowsTheChangedTargetInsteadOfSearching()
    {
        await using var video = await VideoAcquisitionTestHost.CreateAsync(
            MediaAcquisitionKind.Tv,
            "Severance",
            2022,
            "95396",
            "Severance.S01E02.1080p.WEB-DL.x264-GROUP",
            addEpisode: true,
            addSecondEpisode: true);
        var request = await video.CreateApprovedAsync();
        await using var page = await PageHost.CreateAsync(video);

        var html = await page.GetHtmlAsync($"/Admin/ManualSearch?id={request.Id}&unit={Guid.NewGuid()}");

        StringAssert.Contains(html, "no longer missing");
        StringAssert.Contains(html, "name=\"unit\"");
        Assert.IsFalse(html.Contains("bms-table", StringComparison.Ordinal));
        Assert.AreEqual(0, video.Environment.Client.Grabs.Count);
    }

    [TestMethod]
    public async Task SelectingTwiceSendsOneDownloadAndAnUnavailableReleaseLeavesAnExplanation()
    {
        await using var video = await MovieHostAsync();
        var request = await video.CreateApprovedAsync();
        var eligible = await IdentityOfAsync(video, request.Id, "Dune.2021.1080p.WEB-DL.x264-GROUP");
        var rejected = await IdentityOfAsync(video, request.Id, "Arrival.2016.1080p.WEB-DL.x264-GROUP");

        var refusedPage = await PostAsync(video, request.Id, rejected);
        var first = await PostAsync(video, request.Id, eligible);
        var second = await PostAsync(video, request.Id, eligible);

        Assert.AreEqual("/Admin/Wanted", Assert.IsInstanceOfType<RedirectToPageResult>(first.Result).PageName);
        Assert.AreEqual("Release sent to the download client.", first.Page.TempData["Status"]);
        Assert.AreEqual("/Admin/Wanted", Assert.IsInstanceOfType<RedirectToPageResult>(second.Result).PageName);
        Assert.AreEqual("This release was already sent to the download client.", second.Page.TempData["Status"]);
        Assert.AreEqual(1, video.Environment.Client.Grabs.Count, "A double submit never creates a second download.");
        Assert.IsNull(refusedPage.Page.TempData["Status"]);
        StringAssert.Contains((string)refusedPage.Page.TempData["ManualSearchError"]!, "no longer an eligible candidate");

        Assert.IsInstanceOfType<BadRequestResult>((await PostAsync(video, request.Id, " ")).Result);
        Assert.IsInstanceOfType<BadRequestResult>((await PostAsync(video, request.Id, new string('x', 600))).Result);
        Assert.IsInstanceOfType<NotFoundResult>((await PostAsync(video, Guid.NewGuid(), eligible)).Result);
    }

    private static async Task<(IActionResult Result, ManualSearchModel Page)> PostAsync(VideoAcquisitionTestHost video, Guid requestId, string identity)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Role, AccountRoles.Owner)], "test"))
        };
        var page = new ManualSearchModel(video.Environment.Db, video.Get<VideoManualSearchService>())
        {
            PageContext = new PageContext { HttpContext = httpContext, ViewData = new ViewDataDictionary<ManualSearchModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) },
            TempData = new TempDataDictionary(httpContext, new NoTempData())
        };
        return (await page.OnPostGrabAsync(requestId, null, identity, CancellationToken.None), page);
    }

    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
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

    private sealed class PageHost : IAsyncDisposable
    {
        private readonly IHost host;
        private readonly TestServer server;

        private PageHost(IHost host, TestServer server)
        {
            this.host = host;
            this.server = server;
        }

        public static async Task<PageHost> CreateAsync(VideoAcquisitionTestHost video)
        {
            var host = await new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .UseContentRoot(FindWebProjectRoot())
                    .ConfigureServices(services =>
                    {
                        // The same registrations the engine tests run on, served over HTTP with the signed-in account of each request.
                        foreach (var descriptor in video.Descriptors)
                        {
                            services.Add(descriptor);
                        }

                        services.RemoveAll<CurrentAccountContext>();
                        services.AddHttpContextAccessor();
                        services.AddSingleton<CurrentAccountContext>();
                        services.AddRazorPages().AddApplicationPart(typeof(ManualSearchModel).Assembly);
                        services.AddAuthorization(options => JularrPolicies.Register(options));
                        services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ForbiddenAnswerHandler>("test", _ => { });
                        services.AddLogging();
                        services.AddSingleton<ViteAssetManifest>();
                        services.AddScoped<IAppShellService, AppShellService>();
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
            return new PageHost(host, host.GetTestServer());
        }

        public async Task<string> GetHtmlAsync(string path)
        {
            using var client = server.CreateClient();
            client.DefaultRequestHeaders.Add(OwnerHeader, "true");
            using var response = await client.GetAsync(path);
            var html = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"GET {path} failed:\n{html}");
            return WebUtility.HtmlDecode(html);
        }

        public async Task<HttpStatusCode> GetStatusAsync(string path, bool asOwner)
        {
            using var client = server.CreateClient();
            if (asOwner)
            {
                client.DefaultRequestHeaders.Add(OwnerHeader, "true");
            }

            using var response = await client.GetAsync(path);
            return response.StatusCode;
        }

        public async ValueTask DisposeAsync()
        {
            server.Dispose();
            await host.StopAsync();
            host.Dispose();
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
