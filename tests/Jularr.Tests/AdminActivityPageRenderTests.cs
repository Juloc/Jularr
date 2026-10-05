using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Shell;
using Jularr.Web.Frontend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Jularr.Tests;

/// <summary>Admin → Activity rendered end to end through a real HTTP request.</summary>
[TestClass]
public sealed class AdminActivityPageRenderTests
{
    [TestMethod]
    public async Task TheToDoTabShowsRunningQueuedAndFailedWorkWithTabCountsAndNoPauseControl()
    {
        await using var host = await ActivityHost.CreateAsync();
        await host.CreateAsync("Frieren import", OperationStatus.Running, "anime-import", progress: 68, message: "Importing episodes");
        await host.CreateAsync("Remux episode", OperationStatus.Queued, "media-optimization");
        await host.CreateAsync("Nightly scan", OperationStatus.Failed);
        await host.CreateAsync("Old scan", OperationStatus.Succeeded);

        var html = await host.GetHtmlAsync("/Admin/Operations");

        foreach (var text in new[] { "Activity / To-Do", "To-Do", "Running", "Failed", "History", "All" })
        {
            StringAssert.Contains(html, text);
        }

        StringAssert.Contains(html, "Frieren import");
        StringAssert.Contains(html, "Importing episodes");
        StringAssert.Contains(html, "68%");
        StringAssert.Contains(html, "admact-tag-imports");
        StringAssert.Contains(html, "admact-tag-remux");
        StringAssert.Contains(html, "admact-state-running");
        StringAssert.Contains(html, "admact-state-queued");
        StringAssert.Contains(html, "admact-state-failed");
        StringAssert.Contains(html, "Something broke.");
        StringAssert.Contains(html, "1–3 of 3");
        Assert.IsFalse(html.Contains("Old scan", StringComparison.Ordinal), "Finished work is not to-do.");
        StringAssert.Contains(html, "returnUrl=%2FAdmin%2FOperations");
        Assert.AreEqual("3", Regex.Match(html, "To-Do <span class=\"admact-count\">(\\d+)</span>").Groups[1].Value);
        Assert.AreEqual("4", Regex.Match(html, "All <span class=\"admact-count\">(\\d+)</span>").Groups[1].Value);
        var section = html[html.IndexOf("<section class=\"admact\"", StringComparison.Ordinal)..];
        Assert.IsFalse(section.Contains("Pause", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(html, "href=\"/Admin/History\"");
    }

    [TestMethod]
    public async Task TabsTypeStatusAndSearchNarrowTheListAndTheOtherTabsStayCounted()
    {
        await using var host = await ActivityHost.CreateAsync();
        await host.CreateAsync("Frieren import", OperationStatus.Running, "anime-import");
        await host.CreateAsync("Dungeon import", OperationStatus.Failed, "anime-import");
        await host.CreateAsync("Remux episode", OperationStatus.Interrupted, "media-optimization");
        await host.CreateAsync("Nightly scan", OperationStatus.Succeeded);

        var running = await host.GetHtmlAsync("/Admin/Operations?tab=running");
        StringAssert.Contains(running, "Frieren import");
        Assert.IsFalse(running.Contains("Dungeon import", StringComparison.Ordinal));
        Assert.IsFalse(running.Contains("name=\"status\"", StringComparison.Ordinal), "One status needs no status filter.");

        var failed = await host.GetHtmlAsync("/Admin/Operations?tab=failed");
        StringAssert.Contains(failed, "Dungeon import");
        StringAssert.Contains(failed, "Remux episode");
        Assert.IsFalse(failed.Contains("Frieren import", StringComparison.Ordinal));

        var interrupted = await host.GetHtmlAsync("/Admin/Operations?tab=failed&status=interrupted");
        StringAssert.Contains(interrupted, "Remux episode");
        Assert.IsFalse(interrupted.Contains("Dungeon import", StringComparison.Ordinal));

        var imports = await host.GetHtmlAsync("/Admin/Operations?type=imports");
        StringAssert.Contains(imports, "Frieren import");
        StringAssert.Contains(imports, "Dungeon import");
        Assert.IsFalse(imports.Contains("Remux episode", StringComparison.Ordinal));
        Assert.AreEqual("2", Regex.Match(imports, "To-Do <span class=\"admact-count\">(\\d+)</span>").Groups[1].Value);

        var all = await host.GetHtmlAsync("/Admin/Operations?tab=all");
        StringAssert.Contains(all, "Nightly scan");
        StringAssert.Contains(all, "admact-state-succeeded");

        var searched = await host.GetHtmlAsync("/Admin/Operations?q=dungeon");
        StringAssert.Contains(searched, "Dungeon import");
        Assert.IsFalse(searched.Contains("Frieren import", StringComparison.Ordinal));

        StringAssert.Contains(await host.GetHtmlAsync("/Admin/Operations?q=zzz"), "No jobs match these filters.");
        StringAssert.Contains(await host.GetHtmlAsync("/Admin/Operations?tab=running&status=failed"), "Frieren import");
    }

    [TestMethod]
    public async Task RetryAndCancelAreOnlyOfferedWhereTheBackendCanDoThem()
    {
        await using var host = await ActivityHost.CreateAsync();
        var cancellable = await host.CreateAsync("Cancellable job", OperationStatus.Running);
        var attached = await host.CreateAsync("Retryable job", OperationStatus.Failed);
        await host.CreateAsync("Detached failed job", OperationStatus.Failed);
        host.Actions.Cancellable.Add(cancellable);
        host.Actions.Retryable.Add(attached);

        var html = await host.GetHtmlAsync("/Admin/Operations");

        Assert.AreEqual(1, Regex.Matches(html, "handler=Cancel").Count);
        Assert.AreEqual(1, Regex.Matches(html, "handler=Retry").Count);
        Assert.AreEqual(3, Regex.Matches(html, "/Admin/Operation/").Count, "Every job links to its details.");
    }

    [TestMethod]
    public async Task PriorityIsShownAndOnlyWaitingWorkOfTheServerCanBeChanged()
    {
        await using var host = await ActivityHost.CreateAsync();
        var waiting = await host.CreateAsync("Waiting job", OperationStatus.Queued, priority: OperationPriority.Low);
        await host.CreateAsync("Held elsewhere", OperationStatus.Queued, priority: OperationPriority.High);
        await host.CreateAsync("Running job", OperationStatus.Running);
        host.Actions.Reprioritizable.Add(waiting);

        var html = await host.GetHtmlAsync("/Admin/Operations");

        Assert.AreEqual(1, Regex.Matches(html, "handler=Priority").Count, "Only the job the worker still holds offers the change.");
        StringAssert.Contains(html, ">Priority</th>");
        StringAssert.Contains(html, "admact-priority-high");
        Assert.IsTrue(Regex.IsMatch(html, "<option value=\"low\" selected[^>]*>Low</option>"), "The form starts at the current priority.");
        Assert.AreEqual(6, Regex.Matches(html, "<option value=\"(?:low|normal|high)\"").Count, "Three levels in the change form and three in the filter.");

        var high = await host.GetHtmlAsync("/Admin/Operations?priority=high");
        StringAssert.Contains(high, "Held elsewhere");
        Assert.IsFalse(high.Contains("Waiting job", StringComparison.Ordinal));
        Assert.AreEqual("1", Regex.Match(high, "To-Do <span class=\"admact-count\">(\\d+)</span>").Groups[1].Value);
    }

    [TestMethod]
    public async Task ChangingThePriorityStoresItAndReturnsToTheFilteredList()
    {
        await using var host = await ActivityHost.CreateAsync();
        var waiting = await host.CreateAsync("Waiting job", OperationStatus.Queued);
        var refused = await host.CreateAsync("Held elsewhere", OperationStatus.Queued);
        host.Actions.Reprioritizable.Add(waiting);

        var page = await host.GetAsync("/Admin/Operations");
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var cookie = string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));

        Dictionary<string, string> Form(Guid id, string priority) => new()
        {
            ["id"] = id.ToString("D"),
            ["priority"] = priority,
            ["returnUrl"] = "/Admin/Operations?tab=all",
            ["__RequestVerificationToken"] = token
        };

        var changed = await host.PostAsync("/Admin/Operations?handler=Priority", cookie, Form(waiting, "high"));
        Assert.AreEqual(HttpStatusCode.Redirect, changed.StatusCode);
        Assert.AreEqual("/Admin/Operations?tab=all", changed.Headers.Location?.OriginalString);
        Assert.AreEqual(OperationPriority.High, (await new OperationStore(host.Db).GetAsync(waiting))!.Priority);

        var declined = await host.PostAsync("/Admin/Operations?handler=Priority", cookie, Form(refused, "high"));
        Assert.AreEqual(HttpStatusCode.Redirect, declined.StatusCode);
        Assert.AreEqual(OperationPriority.Normal, (await new OperationStore(host.Db).GetAsync(refused))!.Priority);

        var unknown = await host.PostAsync("/Admin/Operations?handler=Priority", cookie, Form(waiting, "urgent"));
        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [TestMethod]
    public async Task RepackWorkIsAJobTypeOfItsOwn()
    {
        await using var host = await ActivityHost.CreateAsync();
        await host.CreateAsync("Recover swap", OperationStatus.Queued, "media-optimization-recovery");
        await host.CreateAsync("Remux episode", OperationStatus.Queued, "media-optimization");

        var repack = await host.GetHtmlAsync("/Admin/Operations?type=repack");

        StringAssert.Contains(repack, "Recover swap");
        StringAssert.Contains(repack, "admact-tag-repack");
        Assert.IsFalse(repack.Contains("Remux episode", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MovieAndTvDownloadsAreAcquisitionWorkLinkedToTheirAdminMediaPage()
    {
        await using var host = await ActivityHost.CreateAsync();
        var movie = new Work { MediaType = WorkMediaType.Movie, CanonicalTitle = "Dune" };
        var series = new Work { MediaType = WorkMediaType.Series, CanonicalTitle = "Severance" };
        var episode = new WorkEpisode { WorkId = series.Id, SeasonNumber = 1, EpisodeNumber = 2 };
        host.Db.Works.AddRange(movie, series);
        host.Db.WorkEpisodes.Add(episode);
        await host.Db.SaveChangesAsync();
        var store = new OperationStore(host.Db);

        async Task DownloadAsync(string title, string subject, MediaAcquisitionKind kind, string target)
        {
            var details = new DownloadOperationDetails(Guid.NewGuid(), kind, "movies", TargetKey: target).Serialize();
            var id = await store.CreateAsync(new OperationDescriptor(VideoAcquisitionEngine.OperationKind, "External downloads", title, subject, IsDownload: true, Details: details));
            await store.MarkRunningAsync(id);
        }

        await DownloadAsync("Download Movie", "Dune", MediaAcquisitionKind.Movie, VideoWorkLinks.WorkTarget(movie.Id));
        await DownloadAsync("Download TV", "Severance", MediaAcquisitionKind.Tv, VideoWorkLinks.EpisodeTarget(episode.Id));

        var html = await host.GetHtmlAsync("/Admin/Operations");

        StringAssert.Contains(html, "Download Movie");
        StringAssert.Contains(html, "Download TV");
        Assert.AreEqual(2, Regex.Matches(html, "admact-tag-acquisition").Count, "Both are acquisition work.");
        StringAssert.Contains(html, $"href=\"/Admin/Media/movie/{movie.Id:D}\"");
        StringAssert.Contains(html, $"href=\"/Admin/Media/series/{series.Id:D}\"");
    }

    [TestMethod]
    public async Task RetryAndCancelRunTheActionAndReturnToTheFilteredList()
    {
        await using var host = await ActivityHost.CreateAsync();
        var running = await host.CreateAsync("Cancellable job", OperationStatus.Running);
        var failed = await host.CreateAsync("Retryable job", OperationStatus.Failed);
        host.Actions.Cancellable.Add(running);
        host.Actions.Retryable.Add(failed);

        var page = await host.GetAsync("/Admin/Operations?tab=failed");
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var cookie = string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));

        var retry = await host.PostAsync("/Admin/Operations?handler=Retry", cookie, new()
        {
            ["id"] = failed.ToString("D"),
            ["returnUrl"] = "/Admin/Operations?tab=failed",
            ["__RequestVerificationToken"] = token
        });
        Assert.AreEqual(HttpStatusCode.Redirect, retry.StatusCode);
        Assert.AreEqual("/Admin/Operations?tab=failed", retry.Headers.Location?.OriginalString);
        CollectionAssert.AreEqual(new[] { failed }, host.Actions.Retried);

        var cancel = await host.PostAsync("/Admin/Operations?handler=Cancel", cookie, new()
        {
            ["id"] = running.ToString("D"),
            ["returnUrl"] = "https://example.invalid/elsewhere",
            ["__RequestVerificationToken"] = token
        });
        Assert.AreEqual(HttpStatusCode.Redirect, cancel.StatusCode);
        Assert.AreEqual("/Admin/Operations", cancel.Headers.Location?.OriginalString, "A return address off this page is ignored.");
        CollectionAssert.AreEqual(new[] { running }, host.Actions.Cancelled);
    }

    [TestMethod]
    public async Task EmptyStatesTellNoJobsFromAnEmptyTabAndAPagePastTheEndShowsTheLastPage()
    {
        await using var host = await ActivityHost.CreateAsync();

        StringAssert.Contains(await host.GetHtmlAsync("/Admin/Operations"), "No jobs have run yet.");

        await host.CreateAsync("Old scan", OperationStatus.Succeeded);
        StringAssert.Contains(await host.GetHtmlAsync("/Admin/Operations"), "Nothing here right now.");

        for (var index = 0; index < AdminActivityQuery.PageSize + 1; index++)
        {
            await host.CreateAsync($"Queued {index:00}", OperationStatus.Queued);
        }

        var last = await host.GetHtmlAsync("/Admin/Operations?p=99");
        StringAssert.Contains(last, "21–21 of 21");
        StringAssert.Contains(last, "aria-current=\"page\"");
        StringAssert.Contains(last, "rel=\"prev\"");
        Assert.IsFalse(last.Contains("rel=\"next\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TheOperationPageLeadsBackToTheActivityItWasOpenedFrom()
    {
        await using var host = await ActivityHost.CreateAsync();
        var id = await host.CreateAsync("Nightly scan", OperationStatus.Failed);

        var html = await host.GetHtmlAsync($"/Admin/Operation/{id}?returnUrl=%2FAdmin%2FOperations%3Ftab%3Dfailed");
        StringAssert.Contains(html, "href=\"/Admin/Operations?tab=failed\"");
        StringAssert.Contains(html, "Back to activity");

        var foreign = await host.GetHtmlAsync($"/Admin/Operation/{id}?returnUrl=https%3A%2F%2Fexample.invalid%2F");
        Assert.IsFalse(foreign.Contains("example.invalid", StringComparison.Ordinal));
    }

    private sealed class FakeOperationActions : IOperationActions
    {
        public HashSet<Guid> Cancellable { get; } = [];

        public HashSet<Guid> Retryable { get; } = [];

        public List<Guid> Cancelled { get; } = [];

        public List<Guid> Retried { get; } = [];

        public bool CanCancel(OperationSnapshot operation) => Cancellable.Contains(operation.Id);

        public bool CanRetry(OperationSnapshot operation) => Retryable.Contains(operation.Id);

        public HashSet<Guid> Reprioritizable { get; } = [];

        public bool CanChangePriority(OperationSnapshot operation) => Reprioritizable.Contains(operation.Id);

        public bool HasRuntime(OperationSnapshot operation) => Retryable.Contains(operation.Id);

        public Task<OperationActionOutcome> CancelAsync(OperationSnapshot operation, CancellationToken cancellationToken)
        {
            Cancelled.Add(operation.Id);
            return Task.FromResult(new OperationActionOutcome(true));
        }

        public Task<OperationActionOutcome> RetryAsync(OperationSnapshot operation, CancellationToken cancellationToken)
        {
            Retried.Add(operation.Id);
            return Task.FromResult(new OperationActionOutcome(true));
        }
    }

    private sealed class ActivityHost : IAsyncDisposable
    {
        private readonly IHost host;
        private readonly TestServer server;

        private ActivityHost(AppDbContext db, FakeOperationActions actions, IHost host, TestServer server)
        {
            Db = db;
            Actions = actions;
            this.host = host;
            this.server = server;
        }

        public AppDbContext Db { get; }

        public FakeOperationActions Actions { get; }

        public static async Task<ActivityHost> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"jularr-activity-page-{Guid.NewGuid():N}.db");
            var connectionString = $"Data Source={path};Foreign Keys=True";
            var actions = new FakeOperationActions();

            var host = await new HostBuilder()
                .ConfigureWebHost(webBuilder => webBuilder
                    .UseTestServer()
                    .UseContentRoot(FindWebProjectRoot())
                    .ConfigureServices(services =>
                    {
                        services
                            .AddRazorPages()
                            .AddApplicationPart(typeof(Jularr.Web.Pages.Admin.OperationsModel).Assembly);
                        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
                        services.AddHttpContextAccessor();
                        services.AddAuthorization(options => JularrPolicies.Register(options));
                        services.AddAuthentication("test").AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, NoAnswerHandler>("test", _ => { });
                        services.AddLogging();
                        services.AddSingleton<IOperationActions>(actions);
                        services.AddScoped<Jularr.Web.Features.Acquisition.Access.VideoRequestWorkResolver>();
                        services.AddScoped<Jularr.Web.Features.Acquisition.Sabnzbd.SabnzbdAcquisitionStore>();
                        services.AddSingleton<ViteAssetManifest>();
                        services.AddScoped<CurrentAccountContext>();
                        services.AddSingleton(new MediaCapabilityStore(Path.Combine(Path.GetTempPath(), $"jularr-activity-caps-{Guid.NewGuid():N}")));
                        services.AddScoped<IMediaCapabilityService, MediaCapabilityService>();
                        services.AddScoped<IAppShellService, AppShellService>();
                    })
                    .Configure(app =>
                    {
                        app.Use(async (context, next) =>
                        {
                            var claims = new List<Claim>
                            {
                                new(ClaimTypes.NameIdentifier, "test-owner"),
                                new(ClaimTypes.Role, AccountRoles.Owner)
                            };
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
            return new ActivityHost(db, actions, host, host.GetTestServer());
        }

        public async Task<Guid> CreateAsync(
            string title,
            OperationStatus status,
            string kind = "library-scan",
            string category = "Library",
            int? progress = null,
            string? message = null,
            OperationPriority priority = OperationPriority.Normal)
        {
            var store = new OperationStore(Db);
            var id = await store.CreateAsync(new OperationDescriptor(kind, category, title, Priority: priority));
            switch (status)
            {
                case OperationStatus.Running:
                    await store.MarkRunningAsync(id);
                    break;
                case OperationStatus.Succeeded:
                    await store.MarkSucceededAsync(id);
                    break;
                case OperationStatus.Failed:
                    await store.MarkFailedAsync(id, "Something broke.");
                    break;
                case OperationStatus.Interrupted:
                    await store.MarkInterruptedAsync(id);
                    break;
            }

            if (progress is not null || message is not null)
            {
                await store.ReportProgressAsync(id, progress, message);
            }

            return id;
        }

        public async Task<HttpResponseMessage> GetAsync(string path)
        {
            using var client = server.CreateClient();
            return await client.GetAsync(path);
        }

        public async Task<HttpResponseMessage> PostAsync(string path, string cookie, Dictionary<string, string> form)
        {
            using var client = server.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
            request.Headers.Add("Cookie", cookie);
            return await client.SendAsync(request);
        }

        public async Task<string> GetHtmlAsync(string path)
        {
            using var response = await GetAsync(path);
            var html = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"GET {path} failed:\n{html}");

            // Razor encodes non-ASCII text as character references; assertions read the text a browser shows.
            return WebUtility.HtmlDecode(html);
        }

        public async ValueTask DisposeAsync()
        {
            server.Dispose();
            await host.StopAsync();
            host.Dispose();
            await Db.DisposeAsync();
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

    private sealed class NoAnswerHandler(
        IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
    }
}
