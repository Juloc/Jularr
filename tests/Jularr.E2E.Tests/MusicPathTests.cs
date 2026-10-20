using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // An album from MusicBrainz through request, download and import to playing a track and continuing it after signing in anew.
    [TestClass]
    public sealed class MusicPathTests
    {
        [TestMethod]
        [Timeout(600_000)]
        public async Task Album_FromMetadataToRequestDownloadImportPlayAndResume()
        {
            var ffmpeg = Environment.GetEnvironmentVariable("JULARR_TEST_FFMPEG");
            using var folders = new Folders("Music");
            var tracks = await Steps.WriteAlbumAsync(ffmpeg, Path.Combine(folders.Fixtures, "album"), "Opening", "Middle Song", "Closing");
            var album = new FakeRelease("Example Artist - Example Album (2019) [FLAC]", "flac", 3040, tracks);
            var otherArtist = new FakeRelease("Other Artist - Example Album (2019) [FLAC]", "other", 3040, tracks);
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
                ["Providers:MusicBrainz"] = fakes.MusicBrainz.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library,
                ["Media:FfmpegPath"] = ffmpeg ?? "",
                ["Media:FfprobePath"] = Steps.FfprobeBeside(ffmpeg) ?? ""
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Music", "Music"));
                await Steps.AddUsenetAsync(page, fakes, "3000,3040");

                // The album is found on Discover in MusicBrainz and requested there.
                await Steps.RequestFromCatalogAsync(page, "music", FakeServices.AlbumTitle, "musicbrainz");

                // Only the requested artist's release is eligible.
                fakes.Offer(album, otherArtist);
                await Steps.OpenSearchAsync(page, FakeServices.AlbumTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                await Assertions.Expect(page.Locator("tr", new() { HasText = otherArtist.Title })).ToContainTextAsync("Not the wanted artist");
                await Assertions.Expect(page.Locator("tr", new() { HasText = album.Title })).ToContainTextAsync("Eligible");
                await Steps.GrabAsync(page, album);
                CollectionAssert.AreEqual(new[] { fakes.NzbAddress(album).ToString() }, fakes.Grabbed.ToArray());

                // All three tracks land in the music library.
                var details = await Steps.WaitForAvailableAsync(page, FakeServices.AlbumTitle);
                Assert.AreEqual(3, Directory.EnumerateFiles(Path.Combine(folders.Library, "Music"), "*.flac", SearchOption.AllDirectories).Count(), "The album was not imported once.");

                // Playing needs real files; FFmpeg makes them.
                if (ffmpeg is null)
                {
                    JularrInstance.Unavailable("JULARR_TEST_FFMPEG is not configured; playback was not checked.");
                }

                await page.GotoAsync(details);
                await Steps.PlayAndPauseAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).First);
                await Steps.SignOutAndInAsync(page);
                await page.GotoAsync(details);
                var resumed = await Steps.ResumeAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Continue at" }).First);
                Assert.IsTrue(resumed >= 4.5, "The track started at " + resumed + " s instead of continuing.");

                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "music-path");
                throw;
            }
        }
    }
}
