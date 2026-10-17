using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Admin.Artwork.V1;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ArtworkSettingsTests
    {
        private const string Endpoint = "/api/v1/admin/artwork/settings";

        [TestMethod]
        public async Task OwnerCanConfigureNasAndCacheIndependently_RejectsStaleEdits()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(folder.Path);

            using var response = await host.SendAsync(HttpMethod.Get, Endpoint);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var original = (await response.Content.ReadFromJsonAsync<ArtworkSettingsDetailsV1>())!;
            Assert.AreEqual(NasArtworkMode.Compatible, original.NasModeId);
            Assert.AreEqual(ArtworkCacheFormat.Webp, original.CacheFormatId);
            Assert.IsTrue(original.ProgressiveJxl);
            Assert.IsTrue(original.ExportToNas);

            var update = new UpdateArtworkSettingsDataV1(NasArtworkMode.JxlAndCompatible,
                ArtworkCacheFormat.Jxl, ArtworkQuality.VisuallyLossless, progressiveJxl: true,
                exportToNas: true, cacheLimitBytes: 512L * 1024 * 1024, version: original.Version);
            using var saved = await host.SendAsync(HttpMethod.Put, Endpoint, update);
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());

            using var changedResponse = await host.SendAsync(HttpMethod.Get, Endpoint);
            var changed = (await changedResponse.Content.ReadFromJsonAsync<ArtworkSettingsDetailsV1>())!;
            Assert.AreEqual(NasArtworkMode.JxlAndCompatible, changed.NasModeId);
            Assert.AreEqual(ArtworkCacheFormat.Jxl, changed.CacheFormatId);
            Assert.AreEqual(ArtworkQuality.VisuallyLossless, changed.CacheQualityId);
            Assert.IsTrue(changed.ProgressiveJxl);
            Assert.AreEqual(512L * 1024 * 1024, changed.CacheLimitBytes);
            Assert.AreEqual(original.Version + 1, changed.Version);

            using var stale = await host.SendAsync(HttpMethod.Put, Endpoint, update);
            Assert.AreNotEqual(HttpStatusCode.OK, stale.StatusCode);
            Assert.AreEqual(changed.Version, await host.ScalarAsync("SELECT \"Version\" FROM \"ArtworkSettings\""));
        }

        [TestMethod]
        public async Task OwnerCanDisableNasWrites_WithoutDisablingImageCaching()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(folder.Path);
            var version = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"ArtworkSettings\""))!;

            using var saved = await host.SendAsync(HttpMethod.Put, Endpoint,
                new UpdateArtworkSettingsDataV1(NasArtworkMode.JxlOnly, ArtworkCacheFormat.Webp,
                    ArtworkQuality.High, progressiveJxl: false, exportToNas: false, cacheLimitBytes: 67108864, version));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"ExportToNas\" FROM \"ArtworkSettings\""));
            Assert.AreEqual((short)3, await host.ScalarAsync("SELECT \"NasModeId\" FROM \"ArtworkSettings\""));
            Assert.AreEqual((short)1, await host.ScalarAsync("SELECT \"CacheFormatId\" FROM \"ArtworkSettings\""));
            Assert.AreEqual(67108864L, await host.ScalarAsync("SELECT \"CacheLimitBytes\" FROM \"ArtworkSettings\""));
        }
    }
}
