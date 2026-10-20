using Jularr.Backend.Logic.Acquisition;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class EditionLanguageFilterTests
    {
        [TestMethod]
        [DataRow("DE", "en", "de", null, true)]
        [DataRow("de", "en", "de", null, true)]
        [DataRow("ger", "en", "de", null, true)]
        [DataRow("EN", "en", "de", null, false)]
        [DataRow("", "en", "de", null, false)]
        [DataRow("FR", "en", "de", null, false)]
        [DataRow("DE", "en,de", "", null, false)]
        [DataRow("DE", "de", "de", null, false)]
        [DataRow("EN", "en,de", "", "de", true)]
        [DataRow("DE", "en,de", "", "de", false)]
        [DataRow("", "en,de", "", "de", false)]
        public void Rejects_AReleaseOnlyInTheLanguageOfAnEditionThatIsSwitchedOff(string release, string monitored, string unmonitored, string? focus, bool rejected)
        {
            string[] Split(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var languages = release.Length == 0 ? Array.Empty<string>() : new[] { release };
            Assert.AreEqual(rejected, EditionLanguageFilter.Rejects(languages, Split(monitored), Split(unmonitored), focus));
        }
    }
}
