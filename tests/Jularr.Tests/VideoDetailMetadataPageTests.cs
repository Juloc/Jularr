using System.Net;
using System.Text.RegularExpressions;
using Jularr.Web.Data;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// The persisted Work metadata and artwork on the Movie and Series pages (#820 UI slice, docs/mockups/movie-detail and anime-series-detail):
/// the backdrop hero and its facts, the click-to-load trailer, Cast &amp; Crew and About, the sections that disappear without data, provider
/// text that is only ever encoded, artwork that only comes through the Jularr endpoint, and the open-time metadata promotion.
/// </summary>
[TestClass]
public sealed class VideoDetailMetadataPageTests
{
    private const string Overview = "In a drought-stricken delta, a surveyor who maps vanishing coastlines is drawn into a conspiracy.";

    private sealed record MetadataSeed(
        string? Title = null,
        string? Overview = null,
        string? Tagline = null,
        string[]? Genres = null,
        string[]? Trailers = null,
        bool Backdrop = false,
        bool Poster = false,
        bool Facts = false,
        bool Credits = false,
        bool Refreshed = true,
        string[]? Studios = null,
        string? OriginalTitle = null);

    private static async Task<Work> AddTitleAsync(VideoDetailPageTestHost host, WorkMediaType type, string title, int year, string tmdbId = "603", bool withFile = true)
    {
        var seed = new LibraryCanonicalSeed(host.Db);
        var work = await seed.AddWorkAsync(type, title, year);
        host.Db.Add(new WorkExternalIdentity { WorkId = work.Id, MediaType = type, Provider = "tmdb", ExternalId = tmdbId, IsPrimary = true });
        await host.Db.SaveChangesAsync();
        if (withFile)
        {
            await seed.AddVideoAsync(work, type == WorkMediaType.Movie ? null : await seed.AddEpisodeAsync(work, 1, 1), audio: ["ger"], subtitles: ["eng"], durationSeconds: 6000, width: 1920, height: 1080);
        }

        return work;
    }

    private static async Task SeedMetadataAsync(VideoDetailPageTestHost host, Work work, MetadataSeed seed)
    {
        var store = new WorkMetadataStore(host.Db);
        var now = DateTime.UtcNow;
        async Task FieldAsync(WorkLocalizedField field, params string[]? values)
        {
            if (values is { Length: > 0 })
            {
                await store.ReplaceLocalizedFieldAsync(work.Id, "en", field, values, "tmdb", "1", 0, now, CancellationToken.None);
            }
        }

        await FieldAsync(WorkLocalizedField.Title, seed.Title is null ? null : [seed.Title]);
        await FieldAsync(WorkLocalizedField.Overview, seed.Overview is null ? null : [seed.Overview]);
        await FieldAsync(WorkLocalizedField.Tagline, seed.Tagline is null ? null : [seed.Tagline]);
        await FieldAsync(WorkLocalizedField.Genre, seed.Genres);
        await FieldAsync(WorkLocalizedField.Trailer, seed.Trailers);
        if (seed.Facts || seed.Studios is not null || seed.OriginalTitle is not null)
        {
            await store.UpsertFactsAsync(
                new WorkMetadataFacts
                {
                    WorkId = work.Id,
                    OriginalTitle = seed.OriginalTitle,
                    OriginalLanguage = "fr",
                    ReleaseDate = seed.Facts ? new DateOnly(2024, 3, 1) : null,
                    RuntimeMinutes = seed.Facts ? 166 : null,
                    Rating = seed.Facts ? 8.1 : null,
                    RatingCount = seed.Facts ? 621345 : null,
                    Certification = seed.Facts ? "PG-13" : null,
                    CertificationCountry = seed.Facts ? "US" : null,
                    Studios = seed.Studios ?? (seed.Facts ? ["Northlight Pictures", "Orbital Films"] : []),
                    ProductionCountries = seed.Facts ? ["US", "GB"] : [],
                    UpdatedAt = now
                },
                CancellationToken.None);
        }

        if (seed.Credits)
        {
            await store.ReplaceCreditsAsync(
                work.Id,
                [new(WorkCreditKind.Cast, "Mara Elling", "Ines Calder", null), new(WorkCreditKind.Cast, "Yuki Tanabe", null, null), new(WorkCreditKind.Crew, "Marta Lindqvist", "Director", null)],
                "tmdb",
                now,
                CancellationToken.None);
        }

        foreach (var (slot, wanted) in new[] { (WorkArtworkSlot.Poster, seed.Poster), (WorkArtworkSlot.Backdrop, seed.Backdrop) })
        {
            if (wanted)
            {
                var candidate = new WorkArtworkCandidate(slot, "", "/x.jpg", new Uri("https://image.tmdb.org/t/p/w780/x.jpg"), slot == WorkArtworkSlot.Poster ? 500 : 1280, slot == WorkArtworkSlot.Poster ? 750 : 720, 5, 1);
                await store.UpsertArtworkAsync(work.Id, candidate, "tmdb", WorkArtworkCache.CacheKey(work.Id, slot, "", "tmdb", "/x.jpg"), now, CancellationToken.None);
            }
        }

        if (seed.Refreshed)
        {
            await host.Db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "WorkMetadataRefreshes" ("WorkId", "Locale", "Priority", "Status", "Attempts", "NextAttemptAt", "LastAttemptAt", "LastSucceededAt", "CreatedAt", "UpdatedAt")
                VALUES ({work.Id}, 'en', 20, 1, 0, {now.AddDays(30)}, {now}, {now}, {now}, {now})
                """);
        }
    }

    private static MetadataSeed Full() => new(
        Title: "The Last Meridian",
        Overview: Overview,
        Tagline: "Every road ends somewhere.",
        Genres: ["Science Fiction", "Adventure", "Drama", "Mystery"],
        Trailers: ["BdJKm16Co6M"],
        Backdrop: true,
        Poster: true,
        Facts: true,
        Credits: true,
        OriginalTitle: "Le Dernier Méridien");

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(from >= 0, $"'{start}' not found.");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.IsTrue(to > from, $"'{end}' not found after '{start}'.");
        return text[from..to];
    }

    [TestMethod]
    public async Task AMovieWithPersistedMetadataShowsTheBackdropHeroItsFactsTrailerCastAndAbout()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "The Last Meridian (cached)", 2024);
        await SeedMetadataAsync(host, movie, Full());

        var html = await host.GetOkAsync($"/Library/Movie/{movie.Id}");

        var hero = Between(html, "<section class=\"ad-hero", "</section>");
        StringAssert.Contains(hero, "The Last Meridian");
        Assert.IsFalse(hero.Contains("(cached)", StringComparison.Ordinal), "The localized title wins over the cached canonical title.");
        StringAssert.Contains(hero, "Le Dernier Méridien");
        StringAssert.Matches(hero, new Regex(@"<section class=""ad-hero"" aria-labelledby=""ad-title"" data-work-hero>"), "A real backdrop is the hero itself, not the blurred fallback.");
        StringAssert.Matches(hero, new Regex($@"<img class=""ad-hero-art"" src=""/works/{movie.Id:D}/artwork/\d+\?v=[0-9a-f]{{12}}"" alt="""" width=""1280"" height=""720"""));
        Assert.AreEqual(1, Regex.Matches(hero, "<img ").Count, "No second poster inside the hero.");
        StringAssert.Contains(hero, "1h 40m", "The runtime of the local file, in the one runtime format.");
        foreach (var genre in new[] { "Science Fiction", "Adventure", "Drama" })
        {
            StringAssert.Contains(hero, $"<li>{genre}</li>");
        }

        Assert.IsFalse(hero.Contains("<li>Mystery</li>", StringComparison.Ordinal), "The hero names a few genres only.");
        StringAssert.Contains(hero, Overview);
        StringAssert.Contains(hero, "data-vd-expand");
        var strip = Between(hero, "<ul class=\"ad-hero-strip\">", "</ul>");
        StringAssert.Contains(strip, "8.1");
        StringAssert.Contains(strip, "(621.3K)");
        StringAssert.Contains(strip, "Northlight Pictures");
        Assert.IsFalse(strip.Contains("Orbital Films", StringComparison.Ordinal), "The strip names the first studio; About lists them all.");
        StringAssert.Contains(strip, "PG-13");

        var trailer = Between(html, "<section class=\"vd-trailer\"", "</section>");
        StringAssert.Contains(trailer, "data-vd-trailer data-key=\"BdJKm16Co6M\"");
        StringAssert.Contains(trailer, "href=\"https://www.youtube.com/watch?v=BdJKm16Co6M\"");
        StringAssert.Contains(trailer, "rel=\"noopener noreferrer\"");
        StringAssert.Contains(trailer, "Play trailer: The Last Meridian");
        StringAssert.Contains(trailer, "Trailer of The Last Meridian");

        var cast = Between(html, "<section class=\"vd-cast\"", "</section>");
        StringAssert.Contains(cast, "role=\"region\" aria-labelledby=\"vd-cast-title\" tabindex=\"0\"");
        StringAssert.Contains(cast, "Mara Elling");
        StringAssert.Contains(cast, "Ines Calder");
        StringAssert.Contains(cast, ">ME<");
        StringAssert.Contains(cast, "Marta Lindqvist");
        StringAssert.Contains(cast, "Director");
        Assert.IsFalse(cast.Contains("<img", StringComparison.Ordinal), "No profile photo is persisted: portraits are placeholders.");

        var about = Between(html, "<section class=\"vd-card vd-about\"", "</section>");
        StringAssert.Contains(about, "Every road ends somewhere.");
        StringAssert.Contains(about, "Friday, March 1, 2024");
        StringAssert.Contains(about, "Northlight Pictures · Orbital Films");
        StringAssert.Contains(about, "Science Fiction · Adventure · Drama · Mystery");
        StringAssert.Contains(about, "US · GB");
        Assert.IsFalse(about.Contains(Overview, StringComparison.Ordinal), "About does not repeat the description the hero already carries.");

        var order = new[] { "class=\"ad-hero", "class=\"vd-trailer\"", "class=\"vd-card\"", "class=\"vd-cast\"", "class=\"vd-card vd-about\"" };
        var positions = order.Select(x => html.IndexOf(x, StringComparison.Ordinal)).ToArray();
        CollectionAssert.AreEqual(positions.Order().ToArray(), positions, "Hero, Trailer, Versions & Languages, Cast & Crew, About: one scroll, no tabs.");
        Assert.IsFalse(positions.Any(x => x < 0));
        Assert.IsFalse(html.Contains("role=\"tablist\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TheTrailerIsAFacadeUntilTheViewerStartsItAndFramesOnlyTheNoCookieOrigin()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "The Last Meridian", 2024);
        await SeedMetadataAsync(host, movie, Full());

        var (status, html, headers) = await host.GetRawAsync($"/Library/Movie/{movie.Id}");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.IsFalse(html.Contains("<iframe", StringComparison.OrdinalIgnoreCase), "No frame before the click.");
        Assert.IsFalse(Regex.IsMatch(html, @"(src|srcset|data-src)\s*=\s*""https?://", RegexOptions.IgnoreCase), "Nothing third-party is loaded by markup.");
        Assert.IsFalse(Regex.IsMatch(html, @"<script[^>]+src\s*=\s*""https?://", RegexOptions.IgnoreCase), "No third-party script.");
        Assert.IsFalse(html.Contains("youtube-nocookie.com", StringComparison.Ordinal), "The embed address is built by the script after the click, from the validated key.");
        Assert.AreEqual("frame-src 'self' https://www.youtube-nocookie.com", headers.GetValues("Content-Security-Policy").Single());
    }

    [TestMethod]
    public async Task ArtworkAlwaysComesThroughTheJularrEndpointAndNeverFromTheProviderCdn()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "The Last Meridian", 2024);
        await SeedMetadataAsync(host, movie, Full());

        var (_, html, _) = await host.GetRawAsync($"/Library/Movie/{movie.Id}");

        var sources = Regex.Matches(html, @"<img\b[^>]*\bsrc=""([^""]+)""").Select(x => x.Groups[1].Value).Where(x => x.Contains("/artwork/", StringComparison.Ordinal)).ToArray();
        Assert.IsTrue(sources.Length >= 2, "The hero backdrop and the trailer still.");
        foreach (var source in sources)
        {
            StringAssert.Matches(source, new Regex($@"^/works/{movie.Id:D}/artwork/\d+\?v=[0-9a-f]{{12}}$"));
        }

        foreach (var forbidden in new[] { "image.tmdb.org", "themoviedb", "/x.jpg", ".webp", "/data/cache" })
        {
            Assert.IsFalse(html.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"The page must not mention '{forbidden}'.");
        }
    }

    [TestMethod]
    public async Task WithoutABackdropThePosterBecomesTheBlurredBackgroundAndWithoutAnyArtworkTheGradientStays()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var posterOnly = await AddTitleAsync(host, WorkMediaType.Movie, "Quiet Harbor", 2022, "701");
        await SeedMetadataAsync(host, posterOnly, new MetadataSeed(Poster: true, Overview: "A ferry captain."));
        var bare = await AddTitleAsync(host, WorkMediaType.Movie, "Plain Film", 2023, "702");

        var derived = Between(await host.GetOkAsync($"/Library/Movie/{posterOnly.Id}"), "<section class=\"ad-hero", "</section>");
        var plain = Between(await host.GetOkAsync($"/Library/Movie/{bare.Id}"), "<section class=\"ad-hero", "</section>");

        StringAssert.Contains(derived, "<section class=\"ad-hero ad-hero-derived\"");
        StringAssert.Contains(derived, "<img class=\"ad-hero-art\"");
        Assert.IsFalse(derived.Contains("vd-hero-plain", StringComparison.Ordinal));
        StringAssert.Contains(plain, "<section class=\"ad-hero ad-hero-derived vd-hero-plain\"");
        Assert.IsFalse(plain.Contains("<img", StringComparison.Ordinal), "No artwork, no image: the gradient is the hero.");
    }

    [TestMethod]
    public async Task SectionsWithoutDataDisappearEntirelyAndPartialMetadataStillRenders()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var partial = await AddTitleAsync(host, WorkMediaType.Movie, "Quiet Harbor", 2022, "701");
        await SeedMetadataAsync(host, partial, new MetadataSeed(Overview: "A ferry captain takes one last crossing."));
        var bare = await AddTitleAsync(host, WorkMediaType.Movie, "Plain Film", 2023, "702");

        foreach (var movie in new[] { partial, bare })
        {
            var html = await host.GetOkAsync($"/Library/Movie/{movie.Id}");

            foreach (var absent in new[] { "vd-trailer", "vd-cast", "vd-about", "Cast &", "Trailer", "data-vd-trailer", "ad-strip-cert", "(621" })
            {
                Assert.IsFalse(html.Contains(absent, StringComparison.Ordinal), $"'{absent}' must not render without its data.");
            }

            StringAssert.Contains(html, "ad-hero");
            StringAssert.Contains(html, "Versions & languages");
        }

        StringAssert.Contains(await host.GetOkAsync($"/Library/Movie/{partial.Id}"), "A ferry captain takes one last crossing.");
    }

    [TestMethod]
    public async Task ProviderTextIsEncodedAndAnInvalidTrailerKeyNeverBecomesALink()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Hostile", 2020);
        await SeedMetadataAsync(
            host,
            movie,
            new MetadataSeed(
                Title: "<script>alert('title')</script>",
                Overview: "<img src=x onerror=alert(1)> </p><script>alert(2)</script>",
                Tagline: "\"><svg onload=alert(3)>",
                Genres: ["<i>Drama</i>"],
                Trailers: ["x\" onload=\"alert(4)", "https://evil.example/watch?v=1", "short"],
                Facts: true,
                Studios: ["\"><img src=x onerror=alert(5)>"],
                Credits: true,
                OriginalTitle: "<b>Orig</b>"));

        var (status, html, _) = await host.GetRawAsync($"/Library/Movie/{movie.Id}");

        Assert.AreEqual(HttpStatusCode.OK, status);
        foreach (var live in new[] { "<script>alert", "<img src=x", "<svg onload", "<i>Drama", "<b>Orig", "x\" onload" })
        {
            Assert.IsFalse(html.Contains(live, StringComparison.Ordinal), $"'{live}' reached the page as markup.");
        }

        Assert.IsFalse(Regex.IsMatch(html, @"<[a-z][^>]*\son(error|load)\s*=", RegexOptions.IgnoreCase), "No tag carries an event handler of provider text.");

        StringAssert.Contains(html, "&lt;script&gt;alert(&#x27;title&#x27;)&lt;/script&gt;");
        StringAssert.Contains(html, "&lt;img src=x onerror=alert(1)&gt;");
        Assert.IsFalse(html.Contains("data-vd-trailer", StringComparison.Ordinal), "A key that is not a YouTube id never builds a trailer, a link or a frame.");
        Assert.IsFalse(html.Contains("evil.example", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ASeriesHeroUsesTheBackdropGenresRatingAndStudioButHasNoMovieSections()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var series = await AddTitleAsync(host, WorkMediaType.Series, "Harbor Lights", 2021, "1399");
        await SeedMetadataAsync(host, series, Full() with { Title = "Harbor Lights", Trailers = ["BdJKm16Co6M"] });

        var (_, html, headers) = await host.GetRawAsync($"/Library/Series/{series.Id}");

        var hero = Between(html, "<section class=\"ad-hero", "</section>");
        StringAssert.Contains(hero, $"<img class=\"ad-hero-art\" src=\"/works/{series.Id:D}/artwork/");
        Assert.AreEqual(1, Regex.Matches(hero, "<img ").Count, "No second poster inside the hero.");
        StringAssert.Contains(hero, "<li>Science Fiction</li>");
        StringAssert.Contains(hero, Overview);
        StringAssert.Contains(hero, "8.1");
        StringAssert.Contains(hero, "Northlight Pictures");
        foreach (var movieOnly in new[] { "vd-trailer", "vd-cast", "vd-about", "data-vd-trailer" })
        {
            Assert.IsFalse(html.Contains(movieOnly, StringComparison.Ordinal), $"'{movieOnly}' belongs to the Movie page.");
        }

        Assert.IsFalse(headers.Contains("Content-Security-Policy"), "The Series page frames nothing.");
        StringAssert.Contains(html, "class=\"ad-main");
    }

    [TestMethod]
    public async Task ArtworkThatIsGoneIsDroppedByTheScriptNotByTheServerAndTheEndpointAnswersNotFound()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Broken Art", 2021);
        await SeedMetadataAsync(host, movie, Full());

        var html = await host.GetOkAsync($"/Library/Movie/{movie.Id}");
        var url = Regex.Match(html, @"<img class=""ad-hero-art"" src=""([^""]+)""").Groups[1].Value;

        Assert.IsTrue(url.StartsWith("/works/", StringComparison.Ordinal), url);
        StringAssert.Contains(html, "data-work-art");
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync(url)).Status, "The cached file is not there: the endpoint answers 404 and work-metadata.js removes the image.");
        StringAssert.Contains(html, "/js/work-metadata.js");
    }

    [TestMethod]
    public async Task OpeningAWorkWithoutFreshMetadataQueuesItOnceAtInteractivePriorityAndFreshMetadataWritesNothing()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var bare = await AddTitleAsync(host, WorkMediaType.Movie, "Plain Film", 2023, "702");
        var fresh = await AddTitleAsync(host, WorkMediaType.Movie, "Fresh Film", 2024, "703");
        await SeedMetadataAsync(host, fresh, Full());

        await host.GetOkAsync($"/Library/Movie/{bare.Id}");
        await host.GetOkAsync($"/Library/Movie/{bare.Id}");
        await host.GetOkAsync($"/Library/Movie/{fresh.Id}");

        var entries = await host.Db.Set<WorkMetadataRefresh>().AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        var queued = entries.Single(x => x.WorkId == bare.Id);
        Assert.AreEqual(WorkMetadataRefreshPriority.Interactive, queued.Priority);
        Assert.AreEqual(WorkMetadataRefreshStatus.Queued, queued.Status);
        Assert.AreEqual(0, queued.Attempts, "Opening a page never runs the fetch.");
        var freshEntry = entries.Single(x => x.WorkId == fresh.Id);
        Assert.AreEqual(WorkMetadataRefreshStatus.Fresh, freshEntry.Status, "A fresh entry is neither promoted nor re-queued.");
        Assert.AreEqual(WorkMetadataRefreshPriority.Imported, freshEntry.Priority, "Its priority is the seeded one.");
        Assert.AreEqual(2, entries.Count);
    }

    [TestMethod]
    public async Task AWorkThatCannotBeOpenedIsNeverQueued()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Hidden Film", 2023, "702");
        var series = await AddTitleAsync(host, WorkMediaType.Series, "Wrong Page", 2023, "703");
        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Hidden);

        Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync($"/Library/Movie/{movie.Id}")).Status, "A hidden type is a 404.");
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync($"/Library/Movie/{series.Id}")).Status, "A Work of another media type is a 404.");
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync($"/Library/Movie/{Guid.NewGuid()}")).Status);

        Assert.AreEqual(0, await host.Db.Set<WorkMetadataRefresh>().CountAsync(), "Nothing a profile cannot open is promoted.");
    }

    [TestMethod]
    public async Task AQueueThatFailsNeverBreaksThePage()
    {
        var broken = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=unused.db").Options);
        await broken.DisposeAsync();
        await using var host = await VideoDetailPageTestHost.CreateAsync(
            metadataRefreshQueue: _ => new WorkMetadataRefreshQueue(new WorkMetadataStore(broken), new WorkMetadataRefreshSignal(), TimeProvider.System));
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Plain Film", 2023, "702");

        var (status, html) = await host.GetAsync($"/Library/Movie/{movie.Id}");

        Assert.AreEqual(HttpStatusCode.OK, status, html);
        StringAssert.Contains(html, "Plain Film");
    }

    [TestMethod]
    public async Task OnAManagerOnlyInstanceTheMetadataRendersAndNoPlayerIsOffered()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "The Last Meridian", 2024);
        await SeedMetadataAsync(host, movie, Full());
        await host.Modules.SetAsync(Jularr.Web.Features.Instance.InstanceModule.Playback, false);

        var html = await host.GetOkAsync($"/Library/Movie/{movie.Id}");

        StringAssert.Contains(html, "The Last Meridian");
        StringAssert.Contains(html, "class=\"vd-trailer\"", "The trailer is Movie content, not Jularr playback.");
        StringAssert.Contains(html, "class=\"vd-cast\"");
        Assert.IsFalse(html.Contains("/Library/Watch", StringComparison.Ordinal), "No page element links into the player.");
    }

    [TestMethod]
    public void TheValueFormattersWriteRuntimesRatingsCountsAndInitialsOneWay()
    {
        Assert.AreEqual("2h 46m", VideoDetailView.RuntimeText(166));
        Assert.AreEqual("1h 05m", VideoDetailView.RuntimeText(65));
        Assert.AreEqual("48m", VideoDetailView.RuntimeText(48));
        Assert.AreEqual("8.1", VideoDetailView.RatingText(8.1));
        Assert.AreEqual("7.0", VideoDetailView.RatingText(7));
        Assert.AreEqual("7.7", VideoDetailView.RatingText(7.65), "Halves round up, like the card rating.");
        Assert.AreEqual("812", VideoDetailView.CompactCount(812));
        Assert.AreEqual("12.3K", VideoDetailView.CompactCount(12_345));
        Assert.AreEqual("621.3K", VideoDetailView.CompactCount(621_345));
        Assert.AreEqual("1.2M", VideoDetailView.CompactCount(1_234_000));
        Assert.AreEqual("ME", VideoDetailView.Initials("Mara Elling"));
        Assert.AreEqual("ZV", VideoDetailView.Initials("Zoe van der Veen"));
        Assert.AreEqual("Y", VideoDetailView.Initials("Yuki"));
        Assert.AreEqual("<", VideoDetailView.Initials("<b>Eve</b>"));
        Assert.AreEqual("·", VideoDetailView.Initials("  "));
    }

    [TestMethod]
    public void OnlyAKeyWithTheShapeOfAYouTubeIdBecomesATrailer()
    {
        foreach (var valid in new[] { "BdJKm16Co6M", "a-b_C1d2E3f" })
        {
            Assert.IsTrue(WorkTrailerView.IsYouTubeKey(valid), valid);
            Assert.IsTrue(new WorkTrailerView(valid).IsPlayable);
        }

        foreach (var invalid in new[] { null, "", "short", "BdJKm16Co6M1", "BdJKm16Co6 ", "x\" onload=\"a", "../../etc/pw", "BdJKm16Co6/", "https://x.y/z" })
        {
            Assert.IsFalse(WorkTrailerView.IsYouTubeKey(invalid), invalid);
        }
    }
}
