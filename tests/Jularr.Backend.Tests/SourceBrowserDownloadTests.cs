using Jularr.Integrations.Data.Browser;
using Jularr.Integrations.Logic.Browser;

namespace Jularr.Backend.Tests
{
    // How long the one source browser waits for a file: a download that never begins or stops growing ends early instead of holding the browser
    // (and every download queued behind it) for the whole limit, and a growing one reports its bytes.
    [TestClass]
    public sealed class SourceBrowserDownloadTests
    {
        private static readonly TimeSpan Total = TimeSpan.FromSeconds(30);

        [TestMethod]
        public async Task ADownloadThatNeverBegins_EndsAfterTheStartLimit_NotTheTotal()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var failure = await Assert.ThrowsExactlyAsync<SourceBrowserException>(() =>
                SourceBrowser.WaitForDownloadAsync(folder.Path, TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(10), Total, null, TimeProvider.System, default));
            StringAssert.Contains(failure.Message, "did not start");
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        }

        [TestMethod]
        public async Task ADownloadThatStopsGrowing_EndsAfterTheIdleLimit_AndReportedItsBytes()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var partial = Path.Combine(folder.Path, "book.epub.crdownload");
            await File.WriteAllBytesAsync(partial, new byte[100]);
            var reported = new List<long>();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var failure = await Assert.ThrowsExactlyAsync<SourceBrowserException>(() =>
                SourceBrowser.WaitForDownloadAsync(folder.Path, TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(1500), Total, (done, _) =>
                {
                    reported.Add(done);
                    return Task.CompletedTask;
                }, TimeProvider.System, default));
            StringAssert.Contains(failure.Message, "stopped");
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
            CollectionAssert.AreEqual(new[] { 100L }, reported);
        }

        [TestMethod]
        public async Task ADownloadThatKeepsGrowing_IsWaitedFor_AndItsFinishedFileReturned()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var partial = Path.Combine(folder.Path, "book.epub.crdownload");
            await File.WriteAllBytesAsync(partial, new byte[10]);
            var waiting = SourceBrowser.WaitForDownloadAsync(folder.Path, TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(1500), Total, null, TimeProvider.System, default);

            // Longer than the idle limit in all, but growing all the time.
            for (var step = 1; step <= 6; step++)
            {
                await Task.Delay(500);
                await File.AppendAllTextAsync(partial, "more bytes");
            }

            File.Move(partial, Path.Combine(folder.Path, "book.epub"));
            Assert.AreEqual(Path.Combine(folder.Path, "book.epub"), await waiting);
        }
    }
}
