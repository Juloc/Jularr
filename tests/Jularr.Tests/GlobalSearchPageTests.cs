using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Search;
using Jularr.Web.Features.Shell;
using Jularr.Web.Features.Watchlist;
using Jularr.Web.Frontend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jularr.Tests;

/// <summary>The <c>/Search</c> page (#434) rendered end to end: results, filters, visibility and paging.</summary>
[TestClass]
public sealed class GlobalSearchPageTests
{
    [TestMethod]
    public async Task ResultsShowTypesFactsBadgesFranchiseAndTheProviderHandOff()
    {
        await using var fixture = await GlobalSearchFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("Sousou no Frieren", 2023, withFile: true, streams: [("jpn", MediaStreamKind.Audio)]);
        await fixture.MonitorAnimeAsync(anime, wanted: true);
        var manga = await fixture.AddMangaAsync("Sousou no Frieren");
        var movie = await fixture.AddMovieAsync("Sousou no Frieren Movie", 2026);
        var franchise = await fixture.AddFranchiseAsync(
            "Sousou no Frieren",
            (WatchlistMediaType.Anime, "154587", "Sousou no Frieren"),
            (WatchlistMediaType.Manga, "118586", "Sousou no Frieren Manga"));
        await using var host = await SearchPageHost.CreateAsync(fixture);

        var html = await host.GetHtmlAsync("/Search?q=Sousou%20no%20Frieren", asOwner: true);

        StringAssert.Contains(html, "<h1>Search</h1>");
        StringAssert.Contains(html, "value=\"Sousou no Frieren\"");
        Assert.IsTrue(Regex.IsMatch(html, @"Results: \d+"), "The result count is shown.");
        StringAssert.Contains(html, $"href=\"/Franchises/{franchise:D}\"");
        StringAssert.Contains(html, "Franchise");
        StringAssert.Contains(html, "Works: 2");
        StringAssert.Contains(html, $"href=\"/Library/Anime/{anime.Id:D}\"");
        StringAssert.Contains(html, $"href=\"/Manga/Series/{manga:D}\"");
        StringAssert.Contains(html, "search-badge-available");
        StringAssert.Contains(html, "search-badge-wanted");
        StringAssert.Contains(html, "search-badge-monitored");
        StringAssert.Contains(html, "search-badge-library");
        StringAssert.Contains(html, "<span>2023</span>");
        StringAssert.Contains(html, "href=\"/?q=Sousou%20no%20Frieren\"");
        StringAssert.Contains(html, "Search online providers");

        // A movie has no page yet: its title is listed, but never linked to a route that does not exist.
        Assert.IsFalse(html.Contains($"{movie.Id:D}", StringComparison.Ordinal), "No link is built for a type without a page.");
        Assert.IsTrue(Regex.IsMatch(html, @"<span class=""search-result-title"">Sousou no Frieren Movie</span>"));
    }

    [TestMethod]
    public async Task FiltersReachTheServiceEchoInTheFormAndOfferOnlyReachableValues()
    {
        await using var fixture = await GlobalSearchFixture.CreateAsync();
        await fixture.AddAnimeAsync("Star Local", 2020, withFile: true, streams: [("jpn", MediaStreamKind.Audio), ("eng", MediaStreamKind.Subtitle)]);
        await fixture.AddAnimeAsync("Star Remote", 2015);
        await fixture.AddNovelAsync("Star Novel", genres: ["Fantasy"], withContent: true, format: "EPUB:de");
        await using var host = await SearchPageHost.CreateAsync(fixture);

        var html = await host.GetHtmlAsync("/Search?q=Star&availability=available&type=anime&language=ja&yearFrom=2019", asOwner: true);

        StringAssert.Contains(html, "Star Local");
        Assert.IsFalse(html.Contains("Star Remote", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("Star Novel", StringComparison.Ordinal));
        StringAssert.Contains(html, "<details class=\"search-filters\" open");
        StringAssert.Contains(html, "<option value=\"available\" selected");
        StringAssert.Contains(html, "name=\"type\" value=\"anime\" checked");
        StringAssert.Contains(html, "<option value=\"ja\" selected");
        StringAssert.Contains(html, "value=\"2019\"");
        StringAssert.Contains(html, "Clear filters");
        StringAssert.Contains(html, "/Search?q=Star&type=anime");

        // Language and genre options are the values the query can reach (before the filters narrowed it).
        var open = await host.GetHtmlAsync("/Search?q=Star", asOwner: true);
        StringAssert.Contains(open, "<option value=\"de\">Deutsch</option>");
        StringAssert.Contains(open, "<option value=\"en\">English</option>");
        StringAssert.Contains(open, "<option value=\"Fantasy\">Fantasy</option>");
        Assert.IsFalse(open.Contains("Clear filters", StringComparison.Ordinal));
        Assert.IsFalse(open.Contains("<details class=\"search-filters\" open", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task HiddenMediaTypesAreNeitherSearchedNorOfferedAsAFilter()
    {
        await using var fixture = await GlobalSearchFixture.CreateAsync();
        await fixture.AddAnimeAsync("Hidden Type Show", 2020);
        await fixture.AddMangaAsync("Hidden Type Manga");
        await using var host = await SearchPageHost.CreateAsync(fixture);
        await host.SetUserCapabilityAsync(WorkMediaType.Anime, MediaCapability.Hidden);

        var user = await host.GetHtmlAsync("/Search?q=Hidden%20Type", asOwner: false);

        StringAssert.Contains(user, "Hidden Type Manga");
        Assert.IsFalse(user.Contains("Hidden Type Show", StringComparison.Ordinal));
        Assert.IsFalse(user.Contains("value=\"anime\"", StringComparison.Ordinal), "A hidden type is not offered as a filter chip.");
        StringAssert.Contains(user, "value=\"manga\"");

        // Asking for the hidden type by URL finds nothing rather than leaking it.
        var forced = await host.GetHtmlAsync("/Search?q=Hidden%20Type&type=anime", asOwner: false);
        Assert.IsFalse(forced.Contains("Hidden Type Show", StringComparison.Ordinal));
        Assert.IsFalse(forced.Contains("name=\"type\" value=\"anime\"", StringComparison.Ordinal));

        var owner = await host.GetHtmlAsync("/Search?q=Hidden%20Type", asOwner: true);
        StringAssert.Contains(owner, "Hidden Type Show");
    }

    [TestMethod]
    public async Task EmptyAndBlankQueriesAndPaging()
    {
        await using var fixture = await GlobalSearchFixture.CreateAsync();
        for (var index = 0; index < 25; index++)
        {
            await fixture.AddMovieAsync($"Paged Feature {index:D2}", 2000 + index);
        }

        await using var host = await SearchPageHost.CreateAsync(fixture);

        var blank = await host.GetHtmlAsync("/Search", asOwner: true);
        Assert.IsFalse(blank.Contains("No matches", StringComparison.Ordinal), "A blank search shows the form, not an empty result.");
        Assert.IsFalse(blank.Contains("search-results", StringComparison.Ordinal));

        var none = await host.GetHtmlAsync("/Search?q=qzxvkjw", asOwner: true);
        StringAssert.Contains(none, "No matches");
        StringAssert.Contains(none, "Search online providers");

        var first = await host.GetHtmlAsync("/Search?q=Paged%20Feature", asOwner: true);
        StringAssert.Contains(first, "Results: 25");
        Assert.AreEqual(20, Regex.Matches(first, "class=\"search-result\"").Count);
        StringAssert.Contains(first, "rel=\"next\" href=\"/Search?q=Paged%20Feature&p=2\"");
        Assert.IsFalse(first.Contains("rel=\"prev\"", StringComparison.Ordinal));

        var second = await host.GetHtmlAsync("/Search?q=Paged%20Feature&p=2", asOwner: true);
        Assert.AreEqual(5, Regex.Matches(second, "class=\"search-result\"").Count);
        StringAssert.Contains(second, "rel=\"prev\" href=\"/Search?q=Paged%20Feature\"");
        Assert.IsFalse(second.Contains("rel=\"next\"", StringComparison.Ordinal));
    }

    private sealed class SearchPageHost : IAsyncDisposable
    {
        private const string OwnerHeader = "X-Test-Owner";
        private const string Profile = "test-profile";

        private readonly IHost host;
        private readonly TestServer server;
        private readonly MediaCapabilityStore capabilities;

        private SearchPageHost(IHost host, TestServer server, MediaCapabilityStore capabilities)
        {
            this.host = host;
            this.server = server;
            this.capabilities = capabilities;
        }

        public static async Task<SearchPageHost> CreateAsync(GlobalSearchFixture fixture)
        {
            var connectionString = fixture.Db.Database.GetConnectionString()!;
            var data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"jularr-search-page-{Guid.NewGuid():N}"));
            var capabilities = new MediaCapabilityStore(data.FullName);

            var host = await new HostBuilder()
                .ConfigureWebHost(webBuilder => webBuilder
                    .UseTestServer()
                    .UseContentRoot(FindWebProjectRoot())
                    .ConfigureServices(services =>
                    {
                        services
                            .AddRazorPages()
                            .AddApplicationPart(typeof(Jularr.Web.Pages.Search.IndexModel).Assembly);
                        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
                        services.AddHttpContextAccessor();
                        services.AddAuthorization(options => JularrPolicies.Register(options));
                        services.AddLogging();
                        services.AddSingleton<ViteAssetManifest>();
                        services.AddScoped<CurrentAccountContext>();
                        services.AddSingleton(capabilities);
                        services.AddScoped<IMediaCapabilityService, MediaCapabilityService>();
                        services.AddScoped<IAppShellService, AppShellService>();
                        services.AddSingleton(fixture.Monitoring);
                        services.AddScoped<AcquisitionAccessStore>();
                        services.AddScoped<MediaSearchService>();
                    })
                    .Configure(app =>
                    {
                        // Stand-in for cookie sign-in: a header chooses the owner, otherwise a plain user.
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

            return new SearchPageHost(host, host.GetTestServer(), capabilities);
        }

        public Task SetUserCapabilityAsync(WorkMediaType type, MediaCapability capability) =>
            capabilities.SetRoleDefaultAsync(AccountRole.User, type, capability);

        public async Task<string> GetHtmlAsync(string path, bool asOwner)
        {
            using var client = server.CreateClient();
            if (asOwner)
            {
                client.DefaultRequestHeaders.Add(OwnerHeader, "true");
            }

            using var response = await client.GetAsync(path);
            var html = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"GET {path} (owner={asOwner}) failed:\n{html}");

            // Razor encodes non-ASCII text as character references and keeps the template's line breaks;
            // assertions read the text a browser shows, with runs of whitespace collapsed.
            return Regex.Replace(WebUtility.HtmlDecode(html), @"\s+", " ");
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
