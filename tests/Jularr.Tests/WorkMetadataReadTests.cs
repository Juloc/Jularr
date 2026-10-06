using System.Net;
using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Progress;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

/// <summary>
/// How persisted Work metadata reaches pages (#820 slice 1): the one locale fallback, the artwork choice, the Detail and Library read
/// models (local, bounded, no provider data) and the artwork endpoint, plus the untrusted-URL rules of the artwork cache.
/// </summary>
[TestClass]
public sealed class WorkMetadataReadTests
{
    [TestMethod]
    public void TheLocaleFallbackGoesFromExactToBaseToFallbackToEnglishToOriginal()
    {
        CollectionAssert.AreEqual(new[] { "de-AT", "de", "en", "ja" }, WorkMetadataLocales.ResolutionOrder("de-AT", null, "ja").ToArray());
        CollectionAssert.AreEqual(new[] { "de-AT", "de", "fr-CA", "fr", "en", "ja" }, WorkMetadataLocales.ResolutionOrder("de_at", "fr-CA", "ja").ToArray());
        CollectionAssert.AreEqual(new[] { "en-GB", "en" }, WorkMetadataLocales.ResolutionOrder("en-GB", null, "en").ToArray(), "Duplicate steps are skipped.");
        CollectionAssert.AreEqual(new[] { "en" }, WorkMetadataLocales.ResolutionOrder("not a locale", null, null).ToArray());

        var order = WorkMetadataLocales.ResolutionOrder("de-AT", null, "ja");
        Assert.AreEqual("de", WorkMetadataLocales.Pick(["de", "de-DE", "en"], order), "The parent beats a regional sibling.");
        Assert.AreEqual("de-DE", WorkMetadataLocales.Pick(["de-DE", "en"], order), "A neutral step takes a regional sibling of the same language.");
        Assert.AreEqual("en", WorkMetadataLocales.Pick(["fr", "en", "ja"], order));
        Assert.AreEqual("ja", WorkMetadataLocales.Pick(["fr", "ja"], order), "The original language comes after English.");
        Assert.AreEqual("fr", WorkMetadataLocales.Pick(["it", "fr"], order), "With no step stored, the best local value is chosen deterministically.");
        Assert.IsNull(WorkMetadataLocales.Pick([], order));

        Assert.AreEqual("US", WorkMetadataLocales.CertificationCountry("en"));
        Assert.AreEqual("AT", WorkMetadataLocales.CertificationCountry("de-AT"));
    }

    [TestMethod]
    public void ArtworkPrefersTheViewersLanguageForPostersAndTextlessBackdrops()
    {
        Uri Cdn(string path) => new($"https://image.tmdb.org/t/p/w780{path}");
        WorkArtworkCandidate[] candidates =
        [
            new(WorkArtworkSlot.Poster, "en", "/en-low.jpg", Cdn("/en-low.jpg"), 1000, 1500, 5.0, 10),
            new(WorkArtworkSlot.Poster, "en", "/en-high.jpg", Cdn("/en-high.jpg"), 1000, 1500, 5.4, 1),
            new(WorkArtworkSlot.Poster, "de", "/de.jpg", Cdn("/de.jpg"), 1000, 1500, 9.0, 1),
            new(WorkArtworkSlot.Poster, "", "/neutral.jpg", Cdn("/neutral.jpg"), 1000, 1500, 1.0, 1),
            new(WorkArtworkSlot.Backdrop, "en", "/backdrop-en.jpg", Cdn("/backdrop-en.jpg"), 3840, 2160, 9.0, 9),
            new(WorkArtworkSlot.Backdrop, "", "/backdrop-a.jpg", Cdn("/backdrop-a.jpg"), 1920, 1080, 5.0, 5),
            new(WorkArtworkSlot.Backdrop, "", "/backdrop-b.jpg", Cdn("/backdrop-b.jpg"), 3840, 2160, 5.0, 5),
            new(WorkArtworkSlot.Logo, "ja", "/logo-ja.png", Cdn("/logo-ja.png"), 500, 200, 5.0, 5)
        ];

        var kept = WorkArtworkSelection.SelectForLocale(candidates, "en").Select(x => x.ProviderFilePath).ToArray();

        CollectionAssert.AreEqual(new[] { "/en-high.jpg", "/neutral.jpg", "/backdrop-en.jpg", "/backdrop-b.jpg", "/logo-ja.png" }, kept, "Per slot: best in the language, best neutral (ties by size), else the best of any.");

        var stored = candidates.Where(x => kept.Contains(x.ProviderFilePath) || x.ProviderFilePath == "/de.jpg").ToArray();
        string? Pick(WorkArtworkSlot slot, string viewer) =>
            WorkArtworkSelection.Pick(stored.Where(x => x.Slot == slot), slot, WorkMetadataLocales.ArtworkLanguages(viewer, null), x => x.Language, x => x.VoteAverage)?.ProviderFilePath;
        Assert.AreEqual("/de.jpg", Pick(WorkArtworkSlot.Poster, "de-AT"));
        Assert.AreEqual("/neutral.jpg", Pick(WorkArtworkSlot.Poster, "fr"), "Neutral artwork beats a poster in a language the viewer did not ask for.");
        Assert.AreEqual("/backdrop-b.jpg", Pick(WorkArtworkSlot.Backdrop, "en"), "Textless backdrops win even over the viewer's language.");
        Assert.AreEqual("/logo-ja.png", Pick(WorkArtworkSlot.Logo, "en"), "Any variant beats an empty slot.");
    }

    [TestMethod]
    public void UntrustedImagePathsNeverLeaveTheTmdbCdn()
    {
        Assert.IsTrue(TmdbDiscoveryProvider.TryBuildImageUri("/abc_DEF-1.jpg", "w780", out var uri));
        Assert.AreEqual("https://image.tmdb.org/t/p/w780/abc_DEF-1.jpg", uri.AbsoluteUri);
        foreach (var path in new[] { null, "", "abc.jpg", "/../etc/passwd.jpg", "/a/b.jpg", "//evil.example/x.jpg", "http://evil.example/x.jpg", "/x.jpg?y=1", "/x.svg", "/x.jpg#f", "/x%2F.jpg" })
        {
            Assert.IsFalse(TmdbDiscoveryProvider.TryBuildImageUri(path, "w780", out _), path);
        }

        Assert.IsFalse(TmdbDiscoveryProvider.TryBuildImageUri("/x.jpg", "../w780", out _));
    }

    [TestMethod]
    public async Task TheArtworkCacheOnlyFetchesHttpsFromAllowedHostsAndKeepsOnlyRealImages()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jularr-work-artwork-{Guid.NewGuid():N}");
        var requests = new List<Uri>();
        Func<HttpRequestMessage, HttpResponseMessage> respond = _ => WorkMetadataFixture.Png();
        var cache = new WorkArtworkCache(root, ["image.tmdb.org"], new WorkMetadataFixture.StubHttpClientFactory(new WorkMetadataFixture.StubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            var response = respond(request);
            response.RequestMessage ??= request;
            return response;
        })));
        var key = WorkArtworkCache.CacheKey(Guid.NewGuid(), WorkArtworkSlot.Poster, "en", "tmdb", "/a.jpg");
        try
        {
            Assert.IsFalse(await cache.EnsureAsync(new Uri("https://evil.example/a.jpg"), WorkArtworkSlot.Poster, key, CancellationToken.None));
            Assert.IsFalse(await cache.EnsureAsync(new Uri("http://image.tmdb.org/a.jpg"), WorkArtworkSlot.Poster, key, CancellationToken.None));
            Assert.IsFalse(await cache.EnsureAsync(new Uri("https://image.tmdb.org:8443/a.jpg"), WorkArtworkSlot.Poster, key, CancellationToken.None));
            Assert.AreEqual(0, requests.Count, "A refused address is never requested.");

            respond = _ =>
            {
                var redirected = WorkMetadataFixture.Png();
                redirected.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://internal.example/a.jpg");
                return redirected;
            };
            Assert.IsFalse(await cache.EnsureAsync(new Uri("https://image.tmdb.org/t/p/w780/a.jpg"), WorkArtworkSlot.Poster, key, CancellationToken.None), "A redirect off the CDN is refused.");

            respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not an image</html>", System.Text.Encoding.UTF8, "text/html") };
            Assert.IsFalse(await cache.EnsureAsync(new Uri("https://image.tmdb.org/t/p/w780/a.jpg"), WorkArtworkSlot.Poster, key, CancellationToken.None));

            respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) { Headers = { ContentType = new("image/png") } } };
            Assert.IsFalse(await cache.EnsureAsync(new Uri("https://image.tmdb.org/t/p/w780/a.jpg"), WorkArtworkSlot.Poster, key, CancellationToken.None), "Bytes that are not a decodable image are refused.");
            Assert.IsFalse(File.Exists(cache.PathFor(key)));

            respond = _ => WorkMetadataFixture.Png();
            Assert.IsTrue(await cache.EnsureAsync(new Uri("https://image.tmdb.org/t/p/w780/a.jpg"), WorkArtworkSlot.Poster, key, CancellationToken.None));
            Assert.IsTrue(File.Exists(cache.PathFor(key)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CacheKeysAreDeterministicHexAndNothingElseBecomesAPath()
    {
        var workId = Guid.NewGuid();
        var key = WorkArtworkCache.CacheKey(workId, WorkArtworkSlot.Poster, "en", "tmdb", "/a.jpg");
        Assert.AreEqual(key, WorkArtworkCache.CacheKey(workId, WorkArtworkSlot.Poster, "en", "tmdb", "/a.jpg"));
        Assert.AreNotEqual(key, WorkArtworkCache.CacheKey(workId, WorkArtworkSlot.Poster, "en", "tmdb", "/b.jpg"));
        Assert.IsTrue(WorkArtworkCache.IsCacheKey(key));

        var root = Path.Combine(Path.GetTempPath(), "jularr-cache-root");
        var cache = new WorkArtworkCache(root, [], new WorkMetadataFixture.StubHttpClientFactory(new WorkMetadataFixture.StubHandler(_ => throw new InvalidOperationException())));
        StringAssert.StartsWith(cache.PathFor(key), root);
        foreach (var hostile in new[] { null, "", "../../etc/passwd", key.ToUpperInvariant(), key + "/..", key[..31], "..\\" + key[3..] })
        {
            Assert.IsNull(cache.PathFor(hostile), hostile);
        }
    }

    [TestMethod]
    public async Task TheDetailReadModelResolvesTheProfileLocaleAndCarriesNoProviderData()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var work = await fixture.AddMovieAsync();
        await fixture.Queue.RequestMetadataRefreshAsync(work.Id, interactive: false, CancellationToken.None);
        await fixture.RunSpoolAsync();
        var now = fixture.Clock.GetUtcNow().UtcDateTime;
        await fixture.Store.ReplaceLocalizedFieldAsync(work.Id, "de", WorkLocalizedField.Title, ["Fight Club (DE)"], "tmdb", "550", 10, now, CancellationToken.None);

        var catalog = new UiTranslationCatalogStore(fixture.Db);
        await catalog.AddLocaleAsync("de-AT", CancellationToken.None);
        await catalog.SetProfileLocaleAsync("viewer", "de-AT", CancellationToken.None);
        var query = new VideoDetailQuery(fixture.Db, new AcquisitionAccessStore(fixture.Db), new VideoProgressService(fixture.Db), fixture.Clock);

        var german = (await query.GetAsync("viewer", work.Id, WorkMediaType.Movie, [WorkMediaType.Movie], CancellationToken.None))!;
        var english = (await query.GetAsync("someone-else", work.Id, WorkMediaType.Movie, [WorkMediaType.Movie], CancellationToken.None))!;

        Assert.AreEqual("Fight Club (DE)", german.Title, "The German title of the de-AT profile, through the parent language.");
        Assert.AreEqual("An insomniac meets a soap maker.", german.Metadata!.Overview, "Each field falls back on its own.");
        Assert.AreEqual("Fight Club", english.Title);
        var metadata = english.Metadata!;
        CollectionAssert.AreEqual(new[] { "Drama", "Thriller" }, metadata.Genres.ToArray());
        Assert.AreEqual(139, metadata.RuntimeMinutes);
        Assert.AreEqual("R", metadata.Certification);
        Assert.AreEqual("Edward Norton", metadata.Cast[0].Name);
        Assert.AreEqual("Director", metadata.Crew.Single().Role);
        Assert.AreEqual("https://www.youtube-nocookie.com/embed/BdJKm16Co6M", metadata.Trailers.Single().EmbedUrl);
        StringAssert.StartsWith(metadata.Poster!.Url, $"/works/{work.Id:D}/artwork/");
        StringAssert.StartsWith(metadata.Backdrop!.Url, $"/works/{work.Id:D}/artwork/");
        Assert.IsNotNull(metadata.Logo);

        var json = JsonSerializer.Serialize(metadata);
        foreach (var leak in new[] { "image.tmdb.org", "api_key", "secret-test-key", "/poster-en-best.jpg", "/backdrop-neutral-best.jpg", fixture.CacheRoot.Replace('\\', '/'), "\\\\", ".webp" })
        {
            Assert.IsFalse(json.Contains(leak, StringComparison.OrdinalIgnoreCase), $"The read model leaks '{leak}': {json}");
        }
    }

    [TestMethod]
    public async Task AWorkWithoutPersistedMetadataHasNoneAndReadingNeverQueuesAnything()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var work = await fixture.AddMovieAsync();
        var query = new VideoDetailQuery(fixture.Db, new AcquisitionAccessStore(fixture.Db), new VideoProgressService(fixture.Db), fixture.Clock);

        var detail = (await query.GetAsync("viewer", work.Id, WorkMediaType.Movie, [WorkMediaType.Movie], CancellationToken.None))!;
        await new LibraryMediaCardQuery(fixture.Db, fixture.Clock).GetEntriesAsync("viewer", [WorkMediaType.Movie], CancellationToken.None);

        Assert.IsNull(detail.Metadata);
        Assert.AreEqual("Fight Club", detail.Title);
        Assert.AreEqual(0, await fixture.Db.Set<WorkMetadataRefresh>().CountAsync(), "Reads are side-effect free; the page asks for a refresh explicitly.");
    }

    [TestMethod]
    public async Task LibraryCardsCarryLocalArtworkWithABoundedNumberOfQueriesWhateverThePageSize()
    {
        await using var fixture = await WorkMetadataFixture.CreateAsync();
        var counter = new LibraryCanonicalReadTests.CommandCounter();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(fixture.Db.Database.GetConnectionString()).AddInterceptors(counter).Options);
        var seed = new LibraryCanonicalSeed(fixture.Db);

        async Task AddTitlesAsync(string prefix, int count)
        {
            for (var index = 0; index < count; index++)
            {
                foreach (var type in new[] { WorkMediaType.Movie, WorkMediaType.Series })
                {
                    var work = await seed.AddWorkAsync(type, $"{prefix}-{type}-{index}");
                    await seed.AddVideoAsync(work, type == WorkMediaType.Movie ? null : await seed.AddEpisodeAsync(work, 1, 1));
                    await SeedArtworkAsync(fixture, work.Id);
                }
            }
        }

        await AddTitlesAsync("first", 1);
        await new LibraryMediaCardQuery(db).GetEntriesAsync("viewer", [WorkMediaType.Movie, WorkMediaType.Series], CancellationToken.None);
        var single = counter.Count;

        await AddTitlesAsync("more", 6);
        counter.Count = 0;
        var read = await new LibraryMediaCardQuery(db).GetEntriesAsync("viewer", [WorkMediaType.Movie, WorkMediaType.Series], CancellationToken.None);

        Assert.AreEqual(14, read.Entries.Count);
        Assert.AreEqual(single, counter.Count, "Artwork is loaded for the whole page at once, never per card.");
        foreach (var entry in read.Entries)
        {
            StringAssert.StartsWith(entry.PosterUrl, $"/works/{entry.WorkId:D}/artwork/", entry.Card.Title);
            StringAssert.StartsWith(entry.Card.BackdropUrl, $"/works/{entry.WorkId:D}/artwork/", entry.Card.Title);
            Assert.AreEqual(77, entry.Card.AverageScore, "A 7.7 rating on the card's 0-100 scale.");
        }
    }

    [TestMethod]
    public async Task TheArtworkEndpointServesOnlyVisibleWorksFromTheLocalCache()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await new LibraryCanonicalSeed(host.Db).AddWorkAsync(WorkMediaType.Movie, "Moon Empire", 2024);
        var cache = host.Services.GetRequiredService<WorkArtworkCache>();
        var key = WorkArtworkCache.CacheKey(movie.Id, WorkArtworkSlot.Poster, "en", "tmdb", "/p.jpg");
        var path = cache.PathFor(key)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [82, 73, 70, 70]);
        var poster = new WorkArtworkCandidate(WorkArtworkSlot.Poster, "en", "/p.jpg", new Uri("https://image.tmdb.org/t/p/w780/p.jpg"), 2, 3, 5, 1);
        await new WorkMetadataStore(host.Db).UpsertArtworkAsync(movie.Id, poster, "tmdb", key, DateTime.UtcNow, CancellationToken.None);
        var artworkId = (await host.Db.Set<WorkArtwork>().AsNoTracking().SingleAsync()).Id;
        var url = $"/works/{movie.Id:D}/artwork/{artworkId}";

        using var client = host.CreateClient();
        using var response = await client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("image/webp", response.Content.Headers.ContentType?.MediaType);
        StringAssert.Contains(response.Headers.CacheControl?.ToString(), "private");

        Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync($"/works/{Guid.NewGuid():D}/artwork/{artworkId}")).Status, "The variant belongs to another Work.");
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync($"/works/{movie.Id:D}/artwork/{artworkId + 1}")).Status);

        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Hidden);
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync(url)).Status, "A hidden media type has no artwork either.");
    }

    private static async Task SeedArtworkAsync(WorkMetadataFixture fixture, Guid workId)
    {
        var now = DateTime.UtcNow;
        foreach (var slot in new[] { WorkArtworkSlot.Poster, WorkArtworkSlot.Backdrop })
        {
            var key = WorkArtworkCache.CacheKey(workId, slot, "", "tmdb", "/x.jpg");
            await fixture.Store.UpsertArtworkAsync(workId, new WorkArtworkCandidate(slot, "", "/x.jpg", new Uri("https://image.tmdb.org/t/p/w780/x.jpg"), 2, 3, 5, 1), "tmdb", key, now, CancellationToken.None);
        }

        await fixture.Store.UpsertFactsAsync(new WorkMetadataFacts { WorkId = workId, Rating = 7.7, RatingCount = 10, UpdatedAt = now }, CancellationToken.None);
    }
}
