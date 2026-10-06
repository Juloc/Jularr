using System.Net;
using System.Text;
using Jularr.Web.Data;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Jularr.Tests;

/// <summary>
/// The Work metadata spool over a real PostgreSQL database with a fake TMDB API and a fake TMDB image CDN: no network is used. The
/// services are wired like production (store, refresher, TMDB adapter through the provider executor, artwork cache under a temporary
/// directory), so <see cref="RunSpoolAsync"/> is exactly one pass of the background worker.
/// </summary>
internal sealed class WorkMetadataFixture : IAsyncDisposable
{
    private readonly ServiceProvider services;

    private WorkMetadataFixture(AppDbContext db, ManualClock clock, string cacheRoot)
    {
        Db = db;
        Clock = clock;
        CacheRoot = cacheRoot;
        var collection = new ServiceCollection();
        collection.AddSingleton(db);
        collection.AddSingleton<TimeProvider>(clock);
        collection.AddLogging();
        collection.AddScoped<WorkService>();
        collection.AddScoped<WorkStructureService>();
        collection.AddScoped<LegacyWorkBridge>();
        collection.AddScoped<WorkMetadataStore>();
        collection.AddSingleton<WorkMetadataRefreshSignal>();
        collection.AddScoped<WorkMetadataRefreshQueue>();
        collection.AddSingleton(new ProviderExecutor(new ProviderRateLimiter(), new ProviderHealthTracker(TimeProvider.System), TimeProvider.System, NullLogger<ProviderExecutor>.Instance));
        collection.AddScoped(provider => new TmdbDiscoveryProvider(
            new HttpClient(new StubHandler(request => Record(TmdbRequests, request, Tmdb))) { BaseAddress = new Uri("https://api.themoviedb.org/3/") },
            new ConfigurationManager { ["Providers:Tmdb:ApiKey"] = "secret-test-key" },
            provider.GetRequiredService<ProviderExecutor>(),
            new ProviderResponseCache(TimeProvider.System),
            provider.GetRequiredService<WorkService>(),
            provider.GetRequiredService<WorkStructureService>(),
            db,
            provider.GetRequiredService<LegacyWorkBridge>(),
            provider.GetRequiredService<WorkMetadataRefreshQueue>()));
        collection.AddSingleton(new WorkArtworkCache(cacheRoot, [TmdbDiscoveryProvider.ImageHost], new StubHttpClientFactory(new StubHandler(request => Record(CdnRequests, request, Cdn)))));
        collection.AddScoped<WorkMetadataRefresher>();
        services = collection.BuildServiceProvider();
    }

    public AppDbContext Db { get; }

    public ManualClock Clock { get; }

    public string CacheRoot { get; }

    /// <summary>The fake TMDB API; defaults to the Fight Club fixture for <c>/3/movie/550</c>.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> Tmdb { get; set; } = request => request.RequestUri!.AbsolutePath == "/3/movie/550" ? Json(MovieJson()) : new HttpResponseMessage(HttpStatusCode.NotFound);

    /// <summary>The fake image CDN; defaults to a small PNG for every image.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> Cdn { get; set; } = _ => Png();

    public List<string> TmdbRequests { get; } = [];

    public List<string> CdnRequests { get; } = [];

    public IServiceProvider Services => services;

    public WorkMetadataStore Store => services.GetRequiredService<WorkMetadataStore>();

    public WorkArtworkCache Cache => services.GetRequiredService<WorkArtworkCache>();

    public WorkMetadataRefreshQueue Queue => services.GetRequiredService<WorkMetadataRefreshQueue>();

    public static async Task<WorkMetadataFixture> CreateAsync()
    {
        var db = await MediaCoreTestSupport.CreateDbAsync();
        var root = Path.Combine(Path.GetTempPath(), $"jularr-work-artwork-{Guid.NewGuid():N}");
        return new WorkMetadataFixture(db, new ManualClock(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero)), root);
    }

    /// <summary>A Movie Work with its TMDB identity, as Discover's Request creates it.</summary>
    public Task<Work> AddMovieAsync(string tmdbId = "550", string title = "Fight Club") =>
        new WorkService(Db).EnsureWorkByExternalIdentityAsync(WorkMediaType.Movie, ProviderKeys.Tmdb, tmdbId, title, 1999, CancellationToken.None);

    public Task<WorkMetadataPass> RunSpoolAsync() => WorkMetadataRefreshService.ProcessDueAsync(services, 50, CancellationToken.None);

    public async Task<WorkMetadataRefresh> RefreshEntryAsync(Guid workId) =>
        await Db.Set<WorkMetadataRefresh>().AsNoTracking().SingleAsync(x => x.WorkId == workId);

    /// <summary>Makes every spool entry due, as if its time had come.</summary>
    public Task MakeDueAsync() =>
        Db.Database.ExecuteSqlAsync($"""UPDATE "WorkMetadataRefreshes" SET "NextAttemptAt" = {Clock.GetUtcNow().UtcDateTime.AddSeconds(-1)}""");

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Png()
    {
        using var bitmap = new SKBitmap(8, 12);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var content = new ByteArrayContent(data.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>A TMDB movie answer with every appended part the spool reads. Image paths and votes make the expected choice unambiguous.</summary>
    public static string MovieJson(string overview = "An insomniac meets a soap maker.", int runtime = 139, string tagline = "Mischief. Mayhem. Soap.", string bestEnglishPoster = "/poster-en-best.jpg") =>
        $$"""
        {
          "id": 550,
          "title": "Fight Club",
          "original_title": "Fight Club",
          "original_language": "en",
          "overview": "{{overview}}",
          "tagline": "{{tagline}}",
          "release_date": "1999-10-15",
          "runtime": {{runtime}},
          "vote_average": 8.438,
          "vote_count": 30000,
          "genres": [ { "id": 18, "name": "Drama" }, { "id": 53, "name": "Thriller" } ],
          "production_companies": [ { "id": 508, "name": "Regency Enterprises" }, { "id": 711, "name": "Fox 2000 Pictures" } ],
          "production_countries": [ { "iso_3166_1": "US", "name": "United States of America" } ],
          "release_dates": { "results": [
            { "iso_3166_1": "DE", "release_dates": [ { "certification": "18", "type": 3 } ] },
            { "iso_3166_1": "US", "release_dates": [ { "certification": "", "type": 1 }, { "certification": "R", "type": 3 } ] } ] },
          "credits": {
            "cast": [
              { "id": 819, "name": "Edward Norton", "character": "Narrator", "order": 0 },
              { "id": 287, "name": "Brad Pitt", "character": "Tyler Durden", "order": 1 } ],
            "crew": [
              { "id": 7467, "name": "David Fincher", "job": "Director", "department": "Directing" },
              { "id": 1, "name": "Someone Else", "job": "Caterer", "department": "Crew" } ] },
          "videos": { "results": [
            { "key": "BdJKm16Co6M", "site": "YouTube", "type": "Trailer", "official": true, "published_at": "2014-10-02T19:20:22.000Z" },
            { "key": "not a key!", "site": "YouTube", "type": "Trailer", "official": false },
            { "key": "vimeo123456", "site": "Vimeo", "type": "Trailer", "official": true } ] },
          "images": {
            "posters": [
              { "file_path": "{{bestEnglishPoster}}", "iso_639_1": "en", "width": 2000, "height": 3000, "vote_average": 5.6, "vote_count": 20 },
              { "file_path": "/poster-en-worse.jpg", "iso_639_1": "en", "width": 2000, "height": 3000, "vote_average": 5.1, "vote_count": 40 },
              { "file_path": "/poster-de.jpg", "iso_639_1": "de", "width": 1000, "height": 1500, "vote_average": 5.9, "vote_count": 3 },
              { "file_path": "/poster-neutral.jpg", "iso_639_1": null, "width": 1000, "height": 1500, "vote_average": 4.0, "vote_count": 2 } ],
            "backdrops": [
              { "file_path": "/backdrop-neutral-best.jpg", "iso_639_1": null, "width": 3840, "height": 2160, "vote_average": 5.5, "vote_count": 9 },
              { "file_path": "/backdrop-neutral-worse.jpg", "iso_639_1": "xx", "width": 3840, "height": 2160, "vote_average": 5.2, "vote_count": 9 },
              { "file_path": "/../../etc/passwd.jpg", "iso_639_1": null, "width": 1, "height": 1, "vote_average": 9.9, "vote_count": 99 } ],
            "logos": [
              { "file_path": "/logo-en.svg", "iso_639_1": "en", "width": 500, "height": 200, "vote_average": 9.0, "vote_count": 9 },
              { "file_path": "/logo-en.png", "iso_639_1": "en", "width": 500, "height": 200, "vote_average": 5.0, "vote_count": 2 } ] }
        }
        """;

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        if (Directory.Exists(CacheRoot))
        {
            Directory.Delete(CacheRoot, recursive: true);
        }
    }

    private static HttpResponseMessage Record(List<string> log, HttpRequestMessage request, Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        log.Add(request.RequestUri!.PathAndQuery);
        var response = handler(request);
        response.RequestMessage ??= request;
        return response;
    }

    internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }

    internal sealed class StubHttpClientFactory(HttpMessageHandler handler, TimeSpan? timeout = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = timeout ?? TimeSpan.FromSeconds(100) };
    }
}
