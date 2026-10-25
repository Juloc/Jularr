using System.Net;
using System.Text.Json;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Modules.Pwa;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    // The app is installable (manifest, icons, a worker that only passes requests through) and caches nothing.
    [TestClass]
    public sealed class PwaTests
    {
        [TestMethod]
        public async Task Manifest_IsPublic_NamesTheAppAndLinksFingerprintedIcons()
        {
            await using var app = await StartAsync();
            using var response = await app.GetTestClient().GetAsync("/manifest.webmanifest");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("no-cache", response.Headers.CacheControl?.ToString());

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            Assert.AreEqual("Jularr", root.GetProperty("name").GetString());
            Assert.AreEqual("Jularr", root.GetProperty("short_name").GetString());
            Assert.AreEqual("/", root.GetProperty("start_url").GetString());
            Assert.AreEqual("/", root.GetProperty("scope").GetString());
            Assert.AreEqual("standalone", root.GetProperty("display").GetString());
            Assert.AreEqual("#8040f7", root.GetProperty("theme_color").GetString());
            Assert.AreEqual("#ffffff", root.GetProperty("background_color").GetString());

            var icons = root.GetProperty("icons").EnumerateArray().ToArray();
            CollectionAssert.AreEquivalent(new[] { "192x192:any", "512x512:any", "512x512:maskable" },
                icons.Select(icon => icon.GetProperty("sizes").GetString() + ":" + icon.GetProperty("purpose").GetString()).ToArray());
            var build = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Jularr.Frontend/wwwroot"));
            foreach (var icon in icons)
            {
                var source = icon.GetProperty("src").GetString()!;
                StringAssert.Matches(source, new System.Text.RegularExpressions.Regex(@"^/build/assets/icon-[A-Za-z0-9_-]+-[A-Za-z0-9_-]{8,}\.png$"));
                Assert.IsTrue(File.Exists(Path.Combine(build, source.TrimStart('/'))), source);
            }
        }

        [TestMethod]
        public async Task ProfileColoredIcons_AreOpaqueSafeAndContainNoAccountIdentifiers()
        {
            await using var app = await StartAsync();
            using var response = await app.GetTestClient().GetAsync("/pwa/icons/maskable-512/336699.png");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("image/png", response.Content.Headers.ContentType?.MediaType);
            Assert.IsTrue(response.Headers.CacheControl?.Public == true);
            var png = await response.Content.ReadAsByteArrayAsync();
            using var bitmap = SkiaSharp.SKBitmap.Decode(png);
            Assert.AreEqual(512, bitmap.Width);
            Assert.AreEqual(512, bitmap.Height);
            var colored = 0;
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                Assert.AreEqual((byte)255, color.Alpha);
                if (color.Red == 255 && color.Green == 255 && color.Blue == 255) continue;
                colored++;
                // Android maskable icons can clip outside this central 80%-diameter circle.
                var dx = x - 255.5d;
                var dy = y - 255.5d;
                Assert.IsTrue(dx * dx + dy * dy <= 205 * 205, $"Unsafe color at {x},{y}");
            }
            Assert.IsTrue(colored > 1000, "The parrot path must be rendered.");
            using var bad = await app.GetTestClient().GetAsync("/pwa/icons/512/not-hex.png");
            Assert.AreEqual(HttpStatusCode.NotFound, bad.StatusCode);
        }

        [TestMethod]
        public async Task FaviconColors_UseCanonicalVectorWithPublicColorOnlyUrls()
        {
            await using var app = await StartAsync();
            using var svgResponse = await app.GetTestClient().GetAsync("/pwa/icons/favicon/1b8aa0.svg");
            Assert.AreEqual(HttpStatusCode.OK, svgResponse.StatusCode);
            Assert.AreEqual("image/svg+xml", svgResponse.Content.Headers.ContentType?.MediaType);
            StringAssert.Contains(await svgResponse.Content.ReadAsStringAsync(), "fill=\"#1b8aa0\"");
            Assert.IsFalse((await svgResponse.Content.ReadAsStringAsync()).Contains("currentColor", StringComparison.Ordinal));

            using var pngResponse = await app.GetTestClient().GetAsync("/pwa/icons/favicon-32/1b8aa0.png");
            Assert.AreEqual(HttpStatusCode.OK, pngResponse.StatusCode);
            Assert.AreEqual("image/png", pngResponse.Content.Headers.ContentType?.MediaType);
            using var bitmap = SkiaSharp.SKBitmap.Decode(await pngResponse.Content.ReadAsByteArrayAsync());
            Assert.AreEqual(32, bitmap.Width);
            Assert.AreEqual(32, bitmap.Height);

            using var invalid = await app.GetTestClient().GetAsync("/pwa/icons/favicon/not-a-color.svg");
            Assert.AreEqual(HttpStatusCode.NotFound, invalid.StatusCode);
        }

        [TestMethod]
        public async Task SignedInManifestAndAppleTouchIcon_UseThePersistedMainProfileAccent()
        {
            await using var host = await Host.CreateAsync(razor: true, endpoints: endpoints => endpoints.MapJularrPwa());
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"PwaMainProfileId\" = 101 WHERE \"Id\" = 41");
            using var authState = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login",
                new Jularr.Backend.Data.Public.Authentication.V1.LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/profiles/101/pwa/accent",
                new Jularr.Backend.Data.User.Profile.V1.SavePwaAccentDataV1("#336699"));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());

            using var manifest = await host.SendAsync(HttpMethod.Get, "/manifest.webmanifest");
            Assert.AreEqual(HttpStatusCode.OK, manifest.StatusCode);
            Assert.IsTrue(manifest.Headers.CacheControl?.NoStore == true, "Account-specific manifest must never enter a shared cache.");
            using var json = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync());
            var icons = json.RootElement.GetProperty("icons").EnumerateArray().Select(icon => icon.GetProperty("src").GetString()).ToArray();
            CollectionAssert.AreEquivalent(new[] {
                "/pwa/icons/192/336699.png", "/pwa/icons/512/336699.png", "/pwa/icons/maskable-512/336699.png"
            }, icons);

            Assert.AreEqual("#336699", json.RootElement.GetProperty("theme_color").GetString());
            using var page = await host.SendAsync(HttpMethod.Get, "/Settings");
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
            StringAssert.Contains(await page.Content.ReadAsStringAsync(), "rel=\"apple-touch-icon\" href=\"/pwa/icons/apple-180/336699.png\"");
            StringAssert.Contains(await page.Content.ReadAsStringAsync(), "/pwa/icons/favicon/336699.svg");
            StringAssert.Contains(await page.Content.ReadAsStringAsync(), "/pwa/icons/favicon-32/336699.png");
        }

        [TestMethod]
        public async Task Version_IsPublicNeverCached_AndMatchesFrontendBuild()
        {
            await using var app = await StartAsync();
            using var response = await app.GetTestClient().GetAsync("/pwa/version");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("no-store", response.Headers.CacheControl?.ToString());
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var build = app.Services.GetRequiredService<FrontendAssets>().BuildId;
            Assert.AreEqual(build, document.RootElement.GetProperty("build").GetString());
        }

        [TestMethod]
        public async Task Worker_IsServedFromTheRootWithScope_NeverCached_AndPassesEveryRequestThrough()
        {
            await using var app = await StartAsync();
            using var response = await app.GetTestClient().GetAsync("/service-worker.js");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("text/javascript", response.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("no-cache", response.Headers.CacheControl?.ToString());
            Assert.AreEqual("/", response.Headers.GetValues("Service-Worker-Allowed").Single());

            var script = await response.Content.ReadAsStringAsync();
            StringAssert.Contains(script, "addEventListener(\"fetch\"");
            foreach (var forbidden in new[] { "respondWith", "caches", "importScripts" })
            {
                Assert.IsFalse(script.Contains(forbidden, StringComparison.Ordinal), forbidden + " would make the worker answer or cache.");
            }
        }

        [TestMethod]
        public async Task Layout_LinksTheManifestAndAppleTags_OnTheSignInPageAndSignedIn()
        {
            await using var host = await Host.CreateAsync(razor: true, endpoints: endpoints => endpoints.MapJularrPwa());
            using var login = await host.SendAsync(HttpMethod.Get, "/Account/Login");
            var loginHtml = await login.Content.ReadAsStringAsync();
            AssertInstallTags(loginHtml);
            var build = host.Services.GetRequiredService<FrontendAssets>().BuildId;
            StringAssert.Matches(build, new System.Text.RegularExpressions.Regex("^[0-9A-F]{16}$"));
            StringAssert.Contains(loginHtml, "data-build=\"" + build + "\"");

            var form = new Dictionary<string, string> { ["Username"] = "a@example.test", ["Password"] = "test-password-123", ["__RequestVerificationToken"] = LibraryTests.Token(loginHtml) };
            using var signedIn = await host.SendAsync(HttpMethod.Post, "/Account/Login", form);
            Assert.AreEqual(HttpStatusCode.Redirect, signedIn.StatusCode);
            using var home = await host.SendAsync(HttpMethod.Get, "/Settings");
            Assert.AreEqual(HttpStatusCode.OK, home.StatusCode);
            var homeHtml = await home.Content.ReadAsStringAsync();
            AssertInstallTags(homeHtml);
            StringAssert.Contains(homeHtml, "data-build=\"" + build + "\"");
        }

        // Only the two public files, over the real frontend assets: they need no database.
        private static async Task<WebApplication> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(FrontendAssets).Assembly.GetName().Name,
                ContentRootPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Jularr.Frontend"))
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<FrontendAssets>();
            var app = builder.Build();
            app.MapJularrPwa();
            await app.StartAsync();
            return app;
        }

        private static void AssertInstallTags(string html)
        {
            StringAssert.Contains(html, "<link rel=\"manifest\" href=\"/manifest.webmanifest\" />");
            StringAssert.Contains(html, "name=\"theme-color\"");
            StringAssert.Contains(html, "name=\"mobile-web-app-capable\"");
            StringAssert.Contains(html, "name=\"apple-mobile-web-app-capable\"");
            StringAssert.Contains(html, "name=\"apple-mobile-web-app-title\"");
            StringAssert.Contains(html, "name=\"apple-mobile-web-app-status-bar-style\"");
            StringAssert.Contains(html, "rel=\"apple-touch-icon\"");
            StringAssert.Matches(html, new System.Text.RegularExpressions.Regex(@"<script src=""/build/assets/[^""]*pwa\.entry[^""]*\.js"" type=""module""></script>"));
        }
    }
}
