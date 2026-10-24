using Jularr.Frontend.Framework;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class BrowserTimeZoneTests
    {
        [TestMethod]
        public void Today_UsesBrowserZoneNearMidnightInsteadOfServerUtcDate()
        {
            var instant = new DateTimeOffset(2026, 10, 23, 23, 30, 0, TimeSpan.Zero);
            Assert.AreEqual(new DateOnly(2026, 10, 24), BrowserTimeZone.TodayAt(instant, "Europe/Berlin"));
            Assert.AreEqual(new DateOnly(2026, 10, 23), BrowserTimeZone.TodayAt(instant, "America/New_York"));
            Assert.AreEqual(new DateOnly(2026, 10, 23), BrowserTimeZone.TodayAt(instant, "UTC"));
        }

        [TestMethod]
        public void Today_RespectsDaylightSavingInBothSeasonsAndAcrossUtcMidnight()
        {
            var summer = new DateTimeOffset(2026, 7, 1, 22, 30, 0, TimeSpan.Zero);
            var winter = new DateTimeOffset(2026, 12, 1, 22, 30, 0, TimeSpan.Zero);
            Assert.AreEqual(new DateOnly(2026, 7, 2), BrowserTimeZone.TodayAt(summer, "Europe/Berlin"));
            Assert.AreEqual(new DateOnly(2026, 12, 1), BrowserTimeZone.TodayAt(winter, "Europe/Berlin"));
            Assert.AreEqual(new DateOnly(2026, 7, 1), BrowserTimeZone.TodayAt(summer, "America/New_York"));
        }

        [TestMethod]
        public void ReleaseCountdown_ChangesAtLocalMidnight_NotAtUtcMidnight()
        {
            var instant = new DateTimeOffset(2026, 10, 23, 23, 30, 0, TimeSpan.Zero);
            var release = new DateOnly(2026, 10, 25);
            Assert.AreEqual(1, release.DayNumber - BrowserTimeZone.TodayAt(instant, "Europe/Berlin").DayNumber);
            Assert.AreEqual(2, release.DayNumber - BrowserTimeZone.TodayAt(instant, "America/New_York").DayNumber);
            var lastAllowed = new DateOnly(2026, 11, 7);
            Assert.AreEqual(14, lastAllowed.DayNumber - BrowserTimeZone.TodayAt(instant, "Europe/Berlin").DayNumber);
            Assert.AreEqual(15, lastAllowed.DayNumber - BrowserTimeZone.TodayAt(instant, "America/New_York").DayNumber);
        }

        [TestMethod]
        public void Today_RejectsInvalidCookieAndFallsBackToUtc()
        {
            var instant = new DateTimeOffset(2026, 10, 23, 23, 30, 0, TimeSpan.Zero);
            var expected = new DateOnly(2026, 10, 23);
            Assert.AreEqual(expected, BrowserTimeZone.TodayAt(instant, null));
            Assert.AreEqual(expected, BrowserTimeZone.TodayAt(instant, "Invalid/Zone"));
            Assert.AreEqual(expected, BrowserTimeZone.TodayAt(instant, "Europe/Berlin;Bad=true"));
            Assert.AreEqual(expected, BrowserTimeZone.TodayAt(instant, new string('x', 100)));
        }
    }
}
