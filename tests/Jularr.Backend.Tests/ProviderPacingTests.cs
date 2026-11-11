using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.AniList;
using Jularr.Integrations.Logic.Common;
using Jularr.Integrations.Logic.Igdb;
using Jularr.Integrations.Logic.MusicBrainz;
using Jularr.Integrations.Logic.Narou;
using Jularr.Integrations.Logic.OpenLibrary;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Backend.Tests
{
    // Every metadata provider keeps the request spacing it asks for, one pace for the whole process; test hosts give their fakes a pace without spacing.
    [TestClass]
    public sealed class ProviderPacingTests
    {
        [TestMethod]
        [DataRow("openlibrary", 1000)]
        [DataRow("anilist", 700)]
        [DataRow("musicbrainz", 1100)]
        [DataRow("igdb", 260)]
        [DataRow("narou-api", 1000)]
        [DataRow("narou-site", 1000)]
        public void Production_KeepsTheProvidersSpacing_InOnePaceForTheProcess(string provider, int milliseconds)
        {
            using var services = new ServiceCollection().AddJularrServices(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JULARR_DB"] = "Host=127.0.0.1;Port=1;Username=unused;Database=unused;Timeout=1",
                ["DataPath"] = Path.Combine(Path.GetTempPath(), "jularr-tests", Guid.NewGuid().ToString("N"))
            }).Build()).BuildServiceProvider();
            (object Pace, TimeSpan Spacing) Pace() => provider switch
            {
                "openlibrary" => Of(services.GetRequiredService<RequestPace<OpenLibraryClient>>()),
                "anilist" => Of(services.GetRequiredService<RequestPace<AniListClient>>()),
                "musicbrainz" => Of(services.GetRequiredService<RequestPace<MusicBrainzClient>>()),
                "igdb" => Of(services.GetRequiredService<RequestPace<IgdbClient>>()),
                "narou-api" => Of(services.GetRequiredService<NarouPace>().Api),
                _ => Of(services.GetRequiredService<NarouPace>().Site)
            };

            var (pace, spacing) = Pace();
            Assert.AreSame(pace, Pace().Pace);
            Assert.AreEqual(TimeSpan.FromMilliseconds(milliseconds), spacing);
        }

        private static (object Pace, TimeSpan Spacing) Of<TClient>(RequestPace<TClient> pace) => (pace, pace.Spacing);

        [TestMethod]
        public async Task Turns_StartAtLeastTheSpacingApart()
        {
            var pace = new RequestPace<OpenLibraryClient>(TimeSpan.FromMilliseconds(300));
            await pace.WaitForTurnAsync(default);
            var first = pace.Last;
            await pace.WaitForTurnAsync(default);

            // A timer may fire a clock tick early; it never fires a whole spacing early.
            Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(285), pace.Last - first);
        }
    }
}
