using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.History;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Shell;
using Jularr.Web.Features.Subtitles;
using Jularr.Web.Features.Tracking;
using Jularr.Web.Frontend;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jularr.Tests;

/// <summary>Admin → Media detail rendered end to end through a real HTTP request.</summary>
[TestClass]
public sealed class AdminMediaDetailPageRenderTests
{
    [TestMethod]
    public async Task TheHeaderSeasonsEpisodesAndFilesShowTheStoredStateWithoutProbingAnything()
    {
        await using var host = await MediaHost.CreateAsync();
        var animeId = await host.SeedAsync();

        var html = await host.GetHtmlAsync($"/Admin/Media/{animeId}?open=s1&ep=1x2");

        StringAssert.Contains(html, "<h1 id=\"admmd-title\">Starfall Chronicle</h1>");
        StringAssert.Contains(html, "TV · 2017 · Finished");
        StringAssert.Contains(html, "4 / 6");
        StringAssert.Contains(html, "4.6 GB");
        StringAssert.Contains(html, "Anime 1080p");
        StringAssert.Contains(html, $"href=\"/Library/Anime/{animeId}\"");
        StringAssert.Contains(html, "href=\"https://anilist.co/anime/9001\"");
        StringAssert.Contains(html, "aria-pressed=\"true\"");

        Assert.AreEqual(2, Regex.Matches(html, "class=\"admmd-group\"").Count, "Season 1 and the specials.");
        Assert.IsTrue(html.IndexOf("Season 1", StringComparison.Ordinal) < html.IndexOf("Specials", StringComparison.Ordinal));
        StringAssert.Contains(html, "3/5 available");
        StringAssert.Contains(html, "2 missing");
        StringAssert.Contains(html, "admmd-monitoring-partial");
        StringAssert.Contains(html, "aria-checked=\"mixed\"");

        // Episode states merge the file, the last attempt and the monitoring.
        StringAssert.Contains(html, "admmd-state-available");
        StringAssert.Contains(html, "admmd-state-downloading");
        StringAssert.Contains(html, "admmd-state-failed");
        StringAssert.Contains(html, "Failed attempts: 2");
        StringAssert.Contains(html, "Upgrade wanted");

        // The episode named in the address is open and lists both real files, never one merged file.
        Assert.IsTrue(Regex.IsMatch(html, "id=\"e-1x2\"\\s+open"), "The episode of the address starts open.");
        StringAssert.Contains(html, "Starfall.S01E02.1080p.WEB-DL.x264.mkv");
        StringAssert.Contains(html, "Starfall.S01E02.2160p.WEBRip.x265.mkv");
        StringAssert.Contains(html, "2160p HDR10");
        StringAssert.Contains(html, "H.264");
        StringAssert.Contains(html, "HEVC");
        StringAssert.Contains(html, "JA AAC 2.0");
        StringAssert.Contains(html, "EN ASS");
        StringAssert.Contains(html, "Media/Starfall");
        StringAssert.Contains(html, "Analysis failed: ffprobe rejected the file");
        StringAssert.Contains(html, "Subtitle files");
        StringAssert.Contains(html, "DE SRT");
        Assert.AreEqual(5, Regex.Matches(html, @">\s*Re-analyse\s*</button>").Count, "Every local file can be analysed again.");

        // Medium-level actions reuse the existing handlers and come back to this page.
        StringAssert.Contains(html, "handler=AnimeSettings");
        StringAssert.Contains(html, "handler=SearchAnime");
        StringAssert.Contains(html, "handler=Rescan");
        StringAssert.Contains(html, "handler=Reanalyze");
        StringAssert.Contains(html, "handler=Optimize");
        StringAssert.Contains(html, $"name=\"returnUrl\" value=\"/Admin/Media/{animeId}?open=s1&ep=1x2\"");
        StringAssert.Contains(html, "search=starfall");
        StringAssert.Contains(html, "mode=Season");
        StringAssert.Contains(html, "href=\"/Library/Rename/");
        Assert.IsTrue(
            Regex.IsMatch(html, "<a class=\"admin-nav-item[^\"]*\"[^>]*href=\"/Admin/Wanted\""),
            "The admin navigation is part of the page.");

        // Panels: acquisition, mapping, activity.
        StringAssert.Contains(html, "Jularr-managed");
        StringAssert.Contains(html, "3 wanted");
        StringAssert.Contains(html, "Needs a decision");
        StringAssert.Contains(html, "Episode could not be matched.");
        StringAssert.Contains(html, "S01 E04–E05 → Starfall Chronicle Part Two");
        StringAssert.Contains(html, "AniList E01–E02");
        StringAssert.Contains(html, "Grabbed");
        StringAssert.Contains(html, "S01 E04 · Starfall S01E04 1080p");
        StringAssert.Contains(html, "Optimize media for Direct Play");
        Assert.IsFalse(html.Contains("Some other anime job", StringComparison.Ordinal), "Only the operations of this anime are listed.");
    }

    [TestMethod]
    public async Task TheAniListGroupingOnlyRegroupsTheSameEpisodes()
    {
        await using var host = await MediaHost.CreateAsync();
        var animeId = await host.SeedAsync();

        var standard = await host.GetHtmlAsync($"/Admin/Media/{animeId}");
        var aniList = await host.GetHtmlAsync($"/Admin/Media/{animeId}?view=anilist");

        StringAssert.Contains(aniList, "Starfall Chronicle Part Two");
        StringAssert.Contains(aniList, "id=\"g-a-anilist-9002\"");
        StringAssert.Contains(aniList, "id=\"g-main\"");
        Assert.IsFalse(aniList.Contains("handler=SeasonMonitor", StringComparison.Ordinal), "AniList groups have no season switch: they never change monitoring on their own.");
        Assert.AreEqual(
            Regex.Matches(standard, "class=\"admmd-ep\"").Count,
            Regex.Matches(aniList, "class=\"admmd-ep\"").Count,
            "Switching the grouping never adds or removes an episode.");
        Assert.AreEqual(
            Regex.Matches(standard, "Starfall\\.S01E0").Count,
            Regex.Matches(aniList, "Starfall\\.S01E0").Count,
            "Every local file stays where it is.");
        StringAssert.Contains(aniList, "aria-current=\"true\">AniList</a>");
    }

    [TestMethod]
    public async Task SeasonAndEpisodeSwitchesWriteOnlyOverridesThatDifferFromWhatIsInherited()
    {
        await using var host = await MediaHost.CreateAsync();
        var animeId = await host.SeedAsync();

        var season = await host.PostAsync(
            $"/Admin/Media/{animeId}",
            "SeasonMonitor",
            new Dictionary<string, string> { ["season"] = "1", ["monitored"] = "true" });
        Assert.AreEqual(HttpStatusCode.Redirect, season.StatusCode);
        Assert.AreEqual($"/Admin/Media/{animeId}?open=s1", season.Headers.Location?.OriginalString);

        var state = await host.Monitoring.LoadAsync();
        var settings = state.Anime["starfall"];
        Assert.IsFalse(settings.SeasonOverrides.ContainsKey(1), "Season 1 now matches the medium, so nothing is stored.");
        Assert.AreEqual(0, settings.EpisodeOverrides.Count, "The single-episode override inside the season is gone.");
        Assert.IsTrue(settings.Monitored);

        var episode = await host.PostAsync(
            $"/Admin/Media/{animeId}",
            "EpisodeMonitor",
            new Dictionary<string, string> { ["season"] = "1", ["episode"] = "3", ["monitored"] = "false", ["open"] = "s1" });
        Assert.AreEqual(HttpStatusCode.Redirect, episode.StatusCode);
        Assert.AreEqual($"/Admin/Media/{animeId}?open=s1&ep=1x3", episode.Headers.Location?.OriginalString);
        Assert.IsFalse((await host.Monitoring.LoadAsync()).Anime["starfall"].EpisodeOverrides["S01E03"]);

        var html = await host.GetHtmlAsync($"/Admin/Media/{animeId}?open=s1");
        StringAssert.Contains(html, "admmd-monitoring-partial");

        var unknown = await host.PostAsync(
            $"/Admin/Media/{Guid.NewGuid()}",
            "SeasonMonitor",
            new Dictionary<string, string> { ["season"] = "1", ["monitored"] = "true" },
            tokenFrom: $"/Admin/Media/{animeId}");
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [TestMethod]
    public async Task ReanalysingAFileOfAnotherAnimeIsRefusedWithoutTouchingIt()
    {
        await using var host = await MediaHost.CreateAsync();
        var animeId = await host.SeedAsync();

        var response = await host.PostAsync(
            $"/Admin/Media/{animeId}",
            "ReanalyzeFile",
            new Dictionary<string, string> { ["fileId"] = Guid.NewGuid().ToString() });
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);

        var html = await host.GetHtmlAsync($"/Admin/Media/{animeId}", cookies: response);
        StringAssert.Contains(html, "That file no longer exists.");
        StringAssert.Contains(html, "role=\"alert\"");
    }

    [TestMethod]
    public async Task AnUnmonitoredMediumDisablesTheSeasonAndEpisodeSwitchesAndSearching()
    {
        await using var host = await MediaHost.CreateAsync();
        var animeId = await host.SeedAsync(monitored: false);

        var html = await host.GetHtmlAsync($"/Admin/Media/{animeId}");

        StringAssert.Contains(html, "Monitor the title first.");
        Assert.AreEqual(0, Regex.Matches(html, "role=\"switch\"(?![^>]*disabled)").Count, "No season or episode switch is live.");
        Assert.IsTrue(
            Regex.IsMatch(html, @"<button class=""button button-primary"" type=""submit"" disabled"),
            "Searching wanted episodes needs a monitored title.");
        StringAssert.Contains(html, "aria-pressed=\"false\"");
        StringAssert.Contains(html, "Not monitored");
    }

    [TestMethod]
    public async Task ATitleWithoutEpisodesShowsTheEmptyStateAndStillOffersTheFolderActions()
    {
        await using var host = await MediaHost.CreateAsync();
        var animeId = await host.SeedEmptyAsync();

        var html = await host.GetHtmlAsync($"/Admin/Media/{animeId}");

        StringAssert.Contains(html, "No episodes yet.");
        StringAssert.Contains(html, "data-admin-media-empty");
        Assert.IsFalse(html.Contains("data-admin-media-groups", StringComparison.Ordinal));
        StringAssert.Contains(html, "handler=Rescan");
        StringAssert.Contains(html, "No provider match.");
        StringAssert.Contains(html, "Nothing recorded yet.");
        Assert.IsTrue(
            Regex.IsMatch(html, @"<button class=""button"" type=""submit"" disabled[^>]*>\s*Re-analyse files"),
            "Without local files there is nothing to analyse or optimize.");
    }

    [TestMethod]
    public async Task UnreadableMappingAndActivityDegradeTheirPanelsButNotThePage()
    {
        await using var host = await MediaHost.CreateAsync();
        var animeId = await host.SeedAsync();
        Directory.CreateDirectory(Path.Combine(host.Root, "integrations"));
        await File.WriteAllTextAsync(Path.Combine(host.Root, "integrations", "anilist-episode-mappings.json"), "{ not json");
        await File.WriteAllTextAsync(Path.Combine(host.Root, "acquisition", "imports.json"), "{ not json");

        var html = await host.GetHtmlAsync($"/Admin/Media/{animeId}");

        StringAssert.Contains(html, "Episode ranges could not be read.");
        StringAssert.Contains(html, "The activity could not be read.");
        StringAssert.Contains(html, "Starfall.S01E02.1080p.WEB-DL.x264.mkv");
        StringAssert.Contains(html, "4 / 6");
    }

    [TestMethod]
    public async Task UnreadableMonitoringStateShowsAnErrorWithRetryInsteadOfFakeEmptiness()
    {
        await using var host = await MediaHost.CreateAsync();
        var animeId = await host.SeedAsync();
        Directory.CreateDirectory(Path.Combine(host.Root, "data", "acquisition"));
        await File.WriteAllTextAsync(Path.Combine(host.Root, "data", "acquisition", "monitoring.json"), "{ not json");

        var html = await host.GetHtmlAsync($"/Admin/Media/{animeId}");

        StringAssert.Contains(html, "role=\"alert\"");
        StringAssert.Contains(html, "The media details could not be loaded.");
        StringAssert.Contains(html, $"href=\"/Admin/Media/{animeId}\"");
        Assert.IsFalse(html.Contains("data-admin-media-groups", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OnlyMediaManagersMayOpenTheMediaDetailAndAnUnknownTitleIsNotFound()
    {
        await using var host = await MediaHost.CreateAsync();
        var animeId = await host.SeedAsync();

        Assert.AreEqual(HttpStatusCode.OK, await host.GetStatusAsync($"/Admin/Media/{animeId}", asOwner: true));
        Assert.AreEqual(HttpStatusCode.Forbidden, await host.GetStatusAsync($"/Admin/Media/{animeId}", asOwner: false));
        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync($"/Admin/Media/{Guid.NewGuid()}", asOwner: true));
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

    private sealed class MediaHost : IAsyncDisposable
    {
        private const string Profile = "test-profile";
        private const string OwnerHeader = "X-Test-Owner";

        private readonly IHost host;
        private readonly TestServer server;

        private MediaHost(string root, AppDbContext db, AnimeMonitoringStore monitoring, AnimeImportStore imports, AniListAccountStore aniList, AcquisitionOwnershipStore ownership, IHost host)
        {
            Root = root;
            Db = db;
            Monitoring = monitoring;
            Imports = imports;
            AniList = aniList;
            Ownership = ownership;
            this.host = host;
            server = host.GetTestServer();
        }

        public string Root { get; }

        public AppDbContext Db { get; }

        public AnimeMonitoringStore Monitoring { get; }

        public AnimeImportStore Imports { get; }

        public AniListAccountStore AniList { get; }

        public AcquisitionOwnershipStore Ownership { get; }

        public static async Task<MediaHost> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"jularr-media-detail-{Guid.NewGuid():N}");
            var data = Directory.CreateDirectory(Path.Combine(root, "data"));
            var connectionString = $"Data Source={Path.Combine(root, "jularr.db")};Foreign Keys=True";
            var monitoring = new AnimeMonitoringStore(data.FullName);
            var imports = new AnimeImportStore(new DirectoryInfo(Path.Combine(root, "acquisition")));
            var aniList = new AniListAccountStore(
                new EphemeralDataProtectionProvider(),
                NullLogger<AniListAccountStore>.Instance,
                new DirectoryInfo(Path.Combine(root, "integrations")));
            var ownership = new AcquisitionOwnershipStore(data.FullName);
            var capabilities = new MediaCapabilityStore(data.FullName);

            var host = await new HostBuilder()
                .ConfigureWebHost(webBuilder => webBuilder
                    .UseTestServer()
                    .UseContentRoot(FindWebProjectRoot())
                    .ConfigureServices(services =>
                    {
                        services
                            .AddRazorPages()
                            .AddApplicationPart(typeof(Jularr.Web.Pages.Admin.MediaDetailModel).Assembly);
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
                        services.AddSingleton(imports);
                        services.AddSingleton(aniList);
                        services.AddSingleton(ownership);
                        services.AddScoped<AcquisitionAccessStore>();
                        services.AddScoped<AcquisitionHistoryService>();
                        services.AddScoped<AdminMediaDetailService>();
                        services.AddSingleton<IJularrEventPublisher, RecordingEventPublisher>();
                        services.AddSingleton<IMediaProbeRunner, FakeMediaProbeRunner>();
                        services.AddSingleton<MediaInventoryService>();
                        services.AddScoped<OperationRunner>();
                        services.AddScoped<MediaFileReanalysisService>();
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
            return new MediaHost(root, db, monitoring, imports, aniList, ownership, host);
        }

        /// <summary>
        /// Starfall Chronicle: season 1 with five episodes (three with files, one downloading, one failed and unmonitored)
        /// and one special with a file; 4.6 GB in total. Returns the anime id.
        /// </summary>
        public async Task<Guid> SeedAsync(bool monitored = true)
        {
            var anime = new Anime { Key = "starfall", Title = "Starfall Chronicle" };
            Db.Anime.Add(anime);
            Db.AnimeMetadata.Add(new AnimeMetadata
            {
                AnimeId = anime.Id,
                Provider = "anilist",
                ExternalId = "9001",
                PreferredTitle = "Starfall Chronicle",
                Format = "TV",
                Status = "FINISHED",
                SeasonYear = 2017
            });
            var root = new LibraryRoot { Name = "Media", Path = "/media" };
            Db.Add(root);

            Episode Add(int season, int number)
            {
                var episode = new Episode { AnimeId = anime.Id, SeasonNumber = season, Number = number, Title = season == 0 ? "Recap" : $"Chapter {number}" };
                Db.Episodes.Add(episode);
                return episode;
            }

            var e1 = Add(1, 1);
            var e2 = Add(1, 2);
            var e3 = Add(1, 3);
            Add(1, 4);
            Add(1, 5);
            var special = Add(0, 1);

            MediaFile File(Episode episode, string name, long size, MediaAnalysisStatus status, int? width, int? height, string? codec, string? range, string? diagnostic = null)
            {
                var file = new MediaFile
                {
                    LibraryRootId = root.Id,
                    EpisodeId = episode.Id,
                    Path = $"/media/Starfall/Season {(episode.SeasonNumber == 0 ? "Specials" : "1")}/{name}",
                    SizeBytes = size
                };
                Db.MediaFiles.Add(file);
                Db.MediaAnalyses.Add(new MediaAnalysis
                {
                    MediaFileId = file.Id,
                    Status = status,
                    Diagnostic = diagnostic,
                    Container = status == MediaAnalysisStatus.Succeeded ? "matroska" : null,
                    VideoCodec = codec,
                    Width = width,
                    Height = height,
                    DynamicRange = range,
                    DurationSeconds = 1440,
                    SourceLastWriteTimeUtc = DateTime.UtcNow
                });
                return file;
            }

            var f1 = File(e1, "Starfall.S01E01.1080p.WEB-DL.x264.mkv", 1_000_000_000, MediaAnalysisStatus.Succeeded, 1920, 1080, "h264", null);
            var f2a = File(e2, "Starfall.S01E02.1080p.WEB-DL.x264.mkv", 1_200_000_000, MediaAnalysisStatus.Succeeded, 1920, 1080, "h264", null);
            File(e2, "Starfall.S01E02.2160p.WEBRip.x265.mkv", 800_000_000, MediaAnalysisStatus.Failed, null, null, null, null, "ffprobe rejected the file");
            File(e3, "Starfall.S01E03.2160p.WEB-DL.x265.mkv", 1_500_000_000, MediaAnalysisStatus.Succeeded, 3840, 2160, "hevc", "HDR10");
            File(special, "Starfall.S00E01.720p.mkv", 100_000_000, MediaAnalysisStatus.Succeeded, 1280, 720, "h264", null);
            Db.MediaAnalysisStreams.Add(new MediaAnalysisStream { MediaFileId = f1.Id, StreamIndex = 1, Kind = MediaStreamKind.Audio, Language = "jpn", Codec = "aac", Channels = 2 });
            Db.MediaAnalysisStreams.Add(new MediaAnalysisStream { MediaFileId = f1.Id, StreamIndex = 2, Kind = MediaStreamKind.Subtitle, Language = "eng", Codec = "ass" });
            Db.MediaAnalysisStreams.Add(new MediaAnalysisStream { MediaFileId = f2a.Id, StreamIndex = 1, Kind = MediaStreamKind.Audio, Language = "jpn", Codec = "aac", Channels = 2 });
            Db.SubtitleTracks.Add(new SubtitleTrack { EpisodeId = e2.Id, Path = "/media/Starfall/Season 1/e2.de.srt", Language = "de", Format = "srt" });
            Db.AcquisitionHistory.Add(new AcquisitionHistoryEntry
            {
                AnimeId = anime.Id,
                SeasonNumber = 1,
                EpisodeNumber = 4,
                EventKind = AcquisitionHistoryEventKind.Grabbed,
                ReleaseTitle = "Starfall S01E04 1080p",
                QualityKey = "WEBDL-1080p",
                Reason = "Best score",
                OccurredAtUtc = DateTime.UtcNow.AddHours(-2)
            });
            await Db.SaveChangesAsync();

            var store = new OperationStore(Db);
            await store.CreateAsync(new OperationDescriptor("media-optimize", "Library", "Optimize media for Direct Play", "Starfall Chronicle"));
            await store.CreateAsync(new OperationDescriptor("media-optimize", "Library", "Some other anime job", "Another Anime"));

            var now = DateTimeOffset.UtcNow;
            await Ownership.UpdateAsync(state =>
            {
                state.Anime["starfall"] = new AnimeManagementAssignment("starfall", AnimeManagementMode.JularrManaged, now);
                return state with { };
            });
            await Monitoring.UpdateAsync(state =>
            {
                var settings = new AnimeMonitorSettings(
                    "starfall",
                    monitored,
                    true,
                    [],
                    new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["S01E05"] = false });
                state.Anime["starfall"] = settings;
                var upgrade = new AnimeEpisodeKey("starfall", 1, 1);
                var downloading = new AnimeEpisodeKey("starfall", 1, 4);
                var failed = new AnimeEpisodeKey("starfall", 1, 5);
                state.Wanted[upgrade.ToString()] = new WantedUnit(upgrade, WantedReason.CutoffUnmet, now.AddDays(-1));
                state.Wanted[downloading.ToString()] = new WantedUnit(downloading, WantedReason.Missing, now.AddDays(-2));
                state.Wanted[failed.ToString()] = new WantedUnit(failed, WantedReason.Missing, now.AddDays(-3));
                state.Attempts[downloading.ToString()] = new AcquisitionAttempt(downloading, AcquisitionAttemptStatus.Grabbed, "release", 0, now.AddHours(-2), null);
                state.Attempts[failed.ToString()] = new AcquisitionAttempt(failed, AcquisitionAttemptStatus.Failed, "release", 2, now.AddHours(-5), now.AddHours(3));
                return state;
            });

            await AniList.TryAddEpisodeMappingAsync(
                new AnimeEpisodeMetadataMapping(Guid.NewGuid(), anime.Id, 1, 4, 5, 1, "anilist", "9002", "Starfall Chronicle Part Two", 2, now),
                CancellationToken.None);
            await Imports.UpsertAsync(new AnimeImportRecord(
                Guid.NewGuid(),
                Guid.NewGuid(),
                null,
                null,
                "starfall",
                "Starfall Chronicle",
                null,
                AnimeImportStatus.ManualRequired,
                [],
                "Episode could not be matched.",
                now,
                now));
            return anime.Id;
        }

        /// <summary>A title the library knows but that has no episodes, files or provider match yet.</summary>
        public async Task<Guid> SeedEmptyAsync()
        {
            var anime = new Anime { Key = "bare", Title = "Bare Title" };
            Db.Anime.Add(anime);
            await Db.SaveChangesAsync();
            return anime.Id;
        }

        public async Task<string> GetHtmlAsync(string path, HttpResponseMessage? cookies = null)
        {
            using var client = Client(asOwner: true);
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (cookies is not null)
            {
                request.Headers.Add("Cookie", CookieHeader(cookies));
            }

            using var response = await client.SendAsync(request);
            var html = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"GET {path} failed:\n{html}");

            // Razor encodes non-ASCII text as character references; assertions read the text a browser shows.
            return WebUtility.HtmlDecode(html);
        }

        /// <summary>Posts a form of the page the way a browser does: with the antiforgery cookie and token of a fresh visit.</summary>
        public async Task<HttpResponseMessage> PostAsync(
            string path,
            string handler,
            Dictionary<string, string> fields,
            string? tokenFrom = null)
        {
            using var client = Client(asOwner: true);
            using var visit = await client.GetAsync(tokenFrom ?? path);
            var page = await visit.Content.ReadAsStringAsync();
            var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            Assert.IsFalse(string.IsNullOrEmpty(token), "The page carries an antiforgery token.");

            var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{path}?handler={handler}")
            {
                Content = new FormUrlEncodedContent(form)
            };
            request.Headers.Add("Cookie", CookieHeader(visit));
            return await client.SendAsync(request);
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
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string CookieHeader(HttpResponseMessage response) =>
            response.Headers.TryGetValues("Set-Cookie", out var values)
                ? string.Join("; ", values.Select(value => value.Split(';')[0]))
                : "";

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
