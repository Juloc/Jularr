using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.AniList;
using Jularr.Integrations.Logic.Common;
using Jularr.Integrations.Logic.Igdb;
using Jularr.Integrations.Logic.MusicBrainz;
using Jularr.Integrations.Logic.Narou;
using Jularr.Integrations.Logic.OpenLibrary;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

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

        // The typed clients get the process-wide pace; their own fallback pace would give every client instance its own spacing.
        [TestMethod]
        public void Production_TypedClients_UseTheProcessWidePace()
        {
            using var services = new ServiceCollection().AddJularrServices(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JULARR_DB"] = "Host=127.0.0.1;Port=1;Username=unused;Database=unused;Timeout=1",
                ["DataPath"] = Path.Combine(Path.GetTempPath(), "jularr-tests", Guid.NewGuid().ToString("N"))
            }).Build()).BuildServiceProvider();
            static object? PaceOf(object client) => client.GetType().GetField("_pace", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client);

            Assert.AreSame(services.GetRequiredService<RequestPace<OpenLibraryClient>>(), PaceOf(services.GetRequiredService<OpenLibraryClient>()));
            Assert.AreSame(services.GetRequiredService<RequestPace<AniListClient>>(), PaceOf(services.GetRequiredService<AniListClient>()));
            Assert.AreSame(services.GetRequiredService<RequestPace<MusicBrainzClient>>(), PaceOf(services.GetRequiredService<MusicBrainzClient>()));
            Assert.AreSame(services.GetRequiredService<RequestPace<IgdbClient>>(), PaceOf(services.GetRequiredService<IgdbClient>()));
            Assert.AreSame(services.GetRequiredService<NarouPace>(), PaceOf(services.GetRequiredService<NarouClient>()));
        }

        [TestMethod]
        public async Task Turns_StartAtLeastTheSpacingApart()
        {
            var pace = new RequestPace<OpenLibraryClient>(TimeSpan.FromMilliseconds(300));
            await pace.WaitForTurnAsync(default);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await pace.WaitForTurnAsync(default);

            // A timer may fire a clock tick early; it never fires a whole spacing early.
            Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(285), watch.Elapsed);
        }
    }
}
