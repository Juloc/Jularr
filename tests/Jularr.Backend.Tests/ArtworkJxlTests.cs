using System.Net;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Core;
using SkiaSharp;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ArtworkJxlTests
    {
        [TestMethod]
        public void JxlEncoder_ChoosesBoundedProgressiveQualityProfiles()
        {
            var args = JxlArtworkCodec.EncoderOptions(ArtworkQuality.VisuallyLossless, progressive: true);
            CollectionAssert.Contains(args, "--distance=0.5");
            CollectionAssert.Contains(args, "--progressive");
            CollectionAssert.Contains(args, "--num_threads=2");
            CollectionAssert.Contains(JxlArtworkCodec.EncoderOptions(ArtworkQuality.Lossless, false), "--distance=0");
            CollectionAssert.DoesNotContain(JxlArtworkCodec.EncoderOptions(ArtworkQuality.High, false), "--progressive");
            CollectionAssert.Contains(JxlArtworkCodec.JpegEncoderOptions(ArtworkQuality.VisuallyLossless, true), "--lossless_jpeg=0");
            CollectionAssert.Contains(JxlArtworkCodec.JpegEncoderOptions(ArtworkQuality.VisuallyLossless, true), "--progressive");
        }

        [TestMethod]
        public async Task MissingEncoder_DoesNotPreventWebpFallback()
        {
            var codec = new JxlArtworkCodec("jularr-unavailable-cjxl-" + Guid.NewGuid().ToString("N"));
            using var surface = SKSurface.Create(new SKImageInfo(160, 240));
            surface.Canvas.Clear(SKColors.Teal);
            using var image = surface.Snapshot();
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            Assert.IsNull(await codec.EncodeAsync(png.ToArray(), ArtworkQuality.High, true, CancellationToken.None));
        }

        [TestMethod]
        public async Task JxlPreferredWithoutEncoder_StillServesLastKnownGoodWebp()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var mount = await LibraryTests.CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mount.ToString(), "Movies", "JXL Preferred");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Film.mkv"), "video");
            using (var surface = SKSurface.Create(new SKImageInfo(160, 240)))
            {
                surface.Canvas.Clear(SKColors.Navy);
                using var bitmap = surface.Snapshot();
                using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                await File.WriteAllBytesAsync(Path.Combine(folder, "poster.png"), png.ToArray());
            }

            var root = await LibraryTests.CreateRootAsync(host, mount, "Movies", MediaType.Movie);
            await host.Fixture.RunSqlAsync("UPDATE \"ArtworkSettings\" SET \"CacheFormatId\" = 2, \"ProgressiveJxl\" = TRUE");
            await LibraryTests.ScanAsync(host, root);
            var file = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\""))!;
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + file + "/assignment",
                new AssignLibraryFileDataV1(null, "JXL Preferred", null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode);
            await LibraryTests.ScanAsync(host, root);

            var work = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\" WHERE \"CanonicalTitle\" = 'JXL Preferred'"))!;
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ArtworkRenditions\" WHERE \"FormatId\" = 3"));
            using var fallback = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + work + "/artwork/1");
            Assert.AreEqual(HttpStatusCode.OK, fallback.StatusCode);
            Assert.AreEqual("image/webp", fallback.Content.Headers.ContentType?.MediaType);
            var bytes = await fallback.Content.ReadAsByteArrayAsync();
            Assert.IsTrue(bytes.Length > 16);
        }
    }
}
