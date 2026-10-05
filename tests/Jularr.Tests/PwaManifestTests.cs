using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Localization;

namespace Jularr.Tests;

[TestClass]
public sealed class PwaManifestTests
{
    [TestMethod]
    public void ManifestAndRuntimeCoverInstallOfflineAndPlatformCapabilities()
    {
        var repoRoot = FindRepositoryRoot();
        var webRoot = Path.Combine(repoRoot, "src", "Jularr.Web", "wwwroot");

        var manifestPath = Path.Combine(webRoot, "manifest.webmanifest");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;

        Assert.AreEqual("Jularr", root.GetProperty("name").GetString());
        Assert.AreEqual("Jularr", root.GetProperty("short_name").GetString());
        Assert.AreEqual("standalone", root.GetProperty("display").GetString());
        Assert.AreEqual("/", root.GetProperty("start_url").GetString());
        Assert.IsFalse(root.GetProperty("prefer_related_applications").GetBoolean());

        var icons = root.GetProperty("icons").EnumerateArray().ToArray();
        Assert.IsTrue(icons.Any(x => x.GetProperty("sizes").GetString() == "192x192"));
        Assert.IsTrue(icons.Any(x => x.GetProperty("sizes").GetString() == "512x512"));
        Assert.IsTrue(icons.Any(x =>
            x.TryGetProperty("purpose", out var purpose)
            && purpose.GetString() == "maskable"));

        foreach (var icon in icons)
        {
            var source = icon.GetProperty("src").GetString();
            Assert.IsFalse(string.IsNullOrWhiteSpace(source));

            var relative = source!.TrimStart('/')
                .Replace('/', Path.DirectorySeparatorChar);
            Assert.IsTrue(
                File.Exists(Path.Combine(webRoot, relative)),
                $"Manifest icon '{source}' does not exist.");
        }

        Assert.IsTrue(File.Exists(
            Path.Combine(webRoot, "icons", "apple-touch-icon.png")));

        var layout = File.ReadAllText(Path.Combine(
            repoRoot,
            "src",
            "Jularr.Web",
            "Pages",
            "Shared",
            "_Layout.cshtml"));

        StringAssert.Contains(layout, "viewport-fit=cover");
        StringAssert.Contains(layout, "apple-mobile-web-app-capable");
        StringAssert.Contains(layout, "apple-mobile-web-app-status-bar-style");
        StringAssert.Contains(layout, "apple-touch-icon");
        StringAssert.Contains(layout, "href=\"~/css/site.css\" asp-append-version=\"true\"");
        StringAssert.Contains(layout, "src=\"~/js/pwa.js\" asp-append-version=\"true\"");
        StringAssert.Contains(layout, "data-app-version=\"@AppBuildInfo.Version\"");

        var siteCss = File.ReadAllText(Path.Combine(webRoot, "css", "site.css"));
        Assert.IsFalse(
            siteCss.Contains("font-family: Inter", StringComparison.Ordinal),
            "The global app shell must not depend on a locally installed Inter font.");

        var offlineHtml = File.ReadAllText(Path.Combine(webRoot, "offline.html"));
        Assert.IsFalse(
            offlineHtml.Contains("font-family: Inter", StringComparison.Ordinal),
            "The offline shell must use the same deterministic system-font policy.");

        var serviceWorker = File.ReadAllText(
            Path.Combine(webRoot, "service-worker.js"));

        StringAssert.Contains(serviceWorker, "\"/offline.html\"");
        Assert.IsFalse(serviceWorker.Contains("\"/css/site.css\"", StringComparison.Ordinal));
        Assert.IsFalse(serviceWorker.Contains("\"/js/pwa.js\"", StringComparison.Ordinal));
        Assert.IsFalse(serviceWorker.Contains("SKIP_WAITING", StringComparison.Ordinal));
        StringAssert.Contains(serviceWorker, "CACHE_CURRENT_ASSETS");
        StringAssert.Contains(serviceWorker, "cache: \"reload\"");
        StringAssert.Contains(serviceWorker, "jularr-static-v1-");
        StringAssert.Contains(serviceWorker, "workerUrl.searchParams.get(\"v\")");
        StringAssert.Contains(serviceWorker, "cacheFirstVersioned");
        StringAssert.Contains(serviceWorker, "isVersionedAsset");
        StringAssert.Contains(serviceWorker, "pathname.startsWith(\"/build/\")");
        StringAssert.Contains(serviceWorker, "offline-media-worker.js?v=1");
        StringAssert.Contains(serviceWorker, "\"/js/offline-library.js\"");
        StringAssert.Contains(serviceWorker, "\"/js/offline-library-storage.js\"");
        StringAssert.Contains(serviceWorker, "startsWith(\"/_offline-media/\")");
        Assert.IsFalse(serviceWorker.Contains("offline-review.js", StringComparison.Ordinal));
        StringAssert.Contains(serviceWorker, "request.mode === \"navigate\"");
        Assert.IsFalse(
            serviceWorker.Contains("self.clients.claim()", StringComparison.Ordinal),
            "A newly activated worker must not silently take over other open tabs.");
        Assert.IsFalse(
            serviceWorker.Contains("putLatestAsset", StringComparison.Ordinal),
            "Versioned asset variants must not delete each other from a shared cache.");
        Assert.IsFalse(serviceWorker.Contains("\"/Learn", StringComparison.Ordinal));
        Assert.IsFalse(serviceWorker.Contains("\"/Library", StringComparison.Ordinal));
        Assert.IsFalse(serviceWorker.Contains("handler=Media", StringComparison.Ordinal));

        var offlineShell = File.ReadAllText(Path.Combine(webRoot, "offline.html"));
        StringAssert.Contains(offlineShell, "/js/offline-library.js");
        StringAssert.Contains(offlineShell, "/js/offline-library-storage.js");
        Assert.IsFalse(offlineShell.Contains("offline-review", StringComparison.Ordinal));

        var pwaRuntime = File.ReadAllText(
            Path.Combine(webRoot, "js", "pwa.js"));

        StringAssert.Contains(pwaRuntime, "beforeinstallprompt");
        StringAssert.Contains(pwaRuntime, "shellLabel(\"pwa.install.appleMobile\")");
        StringAssert.Contains(pwaRuntime, "shellLabel(\"pwa.install.appleDesktop\")");
        StringAssert.Contains(
            UiTranslationResources.Get("pwa.install.appleMobile").DefaultText,
            "Add to Home Screen");
        StringAssert.Contains(
            UiTranslationResources.Get("pwa.install.appleDesktop").DefaultText,
            "Add to Dock");
        StringAssert.Contains(pwaRuntime, "apple-mobile-web-app-capable");
        StringAssert.Contains(pwaRuntime, "viewport-fit=cover");
        StringAssert.Contains(pwaRuntime, "mediaSession");
        StringAssert.Contains(pwaRuntime, "wakeLock");
        // Picture-in-picture and full screen live in the player chrome, not in a second PWA button row.
        var playerPresentation = File.ReadAllText(Path.Combine(webRoot, "js", "player-presentation.js"));
        StringAssert.Contains(playerPresentation, "requestPictureInPicture");
        StringAssert.Contains(playerPresentation, "webkitSetPresentationMode");
        StringAssert.Contains(playerPresentation, "requestFullscreen");
        Assert.IsFalse(pwaRuntime.Contains("pwa-player-actions", StringComparison.Ordinal));
        StringAssert.Contains(pwaRuntime, "navigator.share");
        StringAssert.Contains(pwaRuntime, "currentFingerprintedAssets");
        StringAssert.Contains(pwaRuntime, "link[rel=\"modulepreload\"][href]");
        StringAssert.Contains(pwaRuntime, "url.searchParams.has(\"v\")");
        StringAssert.Contains(pwaRuntime, "url.pathname.startsWith(\"/build/\")");
        StringAssert.Contains(pwaRuntime, "serviceWorkerBuildKey");
        StringAssert.Contains(pwaRuntime, "encodeURIComponent(serviceWorkerBuildKey())");
        Assert.IsFalse(pwaRuntime.Contains("registration.update()", StringComparison.Ordinal));
        Assert.IsFalse(pwaRuntime.Contains("SKIP_WAITING", StringComparison.Ordinal));
        Assert.IsFalse(pwaRuntime.Contains("window.location.reload()", StringComparison.Ordinal));
        Assert.IsFalse(pwaRuntime.Contains("pwa-update-notice", StringComparison.Ordinal));
        Assert.IsFalse(
            pwaRuntime.Contains("currentFingerprintedAssets().slice().sort().join", StringComparison.Ordinal),
            "Page-specific assets must not create a new service-worker registration on every navigation.");
        StringAssert.Contains(pwaRuntime, "CACHE_CURRENT_ASSETS");
        StringAssert.Contains(pwaRuntime, "updateViaCache: \"none\"");
        Assert.IsFalse(
            pwaRuntime.Contains("controllerchange", StringComparison.Ordinal),
            "Only the tab whose user accepted the update should reload.");
    }

    [TestMethod]
    public void PwaRuntimeLabelsComeFromTheUiCatalog()
    {
        var repoRoot = FindRepositoryRoot();
        var webRoot = Path.Combine(repoRoot, "src", "Jularr.Web", "wwwroot");
        var pwaRuntime = File.ReadAllText(Path.Combine(webRoot, "js", "pwa.js"));
        var layout = File.ReadAllText(Path.Combine(
            repoRoot, "src", "Jularr.Web", "Pages", "Shared", "_Layout.cshtml"));

        // The layout serializes exactly the pwa.* catalog subset for the runtime.
        StringAssert.Contains(layout, "id=\"app-shell-text\"");
        StringAssert.Contains(layout, "ui.WithPrefix(\"pwa.\")");

        var keys = Regex.Matches(pwaRuntime, @"shellLabel\(""(?<key>[^""]+)""\)")
            .Select(x => x.Groups["key"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.IsTrue(keys.Length >= 9);
        foreach (var key in keys)
        {
            Assert.IsTrue(key.StartsWith("pwa.", StringComparison.Ordinal), key);
            Assert.IsTrue(UiTranslationResources.TryGet(key, out _), $"Missing catalog key {key}.");
        }

        foreach (var literal in new[]
                 {
                     "\"Install\"",
                     "\"Not now\"",
                     "\"A new Jularr version is ready.\"",
                     "\"Back online.\"",
                     "\"Link copied.\""
                 })
        {
            Assert.IsFalse(
                pwaRuntime.Contains(literal, StringComparison.Ordinal),
                $"pwa.js still hard-codes {literal}.");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }
}
