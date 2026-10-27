using Jularr.Backend.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
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
            using var requested = await RequestMatrixAsync(host);
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
        public async Task Cover_WithFfmpeg_KeepsOneWebPWidthBesideTheOriginal_AndConvertsOlderFormats()
        {
            // Opt-in: JULARR_TEST_FFMPEG names an ffmpeg executable.
            var ffmpeg = Environment.GetEnvironmentVariable("JULARR_TEST_FFMPEG");
            if (string.IsNullOrEmpty(ffmpeg))
            {
                Assert.Inconclusive("JULARR_TEST_FFMPEG is not configured.");
                return;
            }

            var images = new FakeImages { Payload = Convert.FromBase64String(OnePixelPng) };
            await using var host = await CreateHostAsync(images, new Dictionary<string, string> { ["Media:FfmpegPath"] = ffmpeg, ["Media:CjxlPath"] = "" });
            var root = host.Services.GetRequiredService<IOptions<MediaOptions>>().Value.CoversPath;
            using var requested = await RequestMatrixAsync(host);
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\""))!;
            var folder = Path.Combine(root, workId.ToString());

            // Without cjxl the one format is WebP: no JPEG XL and no JPEG beside it.
            foreach (var name in new[] { "poster.png", "poster-250.webp", "poster-500.webp", "backdrop-500.webp", "backdrop-1280.webp" })
            {
                Assert.IsTrue(File.Exists(Path.Combine(folder, name)), name);
            }

            Assert.AreEqual(0, Directory.EnumerateFiles(folder, "*.jxl").Count() + Directory.EnumerateFiles(folder, "*.jpg").Count());
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WorkCovers\" WHERE \"FormatId\" = 2"));

            // A browser that names WebP is given the stored file; one that does not gets a JPEG made for that answer, kept nowhere.
            var poster = (string)(await host.ScalarAsync("SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"FactKey\" = 'Poster'"))!;
            var narrowAddress = poster.Replace("poster?", "poster-250?", StringComparison.Ordinal);
            using var modern = await GetAsync(host, narrowAddress, "image/avif,image/webp,*/*");
            Assert.AreEqual("image/webp", modern.Content.Headers.ContentType!.MediaType);
            Assert.AreEqual("Accept", modern.Headers.Vary.Single());
            using var plain = await host.SendAsync(HttpMethod.Get, narrowAddress);
            Assert.AreEqual("image/jpeg", plain.Content.Headers.ContentType!.MediaType);
            Assert.IsTrue(CoverPaths.IsFormat(await plain.Content.ReadAsByteArrayAsync(), "jpg"));
            Assert.AreNotEqual(modern.Headers.ETag, plain.Headers.ETag);
            Assert.AreEqual(0, Directory.EnumerateFiles(folder, "*.jpg").Count());

            // The backfill makes a width kept in an older format into the chosen one: the JPEG leaves once the WebP is written and checked.
            var poster250 = Path.Combine(folder, "poster-250.webp");
            File.Delete(poster250);
            await File.WriteAllBytesAsync(Path.Combine(folder, "poster-250.jpg"), [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
            await host.ScalarAsync("UPDATE \"WorkCovers\" SET \"FormatId\" = NULL WHERE \"CoverKindId\" = 1");
            await RunAsync(host, scope => scope.ServiceProvider.GetRequiredService<ConvertCoversV1>().ExecuteAsync<Jularr.Backend.Data.Common.UpdateResult>(new CoverParametersV1(), default));
            Assert.IsTrue(CoverPaths.IsFormat(await File.ReadAllBytesAsync(poster250), "webp"));
            Assert.IsFalse(File.Exists(Path.Combine(folder, "poster-250.jpg")));
            Assert.IsTrue(File.Exists(Path.Combine(folder, "poster.png")));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WorkCovers\" WHERE \"FormatId\" = 2"));
        }

        [TestMethod]
        public async Task Cover_JxlWidth_IsServedOnlyToBrowsersThatNameIt()
        {
            // No cjxl needed: the file is put in place, the answer is chosen by the Accept header alone.
            var images = new FakeImages();
            await using var host = await CreateHostAsync(images);
            var root = host.Services.GetRequiredService<IOptions<MediaOptions>>().Value.CoversPath;
            using var requested = await RequestMatrixAsync(host);
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\""))!;
            var poster = (string)(await host.ScalarAsync("SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"FactKey\" = 'Poster'"))!;
            var narrow = poster.Replace("poster?", "poster-250?", StringComparison.Ordinal);
            var jxl = new byte[] { 0xFF, 0x0A, 1, 2, 3 };
            await File.WriteAllBytesAsync(Path.Combine(root, workId.ToString(), "poster-250.jxl"), jxl);

            async Task<HttpResponseMessage> GetAsync(string? accept)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, narrow);
                if (accept is not null)
                {
                    request.Headers.Accept.ParseAdd(accept);
                }

                request.Headers.Add("Cookie", string.Join("; ", host.Cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
                return await host.Client.SendAsync(request);
            }

            using var modern = await GetAsync("image/jxl,image/avif,image/webp,*/*");
            Assert.AreEqual("image/jxl", modern.Content.Headers.ContentType!.MediaType);
            CollectionAssert.AreEqual(jxl, await modern.Content.ReadAsByteArrayAsync());
            Assert.AreEqual("Accept", modern.Headers.Vary.Single());
            using var wildcard = await GetAsync("*/*");
            Assert.AreEqual("image/png", wildcard.Content.Headers.ContentType!.MediaType);
            using var refused = await GetAsync("image/jxl;q=0,image/webp");
            Assert.AreEqual("image/png", refused.Content.Headers.ContentType!.MediaType);
            Assert.AreNotEqual(modern.Headers.ETag, wildcard.Headers.ETag);
        }

        [TestMethod]
        public async Task Cover_WithFfmpegAndCjxl_KeepsOnlyJxlWidths_AndAnswersOtherBrowsersWithWebPOrJpeg()
        {
            // Opt-in: JULARR_TEST_FFMPEG and JULARR_TEST_CJXL name the executables; CI and the image have them.
            var ffmpeg = Environment.GetEnvironmentVariable("JULARR_TEST_FFMPEG");
            var cjxl = Environment.GetEnvironmentVariable("JULARR_TEST_CJXL");
            if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(cjxl))
            {
                Assert.Inconclusive("JULARR_TEST_FFMPEG and JULARR_TEST_CJXL are not both configured.");
                return;
            }

            var images = new FakeImages { Payload = Convert.FromBase64String(OnePixelPng) };
            await using var host = await CreateHostAsync(images, new Dictionary<string, string> { ["Media:FfmpegPath"] = ffmpeg, ["Media:CjxlPath"] = cjxl });
            var root = host.Services.GetRequiredService<IOptions<MediaOptions>>().Value.CoversPath;
            using var requested = await RequestMatrixAsync(host);
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\""))!;
            var folder = Path.Combine(root, workId.ToString());
            foreach (var name in new[] { "poster-250.jxl", "poster-500.jxl", "backdrop-500.jxl", "backdrop-1280.jxl" })
            {
                Assert.IsTrue(File.Exists(Path.Combine(folder, name)), name);
            }

            Assert.AreEqual(0, Directory.EnumerateFiles(folder, "*.webp").Count() + Directory.EnumerateFiles(folder, "*.jpg").Count());
            Assert.IsFalse(Directory.EnumerateFiles(folder, "*.partial*").Any());
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WorkCovers\" WHERE \"FormatId\" = 1"));
            var poster = (string)(await host.ScalarAsync("SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"FactKey\" = 'Poster'"))!;
            var narrowAddress = poster.Replace("poster?", "poster-250?", StringComparison.Ordinal);
            using var served = await GetAsync(host, narrowAddress, "image/jxl,image/webp,*/*");
            Assert.AreEqual("image/jxl", served.Content.Headers.ContentType!.MediaType);
            Assert.IsTrue(IsJxl(await served.Content.ReadAsByteArrayAsync()));

            // A browser without JPEG XL is answered by conversion on the fly, WebP when it names it, else JPEG; nothing of it is kept.
            using var webp = await GetAsync(host, narrowAddress, "image/webp,*/*");
            Assert.AreEqual("image/webp", webp.Content.Headers.ContentType!.MediaType);
            using var jpeg = await GetAsync(host, narrowAddress, null);
            Assert.AreEqual("image/jpeg", jpeg.Content.Headers.ContentType!.MediaType);
            Assert.AreEqual(3, new[] { served, webp, jpeg }.Select(response => response.Headers.ETag!.Tag).Distinct().Count());
            Assert.AreEqual(0, Directory.EnumerateFiles(folder, "*.webp").Count() + Directory.EnumerateFiles(folder, "*.jpg").Count());

            // Choosing WebP makes the backfill re-encode the JPEG XL widths and remove them once the WebP files are written and checked.
            using var current = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/catalog/covers");
            var settings = (await current.Content.ReadFromJsonAsync<CoverCacheV1>())!;
            Assert.AreEqual((CoverFormat.Jxl, CoverFormat.Jxl), (settings.FormatId, settings.ActiveFormatId));
            using var chosen = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/catalog/covers", new UpdateCoverCacheDataV1(settings.KeepDays, settings.MaximumMegabytes, CoverFormat.WebP, settings.Version));
            Assert.AreEqual(HttpStatusCode.OK, chosen.StatusCode, await chosen.Content.ReadAsStringAsync());
            for (var pass = 0; pass < 4; pass++)
            {
                await RunAsync(host, scope => scope.ServiceProvider.GetRequiredService<ConvertCoversV1>().ExecuteAsync<Jularr.Backend.Data.Common.UpdateResult>(new CoverParametersV1(), default));
            }

            foreach (var name in new[] { "poster-250.webp", "poster-500.webp", "backdrop-500.webp", "backdrop-1280.webp" })
            {
                Assert.IsTrue(File.Exists(Path.Combine(folder, name)), name);
            }

            Assert.AreEqual(0, Directory.EnumerateFiles(folder, "*.jxl").Count());
            Assert.IsTrue(File.Exists(Path.Combine(folder, "poster.png")));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WorkCovers\" WHERE \"FormatId\" = 2"));
        }

        private static async Task<HttpResponseMessage> GetAsync(Host host, string address, string? accept)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            if (accept is not null)
            {
                request.Headers.Accept.ParseAdd(accept);
            }

            request.Headers.Add("Cookie", string.Join("; ", host.Cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
            return await host.Client.SendAsync(request);
        }

        [TestMethod]
        public void CoverPaths_Accepts_NamesOnlyTheFormatsTheBrowserAsksFor()
        {
            Assert.AreEqual(CoverFormats.None, CoverPaths.Accepts(null));
            Assert.AreEqual(CoverFormats.None, CoverPaths.Accepts("*/*"));
            Assert.AreEqual(CoverFormats.None, CoverPaths.Accepts("image/*,text/html"));
            Assert.AreEqual(CoverFormats.WebP, CoverPaths.Accepts("image/avif,image/webp,image/apng,*/*;q=0.8"));
            Assert.AreEqual(CoverFormats.Jxl, CoverPaths.Accepts("image/jxl"));
            Assert.AreEqual(CoverFormats.Jxl | CoverFormats.WebP, CoverPaths.Accepts("IMAGE/JXL, image/webp;q=0.5"));
            Assert.AreEqual(CoverFormats.WebP, CoverPaths.Accepts("image/jxl;q=0,image/webp"));
            Assert.AreEqual(CoverFormats.None, CoverPaths.Accepts("image/jxlx,image/webp2"));
        }

        [TestMethod]
        public async Task JxlEncoder_Arguments_AreOneListWithDistanceEffortAndProgressive_AndAMissingToolSwitchesItOff()
        {
            CollectionAssert.AreEqual(new[] { "in.png", "out.jxl", "-d", "1.0", "-e", "7", "--progressive", "--num_threads=1", "--quiet" }, JxlEncoder.BuildArguments("in.png", "out.jxl", true).ToArray());
            CollectionAssert.AreEqual(new[] { "in.png", "out.jxl", "-d", "1.0", "-e", "7", "--num_threads=1", "--quiet" }, JxlEncoder.BuildArguments("in.png", "out.jxl", false).ToArray());

            var folder = Directory.CreateTempSubdirectory("jularr-jxl").FullName;
            try
            {
                Assert.IsFalse(new JxlEncoder().IsAvailable);
                var missing = new JxlEncoder { ExecutablePath = Path.Combine(folder, "no-such-cjxl") };
                Assert.IsTrue(missing.IsAvailable);
                Assert.IsFalse(await missing.DetectAsync(default));
                Assert.IsFalse(missing.IsAvailable);
                Assert.IsFalse(await missing.EncodeAsync(Path.Combine(folder, "in.png"), Path.Combine(folder, "out.jxl"), default));
                Assert.IsFalse(File.Exists(Path.Combine(folder, "out.jxl")));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [TestMethod]
        public async Task JxlEncoder_WithCjxl_WritesAJxlFile()
        {
            // Opt-in: JULARR_TEST_CJXL names a cjxl executable.
            var cjxl = Environment.GetEnvironmentVariable("JULARR_TEST_CJXL");
            if (string.IsNullOrEmpty(cjxl))
            {
                Assert.Inconclusive("JULARR_TEST_CJXL is not configured.");
                return;
            }

            var folder = Directory.CreateTempSubdirectory("jularr-jxl").FullName;
            try
            {
                var encoder = new JxlEncoder { ExecutablePath = cjxl };
                Assert.IsTrue(await encoder.DetectAsync(default));
                StringAssert.Contains(encoder.Version!, "cjxl");
                var input = Path.Combine(folder, "in.png");
                var output = Path.Combine(folder, "out.jxl");
                await File.WriteAllBytesAsync(input, Convert.FromBase64String(OnePixelPng));
                Assert.IsTrue(await encoder.EncodeAsync(input, output, default));
                Assert.IsTrue(IsJxl(await File.ReadAllBytesAsync(output)));

                // A file that is no picture leaves nothing behind.
                var broken = Path.Combine(folder, "broken.png");
                await File.WriteAllTextAsync(broken, "not a picture");
                Assert.IsFalse(await encoder.EncodeAsync(broken, Path.Combine(folder, "broken.jxl"), default));
                Assert.IsFalse(File.Exists(Path.Combine(folder, "broken.jxl")));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

        // The bare codestream starts FF 0A; the ISO container with 00 00 00 0C 'JXL '.
        private static bool IsJxl(byte[] bytes) =>
            bytes is [0xFF, 0x0A, ..] || bytes.Length > 12 && bytes[4..8].SequenceEqual("JXL "u8.ToArray());

        [TestMethod]
        public async Task Cover_ChangedAddress_IsFetchedAgain_AndAFailureKeepsTheProviderAddress()
        {
            var images = new FakeImages();
            await using var host = await CreateHostAsync(images);
            using var requested = await RequestMatrixAsync(host);
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
            using var requested = await RequestMatrixAsync(host);
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
            Assert.AreEqual(TimeSpan.FromDays(7), first.Headers.CacheControl!.MaxAge);
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
            Assert.AreEqual((14, 500), (settings.KeepDays, settings.MaximumMegabytes));
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/catalog/covers", new UpdateCoverCacheDataV1(3, 50, CoverFormat.Jxl, settings.Version));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var stale = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/catalog/covers", new UpdateCoverCacheDataV1(3, 50, CoverFormat.Jxl, settings.Version));
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
            using var none = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/catalog/covers", new UpdateCoverCacheDataV1(3, 0, CoverFormat.Jxl, version));
            Assert.AreEqual(HttpStatusCode.OK, none.StatusCode);
            using var passed = await host.SendAsync(HttpMethod.Get, address);
            Assert.AreEqual(HttpStatusCode.OK, passed.StatusCode);
            Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(root, "external"), "*", SearchOption.AllDirectories).Any());
        }

        [TestMethod]
        public void CoverPaths_StoredFormat_IsServedToBrowsersThatTakeIt_AndDerivedForTheOthers()
        {
            // One stored file answers when its format is taken; the older triple still resolves to the best taken one.
            Assert.AreEqual("jxl", CoverPaths.Serve(["jxl"], CoverFormats.Jxl));
            Assert.AreEqual("jxl", CoverPaths.Serve(["jpg", "webp", "jxl"], CoverFormats.Jxl | CoverFormats.WebP));
            Assert.AreEqual("webp", CoverPaths.Serve(["webp"], CoverFormats.WebP));
            Assert.AreEqual("jpg", CoverPaths.Serve(["webp", "jpg"], CoverFormats.Jxl));
            Assert.IsNull(CoverPaths.Serve(["jxl"], CoverFormats.None));
            Assert.IsNull(CoverPaths.Serve(["webp"], CoverFormats.Jxl));
            Assert.IsNull(CoverPaths.Serve([], CoverFormats.Jxl | CoverFormats.WebP));

            // What a browser that takes none of them gets: WebP when it names it, else JPEG.
            Assert.AreEqual("webp", CoverPaths.Derive(CoverFormats.WebP));
            Assert.AreEqual("webp", CoverPaths.Derive(CoverFormats.Jxl | CoverFormats.WebP));
            Assert.AreEqual("jpg", CoverPaths.Derive(CoverFormats.Jxl));
            Assert.AreEqual("jpg", CoverPaths.Derive(CoverFormats.None));

            // A width is made in the chosen format, then in what the tools can still make, never in a better one.
            CollectionAssert.AreEqual(new[] { "jxl", "webp", "jpg" }, CoverPaths.Extensions(CoverFormat.Jxl).ToArray());
            CollectionAssert.AreEqual(new[] { "webp", "jpg" }, CoverPaths.Extensions(CoverFormat.WebP).ToArray());

            // A written file counts only when its first bytes name its format.
            Assert.IsTrue(CoverPaths.IsFormat([0xFF, 0x0A, 1, 2], "jxl"));
            Assert.IsTrue(CoverPaths.IsFormat([0, 0, 0, 12, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D], "jxl"));
            Assert.IsTrue(CoverPaths.IsFormat("RIFF\0\0\0\0WEBPVP8 "u8, "webp"));
            Assert.IsTrue(CoverPaths.IsFormat([0xFF, 0xD8, 0xFF, 0xE0], "jpg"));
            Assert.IsFalse(CoverPaths.IsFormat([0xFF, 0xD8, 0xFF, 0xE0], "webp"));
            Assert.IsFalse(CoverPaths.IsFormat("RIFF\0\0\0\0WAVEfmt "u8, "webp"));
            Assert.IsFalse(CoverPaths.IsFormat([], "jxl"));
        }

        [TestMethod]
        public void CoverStore_EffectiveFormat_IsJxlOnlyWhereCjxlIsThere_ElseWebP()
        {
            CoverStore Store(JxlEncoder encoder) => new(Options.Create(new MediaOptions()), null!, new MediaTranscoder(), encoder, Microsoft.Extensions.Logging.Abstractions.NullLogger<CoverStore>.Instance);
            Assert.AreEqual(CoverFormat.WebP, Store(new JxlEncoder()).EffectiveFormat(CoverFormat.Jxl));
            Assert.AreEqual(CoverFormat.Jxl, Store(new JxlEncoder { ExecutablePath = "cjxl" }).EffectiveFormat(CoverFormat.Jxl));
            Assert.AreEqual(CoverFormat.WebP, Store(new JxlEncoder { ExecutablePath = "cjxl" }).EffectiveFormat(CoverFormat.WebP));
            Assert.IsFalse(Store(new JxlEncoder()).CanScale);
        }

        [TestMethod]
        public async Task CoverStore_WithFfmpeg_ConvertsAWidthToWebP_ServesItAndDerivesForOtherBrowsers()
        {
            // Opt-in: JULARR_TEST_FFMPEG names an ffmpeg executable; no database needed.
            var ffmpeg = Environment.GetEnvironmentVariable("JULARR_TEST_FFMPEG");
            if (string.IsNullOrEmpty(ffmpeg))
            {
                Assert.Inconclusive("JULARR_TEST_FFMPEG is not configured.");
                return;
            }

            var root = Directory.CreateTempSubdirectory("jularr-covers").FullName;
            try
            {
                var store = new CoverStore(Options.Create(new MediaOptions { CoversPath = root }), null!, new MediaTranscoder { ExecutablePath = ffmpeg }, new JxlEncoder(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<CoverStore>.Instance);
                var folder = Directory.CreateDirectory(Path.Combine(root, "7")).FullName;
                await File.WriteAllBytesAsync(Path.Combine(folder, "poster.png"), Convert.FromBase64String(OnePixelPng));
                await File.WriteAllBytesAsync(Path.Combine(folder, "poster-250.jpg"), [0xFF, 0xD8, 0xFF, 0xE0, 1]);
                await File.WriteAllBytesAsync(Path.Combine(folder, "poster-500.jpg"), [0xFF, 0xD8, 0xFF, 0xE0, 1]);

                // Both widths are made in WebP from the download, then the JPEGs leave; the download stays.
                var cover = new WorkCoverConvertData(7, CoverKind.Poster, null, "image/png", "abc");
                Assert.IsTrue(await store.ConvertAsync(cover, store.EffectiveFormat(CoverFormat.Jxl), default));
                foreach (var width in new[] { "250", "500" })
                {
                    Assert.IsTrue(CoverPaths.IsFormat(await File.ReadAllBytesAsync(Path.Combine(folder, "poster-" + width + ".webp")), "webp"));
                    Assert.IsFalse(File.Exists(Path.Combine(folder, "poster-" + width + ".jpg")));
                }

                Assert.IsTrue(File.Exists(Path.Combine(folder, "poster.png")));
                Assert.IsFalse(Directory.EnumerateFiles(folder, "*.partial*").Any());

                // Converting again changes nothing; a browser that names WebP gets the file, any other a JPEG made for it and kept nowhere.
                Assert.IsTrue(await store.ConvertAsync(cover, CoverFormat.WebP, default));
                var webp = await store.FindAsync(7, CoverKind.Poster, null, 250, "image/png", CoverFormats.WebP, default);
                Assert.AreEqual("image/webp", webp!.ContentType);
                Assert.IsNotNull(webp.Path);
                var plain = await store.FindAsync(7, CoverKind.Poster, null, 250, "image/png", CoverFormats.None, default);
                Assert.AreEqual("image/jpeg", plain!.ContentType);
                Assert.IsNull(plain.Path);
                Assert.IsTrue(CoverPaths.IsFormat(plain.Bytes!, "jpg"));
                Assert.AreEqual(0, Directory.EnumerateFiles(folder, "*.jpg").Count());
                var original = await store.FindAsync(7, CoverKind.Poster, null, null, "image/png", CoverFormats.None, default);
                Assert.AreEqual("image/png", original!.ContentType);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public async Task CoverBackfill_ConvertsOnlyWhatIsNotInTheChosenFormat_AndNeverRemovesBeforeTheNewFileExists()
        {
            // Tools that cannot run: the files are fakes, the job's decisions are what is checked. Without a cjxl that runs, the one format the widths are kept in is WebP.
            var missing = Path.Combine(Path.GetTempPath(), "jularr-no-tool-" + Guid.NewGuid().ToString("N"));
            await using var host = await CreateHostAsync(new FakeImages(), new Dictionary<string, string> { ["Media:FfmpegPath"] = missing, ["Media:CjxlPath"] = missing });
            var root = host.Services.GetRequiredService<IOptions<MediaOptions>>().Value.CoversPath;
            using var requested = await RequestMatrixAsync(host);
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\""))!;
            var folder = Path.Combine(root, workId.ToString());

            // New covers are recorded in the format they were made for, even when no tool could make the widths.
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WorkCovers\" WHERE \"FormatId\" = 2"));

            // Covers from before: the triple of formats, no recorded format.
            foreach (var name in new[] { "poster-250.webp", "poster-250.jpg", "poster-500.webp", "poster-500.jpg", "poster-250.jxl", "poster-500.jxl" })
            {
                await File.WriteAllBytesAsync(Path.Combine(folder, name), [1, 2, 3]);
            }

            await host.ScalarAsync("UPDATE \"WorkCovers\" SET \"FormatId\" = NULL WHERE \"CoverKindId\" = 1");
            Assert.IsFalse((await ConvertAsync(host)).Success, "One pass takes the whole cover and nothing else is waiting.");

            // The poster's widths are already WebP: the other formats of them leave, the download stays, the cover is recorded.
            foreach (var name in new[] { "poster-250.webp", "poster-500.webp", "poster.png" })
            {
                Assert.IsTrue(File.Exists(Path.Combine(folder, name)), name);
            }

            foreach (var name in new[] { "poster-250.jxl", "poster-250.jpg", "poster-500.jxl", "poster-500.jpg" })
            {
                Assert.IsFalse(File.Exists(Path.Combine(folder, name)), name);
            }

            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"WorkCovers\" WHERE \"FormatId\" IS NULL"));

            // A width in JPEG XL only, WebP chosen, and no tool that can make it: nothing is removed, the cover is recorded and not tried again until the choice changes.
            File.Delete(Path.Combine(folder, "poster-250.webp"));
            await File.WriteAllBytesAsync(Path.Combine(folder, "poster-250.jxl"), [1, 2, 3]);
            await host.ScalarAsync("UPDATE \"WorkCovers\" SET \"FormatId\" = 1 WHERE \"CoverKindId\" = 1");
            await ConvertAsync(host);
            Assert.IsTrue(File.Exists(Path.Combine(folder, "poster-250.jxl")), "The old width stays when the new one could not be written.");
            Assert.IsTrue(File.Exists(Path.Combine(folder, "poster.png")));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WorkCovers\" WHERE \"FormatId\" = 2"));
            await ConvertAsync(host);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WorkCovers\" WHERE \"FormatId\" = 2"));

            // The sweep knows every extension a width may have and still removes what no cover refers to.
            var stale = Path.Combine(folder, "unit-77-250.jxl");
            await File.WriteAllBytesAsync(stale, [1]);
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-1));
            File.SetLastWriteTimeUtc(Path.Combine(folder, "poster-250.jxl"), DateTime.UtcNow.AddDays(-1));
            await RunAsync(host, scope => scope.ServiceProvider.GetRequiredService<CleanCoversV1>().ExecuteAsync<Jularr.Backend.Data.Common.UpdateResult>(new CoverParametersV1(), default));
            Assert.IsFalse(File.Exists(stale));
            Assert.IsTrue(File.Exists(Path.Combine(folder, "poster-250.jxl")));
        }

        [TestMethod]
        public async Task CoverBackfill_WithoutFfmpeg_ChangesNothing_AndIsAnOwnerVisibleJob()
        {
            await using var host = await CreateHostAsync(new FakeImages(), new Dictionary<string, string> { ["Media:FfmpegPath"] = "" });
            using var requested = await RequestMatrixAsync(host);
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            await host.ScalarAsync("UPDATE \"WorkCovers\" SET \"FormatId\" = NULL");
            Assert.IsFalse((await ConvertAsync(host)).Success);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WorkCovers\" WHERE \"FormatId\" IS NULL"));

            // The job is listed and may be run on request, paused while Jularr is used, like the other heavy ones.
            var job = WorkerJobs.Definition("coverBackfill");
            Assert.IsTrue(job.CanRunNow && job.CanPauseWhileUsed && job.PausesWhileUsedByDefault);
        }

        private static async Task<Jularr.Backend.Data.Common.UpdateResult> ConvertAsync(Host host)
        {
            await using var scope = host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ConvertCoversV1>().ExecuteAsync<Jularr.Backend.Data.Common.UpdateResult>(new CoverParametersV1(), default);
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

        // The catalog title is a Work already; the normal request imports its details (poster, backdrop) into it.
        private static async Task<HttpResponseMessage> RequestMatrixAsync(Host host)
        {
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle", "ReleaseYear", "CatalogNamespace") VALUES (1, 'The Matrix', 1999, 'tmdb_movie');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") SELECT "Id", 'tmdb_movie', '603' FROM "Works";
                """);
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\""))!;
            return await host.SendAsync(HttpMethod.Post, $"/api/v1/works/{workId}/requests", new CreateRequestDataV1(1));
        }

        private static async Task<Host> CreateHostAsync(FakeImages images,IReadOnlyDictionary<string, string>? settings = null)
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
