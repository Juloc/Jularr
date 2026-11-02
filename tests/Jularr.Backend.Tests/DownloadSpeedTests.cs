using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Common;
using Jularr.Integrations.Logic.Sabnzbd;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DownloadSpeedTests
    {
        private static readonly DateTimeOffset Start = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

        // Every block waits behind the ones before it at the limit's rate; an idle downloader saves nothing up, and no limit lets everything through.
        [TestMethod]
        public void RateLimiter_BlocksFollowEachOtherAtTheRate_AndNoLimitWaitsNothing()
        {
            var limiter = new ByteRateLimiter { BytesPerSecond = 1000 };
            Assert.AreEqual(TimeSpan.Zero, limiter.Reserve(500, Start));
            Assert.AreEqual(TimeSpan.FromSeconds(0.5), limiter.Reserve(500, Start));
            Assert.AreEqual(TimeSpan.FromSeconds(0.5), limiter.Reserve(1000, Start.AddSeconds(0.5)));
            Assert.AreEqual(TimeSpan.Zero, limiter.Reserve(1000, Start.AddMinutes(1)), "An idle downloader starts at once.");

            limiter.BytesPerSecond = 0;
            Assert.AreEqual(TimeSpan.Zero, limiter.Reserve(1_000_000, Start.AddMinutes(1)));
        }

        // A client's speed line takes one sample per interval (the newest value of it), keeps a bounded number and starts again after a long gap.
        [TestMethod]
        public void SpeedLine_OneSamplePerInterval_BoundedAndRestartedAfterAGap()
        {
            var time = new Clock(Start);
            var speeds = new DownloadSpeeds(time);
            speeds.Record("Usenet", 10);
            CollectionAssert.AreEqual(new long[] { 20 }, speeds.Record("Usenet", 20), "Within one interval the newest value counts.");
            for (var sample = 0; sample < DownloadSpeeds.SampleCount + 5; sample++)
            {
                time.Now += DownloadSpeeds.SampleInterval;
                speeds.Record("Usenet", sample);
            }

            var line = speeds.Record("Usenet", 99);
            Assert.AreEqual(DownloadSpeeds.SampleCount, line.Length);
            Assert.AreEqual(99, line[^1]);
            time.Now += DownloadSpeeds.SampleInterval * (DownloadSpeeds.SampleCount + 2);
            CollectionAssert.AreEqual(new long[] { 7 }, speeds.Record("Usenet", 7));
        }

        // A news server's last pass: connections and pace while it is recent, articles and missing ones added up.
        [TestMethod]
        public void NewsServer_PassesAddUpArticles_AndAnOldPassHoldsNoConnections()
        {
            var time = new Clock(Start);
            var speeds = new DownloadSpeeds(time);
            speeds.RecordServer(9, 12, 45_000_000, TimeSpan.FromSeconds(45), 100, 1);
            speeds.RecordServer(9, 10, 90_000_000, TimeSpan.FromSeconds(45), 200, 3);
            Assert.AreEqual((10, 2_000_000L, 300, 4), (speeds.Server(9)!.Connections, speeds.Server(9)!.BytesPerSecond, speeds.Server(9)!.Articles, speeds.Server(9)!.Missing));
            time.Now += TimeSpan.FromMinutes(5);
            Assert.AreEqual((0, 0L, 300), (speeds.Server(9)!.Connections, speeds.Server(9)!.BytesPerSecond, speeds.Server(9)!.Articles));
            Assert.IsNull(speeds.Server(10));
        }

        // SABnzbd's queue names each job's priority and place.
        [TestMethod]
        public void SabnzbdQueue_ReadsPriorityAndPlace()
        {
            var queue = SabnzbdClient.ParseQueueResponse("""{"queue":{"slots":[{"nzo_id":"a","filename":"A","priority":"High","index":0},{"nzo_id":"b","filename":"B","priority":"Force","index":1},{"nzo_id":"c","filename":"C"}]}}""");
            CollectionAssert.AreEqual(new int?[] { 1, 2, null }, queue.Jobs.Select(job => job.Priority).ToArray());
            CollectionAssert.AreEqual(new int?[] { 0, 1, null }, queue.Jobs.Select(job => job.Index).ToArray());
        }

        private sealed class Clock(DateTimeOffset start) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = start;

            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
