using Jularr.Backend.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Media.V1;
using Jularr.Integrations.Logic.Media;
using Jularr.Integrations.Logic.Tmdb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class CoverTests
    {
        private const string Token = "eyJ-cover-token";

        [TestMethod]
        public async Task Cover_StoredOnceForAWork_IsServedLocally_AndLeavesWithIt()
        {
            var images = new FakeImages();
            await using var host = await CreateHostAsync(images);
            var root = host.Services.GetRequiredService<IOptions<MediaOptions>>().Value.CoversPath;

            // Adding the title keeps its poster and backdrop; the facts name Jularr's own address from then on.
            using var requested = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/Movie/603/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\""))!;
            var poster = (string)(await host.ScalarAsync("SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"FactKey\" = 'Poster'"))!;
            StringAssert.StartsWith(poster, "/api/v1/covers/" + workId + "/poster?v=");
            StringAssert.StartsWith((string)(await host.ScalarAsync("SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"FactKey\" = 'Backdrop'"))!, "/api/v1/covers/" + workId + "/backdrop?v=");
            Assert.AreEqual(2, images.Requests.Count);
            Assert.IsTrue(File.Exists(Path.Combine(root, workId.ToString(), "poster.png")));

            // Served from the endpoint with validators and a lasting lifetime; a width that is not stored falls back to the original.
            using var served = await host.SendAsync(HttpMethod.Get, poster);
            Assert.AreEqual(HttpStatusCode.OK, served.StatusCode);
            Assert.AreEqual("image/png", served.Content.Headers.ContentType!.MediaType);
            CollectionAssert.AreEqual(FakeImages.Png, await served.Content.ReadAsByteArrayAsync());
            StringAssert.Contains(served.Headers.CacheControl!.ToString(), "immutable");
            Assert.IsNotNull(served.Headers.ETag);
            using var revalidated = new HttpRequestMessage(HttpMethod.Get, poster);
            revalidated.Headers.IfNoneMatch.Add(served.Headers.ETag);
            revalidated.Headers.Add("Cookie", string.Join("; ", host.Cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
            using var notModified = await host.Client.SendAsync(revalidated);
            Assert.AreEqual(HttpStatusCode.NotModified, notModified.StatusCode);
            using var narrow = await host.SendAsync(HttpMethod.Get, poster.Replace("poster?", "poster-250?", StringComparison.Ordinal));
            Assert.AreEqual(HttpStatusCode.OK, narrow.StatusCode);
            using var unknownWidth = await host.SendAsync(HttpMethod.Get, "/api/v1/covers/" + workId + "/poster-999");
            Assert.AreEqual(HttpStatusCode.NotFound, unknownWidth.StatusCode);
            using var otherWork = await host.SendAsync(HttpMethod.Get, "/api/v1/covers/" + (workId + 1) + "/poster");
            Assert.AreEqual(HttpStatusCode.NotFound, otherWork.StatusCode);
            using var signedOut = await host.Client.GetAsync(poster);
            Assert.AreEqual(HttpStatusCode.Unauthorized, signedOut.StatusCode);

            // A refresh with the same provider address fetches nothing again.
            using var refreshed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/" + workId + "/metadata");
            Assert.AreEqual(HttpStatusCode.OK, refreshed.StatusCode, await refreshed.Content.ReadAsStringAsync());
            Assert.AreEqual(2, images.Requests.Count);
            Assert.AreEqual(poster, (string)(await host.ScalarAsync("SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"FactKey\" = 'Poster'"))!);

            // Deleting the title removes its covers.
            using var deleted = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/" + workId + "/deletion", new DeleteWorkDataV1(false, false));
            Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());
            Assert.IsFalse(Directory.Exists(Path.Combine(root, workId.ToString())));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"WorkCovers\""));
        }

        [TestMethod]
        public async Task Cover_WithFfmpeg_KeepsJpegWidthsBesideTheOriginal()
        {
            // Opt-in: JULARR_TEST_FFMPEG names an ffmpeg executable.
            var ffmpeg = Environment.GetEnvironmentVariable("JULARR_TEST_FFMPEG");
            if (string.IsNullOrEmpty(ffmpeg))
            {
                Assert.Inconclusive("JULARR_TEST_FFMPEG is not configured.");
                return;
            }

            var images = new FakeImages { Payload = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==") };
            await using var host = await CreateHostAsync(images, new Dictionary<string, string> { ["Media:FfmpegPath"] = ffmpeg });
            var root = host.Services.GetRequiredService<IOptions<MediaOptions>>().Value.CoversPath;
            using var requested = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/Movie/603/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\""))!;
            foreach (var name in new[] { "poster.png", "poster-250.jpg", "poster-250.webp", "poster-500.jpg", "backdrop-500.webp", "backdrop-1280.jpg" })
            {
                Assert.IsTrue(File.Exists(Path.Combine(root, workId.ToString(), name)), name);
            }

            var poster = (string)(await host.ScalarAsync("SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"FactKey\" = 'Poster'"))!;
            var narrowAddress = poster.Replace("poster?", "poster-250?", StringComparison.Ordinal);
            using var narrow = await host.SendAsync(HttpMethod.Get, narrowAddress);
            Assert.AreEqual("image/jpeg", narrow.Content.Headers.ContentType!.MediaType);

            // A browser that takes WebP is given WebP; the format is chosen by its Accept header.
            using var request = new HttpRequestMessage(HttpMethod.Get, narrowAddress);
            request.Headers.Accept.ParseAdd("image/avif,image/webp,*/*");
            request.Headers.Add("Cookie", string.Join("; ", host.Cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
            using var modern = await host.Client.SendAsync(request);
            Assert.AreEqual("image/webp", modern.Content.Headers.ContentType!.MediaType);
            Assert.AreEqual("Accept", modern.Headers.Vary.Single());
        }

        [TestMethod]
        public async Task Cover_ChangedAddress_IsFetchedAgain_AndAFailureKeepsTheProviderAddress()
        {
            var images = new FakeImages();
            await using var host = await CreateHostAsync(images);
            using var requested = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/Movie/603/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\""))!;
            var before = (string)(await host.ScalarAsync("SELECT \"LocalUrl\" FROM \"WorkCovers\" WHERE \"CoverKindId\" = 1"))!;

            // The provider names another poster; the new bytes replace the old and get a new version.
            images.Changed = true;
            await host.ScalarAsync("UPDATE \"WorkMetadataFacts\" SET \"Value\" = 'https://image.tmdb.org/t/p/w500/other.png' WHERE \"FactKey\" = 'Poster'");
            await RunAsync(host, scope => scope.ServiceProvider.GetRequiredService<StoreCoversV1>().ExecuteAsync<Jularr.Backend.Data.Common.UpdateResult>(new CoverParametersV1(), default));
            var after = (string)(await host.ScalarAsync("SELECT \"LocalUrl\" FROM \"WorkCovers\" WHERE \"CoverKindId\" = 1"))!;
            Assert.AreNotEqual(before, after);
            Assert.AreEqual(after, (string)(await host.ScalarAsync("SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"FactKey\" = 'Poster'"))!);

            // An address that cannot be fetched stays the provider's own and is not tried again at once.
            images.Failing = true;
            await host.ScalarAsync("UPDATE \"WorkMetadataFacts\" SET \"Value\" = 'https://image.tmdb.org/t/p/w500/gone.png' WHERE \"FactKey\" = 'Poster'");
            var requests = images.Requests.Count;
            await RunAsync(host, scope => scope.ServiceProvider.GetRequiredService<StoreCoversV1>().ExecuteAsync<Jularr.Backend.Data.Common.UpdateResult>(new CoverParametersV1(), default));
            await RunAsync(host, scope => scope.ServiceProvider.GetRequiredService<StoreCoversV1>().ExecuteAsync<Jularr.Backend.Data.Common.UpdateResult>(new CoverParametersV1(), default));
            Assert.AreEqual(requests + 1, images.Requests.Count);
            Assert.AreEqual("https://image.tmdb.org/t/p/w500/gone.png", (string)(await host.ScalarAsync("SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"FactKey\" = 'Poster'"))!);
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"LocalUrl\" IS NULL FROM \"WorkCovers\" WHERE \"CoverKindId\" = 1 AND \"WorkId\" = " + workId));
        }

        [TestMethod]
        public async Task CoverSweep_RemovesFilesNoStoredCoverRefersTo_AndKeepsTheRest()
        {
            var images = new FakeImages();
            await using var host = await CreateHostAsync(images);
            var root = host.Services.GetRequiredService<IOptions<MediaOptions>>().Value.CoversPath;
            using var requested = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/Movie/603/requests", new CreateRequestDataV1(1));
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\""))!;

            // A deleted Work's folder and a replaced image's leftover, both older than an hour.
            var orphanFolder = Directory.CreateDirectory(Path.Combine(root, "999999"));
            var orphan = Path.Combine(orphanFolder.FullName, "poster.jpg");
            var leftover = Path.Combine(root, workId.ToString(), "unit-77.png");
            await File.WriteAllBytesAsync(orphan, FakeImages.Png);
            await File.WriteAllBytesAsync(leftover, FakeImages.Png);
            foreach (var path in new[] { orphan, leftover })
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-1));
            }

            await RunAsync(host, scope => scope.ServiceProvider.GetRequiredService<CleanCoversV1>().ExecuteAsync<Jularr.Backend.Data.Common.UpdateResult>(new CoverParametersV1(), default));
            Assert.IsFalse(Directory.Exists(orphanFolder.FullName));
            Assert.IsFalse(File.Exists(leftover));
            Assert.IsTrue(File.Exists(Path.Combine(root, workId.ToString(), "poster.png")));
        }

        [TestMethod]
        public async Task ExternalCover_FetchedOnceKeptByUse_AndTrimmedByTheOwnersDaysAndSize()
        {
            var images = new FakeImages();
            await using var host = await CreateHostAsync(images);
            var root = host.Services.GetRequiredService<IOptions<MediaOptions>>().Value.CoversPath;
            const string provider = "https://image.tmdb.org/t/p/w500/other.png";
            var address = CoverPaths.External(provider);

            // The first look fetches and keeps the image, the next ones are answered from the cache; the browser never goes to the provider.
            using var first = await host.SendAsync(HttpMethod.Get, address);
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(FakeImages.Png, await first.Content.ReadAsByteArrayAsync());
            using var second = await host.SendAsync(HttpMethod.Get, address);
            Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
            Assert.AreEqual(1, images.Requests.Count);
            var kept = Directory.EnumerateFiles(Path.Combine(root, "external"), "*", SearchOption.AllDirectories).Single();

            // Only the providers' image hosts and the known widths are served.
            using var elsewhere = await host.SendAsync(HttpMethod.Get, CoverPaths.External("https://example.test/poster.png"));
            Assert.AreEqual(HttpStatusCode.NotFound, elsewhere.StatusCode);
            using var odd = await host.SendAsync(HttpMethod.Get, CoverPaths.External(provider, 333));
            Assert.AreEqual(HttpStatusCode.NotFound, odd.StatusCode);
            Assert.AreEqual(1, images.Requests.Count);

            // The Owner's settings: unused images leave after their days, used ones stay.
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/catalog/covers");
            var settings = (await read.Content.ReadFromJsonAsync<CoverCacheV1>())!;
            Assert.AreEqual((7, 200), (settings.KeepDays, settings.MaximumMegabytes));
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/catalog/covers", new UpdateCoverCacheDataV1(3, 50, settings.Version));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var stale = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/catalog/covers", new UpdateCoverCacheDataV1(3, 50, settings.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            File.SetLastWriteTimeUtc(kept, DateTime.UtcNow.AddDays(-2));
            await RunAsync(host, scope => scope.ServiceProvider.GetRequiredService<CleanCoversV1>().ExecuteAsync<Jularr.Backend.Data.Common.UpdateResult>(new CoverParametersV1(), default));
            Assert.IsTrue(File.Exists(kept));
            using var used = await host.SendAsync(HttpMethod.Get, address);
            Assert.AreEqual(HttpStatusCode.OK, used.StatusCode);
            Assert.IsTrue(File.GetLastWriteTimeUtc(kept) > DateTime.UtcNow.AddMinutes(-1));
            File.SetLastWriteTimeUtc(kept, DateTime.UtcNow.AddDays(-4));
            await RunAsync(host, scope => scope.ServiceProvider.GetRequiredService<CleanCoversV1>().ExecuteAsync<Jularr.Backend.Data.Common.UpdateResult>(new CoverParametersV1(), default));
            Assert.IsFalse(File.Exists(kept));

            // With a size of zero nothing is kept, the image is still passed on.
            using var current = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/catalog/covers");
            var version = (await current.Content.ReadFromJsonAsync<CoverCacheV1>())!.Version;
            using var none = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/catalog/covers", new UpdateCoverCacheDataV1(3, 0, version));
            Assert.AreEqual(HttpStatusCode.OK, none.StatusCode);
            using var passed = await host.SendAsync(HttpMethod.Get, address);
            Assert.AreEqual(HttpStatusCode.OK, passed.StatusCode);
            Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(root, "external"), "*", SearchOption.AllDirectories).Any());
        }

        [TestMethod]
        public void CoverPaths_NamesAndWidths_FollowOneRule()
        {
            Assert.AreEqual("/api/v1/covers/5/unit-9?v=ab", CoverPaths.Url(5, Jularr.Backend.Data.Enums.Media.CoverKind.UnitImage, 9, "ab"));
            Assert.AreEqual("/api/v1/covers/5/poster-500?v=ab12", CoverPaths.Sized("/api/v1/covers/5/poster?v=ab12", 342));
            Assert.AreEqual("/api/v1/covers/external?u=https%3A%2F%2Fimage.tmdb.org%2Fx.jpg&w=500", CoverPaths.Sized("https://image.tmdb.org/x.jpg", 342));
            Assert.AreEqual("https://i.ytimg.com/vi/a/hqdefault.jpg", CoverPaths.Sized("https://i.ytimg.com/vi/a/hqdefault.jpg", 480));
            Assert.AreEqual("/api/v1/covers/5/backdrop-1280?v=ab12", CoverPaths.Sized("/api/v1/covers/5/backdrop?v=ab12", 1280));
            Assert.AreEqual("/api/v1/covers/5/unit-9?v=ab12", CoverPaths.Sized("/api/v1/covers/5/unit-9?v=ab12", 400));
                        Assert.IsFalse(CoverPaths.TryParse("poster-999", out _, out _, out _));
            Assert.IsFalse(CoverPaths.TryParse("../poster", out _, out _, out _));
            Assert.IsTrue(CoverPaths.TryParse("unit-12-250", out var kind, out var unit, out var width));
            Assert.AreEqual((Jularr.Backend.Data.Enums.Media.CoverKind.UnitImage, 12L, 250), (kind, unit, width));
        }

        [TestMethod]
        public void CoverClient_Signatures_AreRecognizedByTheirBytes()
        {
            Assert.AreEqual("image/png", CoverClient.Recognize(FakeImages.Png));
            Assert.AreEqual("image/jpeg", CoverClient.Recognize([.. new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, .. new byte[40]]));
            Assert.IsNull(CoverClient.Recognize(Encoding.ASCII.GetBytes("<html><body>not an image at all, only text here</body></html>")));
        }

        private static async Task RunAsync(Host host, Func<IServiceScope, Task> action)
        {
            await using var scope = host.Services.CreateAsyncScope();
            await action(scope);
        }

        private static async Task<Host> CreateHostAsync(FakeImages images, IReadOnlyDictionary<string, string>? settings = null)
        {
            var tmdb = new FakeTmdb();
            var host = await Host.CreateAsync(library: true, settings: settings, services: services =>
            {
                services.AddHttpClient<TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => tmdb);
                services.AddHttpClient<CoverClient>().ConfigurePrimaryHttpMessageHandler(() => images);
            });
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            var provider = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'tmdb'"))!;
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + provider + "/secret", new SaveProviderSecretDataV1(Token));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            return host;
        }

        // The provider's image server: any address answers with a small PNG, a changed one with other bytes.
        private sealed class FakeImages : HttpMessageHandler
        {
            public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[64]];
            public List<Uri> Requests { get; } = [];
            public bool Changed { get; set; }
            public bool Failing { get; set; }
            public byte[] Payload { get; init; } = Png;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.RequestUri!);
                if (Failing) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                var bytes = Changed ? [.. Payload, 1, 2, 3] : Payload;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } } });
            }
        }

        private sealed class FakeTmdb : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Headers.Authorization?.Parameter != Token) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                var body = request.RequestUri!.AbsolutePath == "/3/movie/603"
                    ? """{"id":603,"title":"The Matrix","original_title":"The Matrix","release_date":"1999-03-31","overview":"A hacker learns the truth.","poster_path":"/poster.png","backdrop_path":"/backdrop.png","external_ids":{"imdb_id":"tt0133093"}}"""
                    : null;
                return Task.FromResult(body is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
        }
    }
}
