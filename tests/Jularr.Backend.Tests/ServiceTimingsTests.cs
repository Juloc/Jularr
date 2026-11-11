using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ServiceTimingsTests
    {
        [TestMethod]
        public void Timings_Last24Hours_SumPerServiceAndForgetOlderCalls()
        {
            var clock = new Clock();
            var timings = new ServiceTimings(clock);
            timings.Record("User/ReadA", TimeSpan.FromMilliseconds(10), false);
            timings.Record("User/ReadA", TimeSpan.FromMilliseconds(30), true);
            clock.Advance(TimeSpan.FromHours(12));
            timings.Record("User/ReadB", TimeSpan.FromMilliseconds(5), false);

            // Most time in total first; calls, errors and the longest call per service, and all of them together.
            var read = timings.Read(20);
            CollectionAssert.AreEqual(new[] { "User/ReadA", "User/ReadB" }, read.Select(timing => timing.Service).ToArray());
            Assert.AreEqual(2L, read[0].Count);
            Assert.AreEqual(1L, read[0].Errors);
            Assert.AreEqual(30d, read[0].MaxMilliseconds);
            var totals = timings.Totals();
            Assert.AreEqual(3L, totals.Calls);
            Assert.AreEqual(1L, totals.Errors);
            Assert.AreEqual(45d, totals.TotalMilliseconds);

            // A day later only the newer calls remain; a reused slice starts over.
            clock.Advance(TimeSpan.FromHours(13));
            Assert.AreEqual("User/ReadB", timings.Read(20).Single().Service);
            clock.Advance(TimeSpan.FromHours(11));
            timings.Record("User/ReadB", TimeSpan.FromMilliseconds(7), false);
            Assert.AreEqual(1L, timings.Read(20).Single().Count);

            // Switched off, nothing is kept or recorded.
            timings.Enable(false);
            timings.Record("User/ReadC", TimeSpan.FromMilliseconds(1), false);
            Assert.AreEqual(0, timings.Read(20).Count);
        }

        [TestMethod]
        public void Timings_Save_OnlyWhatChanged_AndAReloadAddsUpWithNewCalls()
        {
            var clock = new Clock();
            var timings = new ServiceTimings(clock);
            timings.Record("User/ReadA", TimeSpan.FromMilliseconds(10), false);

            // Changed slices are handed out until the saver confirms them; a call after that makes the slice changed again.
            var first = timings.ReadUnsaved();
            Assert.AreEqual(1, first.Count);
            Assert.AreEqual(1, timings.ReadUnsaved().Count);
            timings.MarkSaved(first);
            Assert.AreEqual(0, timings.ReadUnsaved().Count);
            timings.Record("User/ReadA", TimeSpan.FromMilliseconds(20), false);
            var second = timings.ReadUnsaved().Single();
            Assert.AreEqual(2L, second.Count);
            Assert.AreEqual(30L, second.TotalMilliseconds);

            // A call between reading and confirming keeps the slice changed.
            timings.Record("User/ReadA", TimeSpan.FromMilliseconds(5), false);
            timings.MarkSaved([second]);
            Assert.AreEqual(3L, timings.ReadUnsaved().Single().Count);

            // After a restart what was kept comes back once and adds up with what happened since; slices of more than a day ago are ignored.
            var restarted = new ServiceTimings(clock);
            restarted.Record("User/ReadA", TimeSpan.FromMilliseconds(100), true);
            var index = ServiceTimings.IndexOf(clock.GetUtcNow().UtcDateTime);
            restarted.Load([new ServiceTimingSlice("User/ReadA", index, 3, 0, 35, 20, 0), new ServiceTimingSlice("User/Old", index - 30, 9, 0, 90, 10, 0)]);
            var read = restarted.Read(20).Single();
            Assert.AreEqual("User/ReadA", read.Service);
            Assert.AreEqual(4L, read.Count);
            Assert.AreEqual(1L, read.Errors);
            Assert.AreEqual(100d, read.MaxMilliseconds);
            Assert.AreEqual(1, restarted.ReadUnsaved().Count, "What was called before the load is not in the database yet.");
        }

        [TestMethod]
        public void Timings_Pages_KeptApartFromServicesAndSavedLikeThem()
        {
            var clock = new Clock();
            var timings = new ServiceTimings(clock);
            timings.Record("Admin/ReadExternalWorkSyncsV1", TimeSpan.FromMilliseconds(8), false);
            timings.RecordPage("/Admin/Catalog/Catalog", TimeSpan.FromMilliseconds(20), false);
            timings.RecordPage("/Admin/Catalog/Catalog", TimeSpan.FromMilliseconds(40), false);
            timings.RecordPage("/User/Discover/Index:GetRecommendations", TimeSpan.FromMilliseconds(90), true);

            // The service list and its totals hold services only; a page's time already holds the services it called.
            Assert.AreEqual("Admin/ReadExternalWorkSyncsV1", timings.Read(20).Single().Service);
            Assert.AreEqual(1L, timings.Totals().Calls);

            // One row per page by its view path, the slowest on average first.
            var pages = timings.ReadPages();
            CollectionAssert.AreEqual(new[] { "/User/Discover/Index:GetRecommendations", "/Admin/Catalog/Catalog" }, pages.Select(page => page.Service).ToArray());
            Assert.AreEqual(2L, pages[1].Count);
            Assert.AreEqual(60d, pages[1].TotalMilliseconds);
            Assert.AreEqual(1L, pages[0].Errors);

            // Pages are saved and read back after a restart like services.
            var restarted = new ServiceTimings(clock);
            restarted.Load(timings.ReadUnsaved());
            Assert.AreEqual(2L, restarted.ReadPages().Single(page => page.Service == "/Admin/Catalog/Catalog").Count);

            // Switched off, no page is kept or recorded.
            timings.Enable(false);
            timings.RecordPage("/Admin/Catalog/Catalog", TimeSpan.FromMilliseconds(5), false);
            Assert.AreEqual(0, timings.ReadPages().Count);
        }

        private sealed class Clock : TimeProvider
        {
            private DateTimeOffset _now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance(TimeSpan by) => _now += by;
        }
    }
}
