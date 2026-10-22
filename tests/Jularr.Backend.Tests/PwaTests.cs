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

            var form = new Dictionary<string, string> { ["Username"] = "a@example.test", ["Password"] = "test-password-123", ["__RequestVerificationToken"] = LibraryTests.Token(loginHtml) };
            using var signedIn = await host.SendAsync(HttpMethod.Post, "/Account/Login", form);
            Assert.AreEqual(HttpStatusCode.Redirect, signedIn.StatusCode);
            using var home = await host.SendAsync(HttpMethod.Get, "/Settings");
            Assert.AreEqual(HttpStatusCode.OK, home.StatusCode);
            AssertInstallTags(await home.Content.ReadAsStringAsync());
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
