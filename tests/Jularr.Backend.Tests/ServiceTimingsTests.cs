using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ServiceTimingsTests
    {
        [TestMethod]
        public void Timings_LastHour_SumPerServiceAndForgetOlderCalls()
        {
            var clock = new Clock();
            var timings = new ServiceTimings(clock);
            timings.Record("User/ReadA", TimeSpan.FromMilliseconds(10), false);
            timings.Record("User/ReadA", TimeSpan.FromMilliseconds(30), true);
            clock.Advance(TimeSpan.FromMinutes(30));
            timings.Record("User/ReadB", TimeSpan.FromMilliseconds(5), false);

            // Most time in total first; calls, errors and the longest call per service.
            var read = timings.Read(20);
            CollectionAssert.AreEqual(new[] { "User/ReadA", "User/ReadB" }, read.Select(timing => timing.Service).ToArray());
            Assert.AreEqual(2L, read[0].Count);
            Assert.AreEqual(1L, read[0].Errors);
            Assert.AreEqual(30d, read[0].MaxMilliseconds);

            // An hour later only the newer calls remain; a reused slice starts over.
            clock.Advance(TimeSpan.FromMinutes(35));
            Assert.AreEqual("User/ReadB", timings.Read(20).Single().Service);
            clock.Advance(TimeSpan.FromMinutes(25));
            timings.Record("User/ReadB", TimeSpan.FromMilliseconds(7), false);
            Assert.AreEqual(1L, timings.Read(20).Single().Count);
        }

        private sealed class Clock : TimeProvider
        {
            private DateTimeOffset _now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance(TimeSpan by) => _now += by;
        }
    }
}
