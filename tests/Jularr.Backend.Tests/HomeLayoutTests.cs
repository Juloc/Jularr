using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Home.V1;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class HomeLayoutTests
    {
        private const string Route = "/api/v1/home/layout";

        [TestMethod]
        public async Task HomeLayout_ProfileOrdersAndHidesShelves()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);

            // Without a choice the default order applies.
            CollectionAssert.AreEqual(Enum.GetValues<HomeShelfType>(), (await ReadAsync(host)).Select(shelf => shelf.HomeShelfTypeId).ToArray());

            // Every shelf once; hidden ones among them.
            using var duplicate = await host.SendAsync(HttpMethod.Put, Route, new SaveHomeLayoutDataV1(["RecentlyAdded", "RecentlyAdded", "ContinueWatching", "ContinueListening", "ContinueReading"], []));
            StringAssert.Contains(await duplicate.Content.ReadAsStringAsync(), "unknown_or_duplicate_id");
            using var saved = await host.SendAsync(HttpMethod.Put, Route,
                new SaveHomeLayoutDataV1(["RecentlyAdded", "ContinueWatching", "ContinueListening", "ContinueReading", "Recommendations"], ["Recommendations"]));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            var layout = await ReadAsync(host);
            Assert.AreEqual(HomeShelfType.RecentlyAdded, layout[0].HomeShelfTypeId);
            Assert.IsTrue(layout.Single(shelf => shelf.HomeShelfTypeId == HomeShelfType.Recommendations).IsHidden);
            using var home = await host.SendAsync(HttpMethod.Get, "/");
            Assert.IsFalse((await home.Content.ReadAsStringAsync()).Contains("id=\"discover-recommendations\"", StringComparison.Ordinal));

            // The settings page moves and shows shelves with plain forms.
            using var settings = await host.SendAsync(HttpMethod.Get, "/Settings");
            var html = await settings.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode, html);
            using var moved = await host.SendAsync(HttpMethod.Post, "/Settings?handler=Home&shelf=ContinueWatching&action=Up", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = LibraryTests.Token(html)
            });
            Assert.AreEqual(HttpStatusCode.Redirect, moved.StatusCode, await moved.Content.ReadAsStringAsync());
            using var shown = await host.SendAsync(HttpMethod.Post, "/Settings?handler=Home&shelf=Recommendations&action=Show", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = LibraryTests.Token(html)
            });
            Assert.AreEqual(HttpStatusCode.Redirect, shown.StatusCode, await shown.Content.ReadAsStringAsync());
            layout = await ReadAsync(host);
            Assert.AreEqual(HomeShelfType.ContinueWatching, layout[0].HomeShelfTypeId);
            Assert.IsTrue(layout.All(shelf => !shelf.IsHidden));

            // Switched off, everyone sees the default page.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'HomeLayout'");
            using var off = await host.SendAsync(HttpMethod.Get, Route);
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
            using var plain = await host.SendAsync(HttpMethod.Get, "/Settings");
            Assert.IsFalse((await plain.Content.ReadAsStringAsync()).Contains("handler=Home", StringComparison.Ordinal));
        }

        private static async Task<IReadOnlyList<HomeShelfV1>> ReadAsync(AuthenticationTests.Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, Route);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<BaseDataList<HomeShelfV1>>())!;
        }
    }
}
