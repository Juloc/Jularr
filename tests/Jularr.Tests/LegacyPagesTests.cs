using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jularr.Tests;

/// <summary>
/// Guards the legacy page subtree (docs/INFORMATION_ARCHITECTURE.md, "UI maturity classes"): pages moved to
/// <c>Pages/Legacy</c> keep their URL through an explicit absolute route, keep the namespace of the feature folder they
/// came from, resolve their partials by explicit path, and no feature page depends on a legacy partial.
/// </summary>
[TestClass]
public sealed partial class LegacyPagesTests
{
    private static readonly string WebRoot = Path.Combine(FindRepositoryRoot(), "src", "Jularr.Web");

    private static readonly string PagesRoot = Path.Combine(WebRoot, "Pages");

    private static readonly string LegacyRoot = Path.Combine(PagesRoot, "Legacy");

    // The URL contract of the legacy subtree. Adding a legacy page means adding its unchanged URL here.
    private static readonly Dictionary<string, string> ExpectedRoutes = new()
    {
        ["/Pages/Legacy/Admin/ReadingSources.cshtml"] = "Admin/ReadingSources",
        ["/Pages/Legacy/Admin/Sonarr.cshtml"] = "Admin/Sonarr",
        ["/Pages/Legacy/Admin/Subtitles.cshtml"] = "Admin/Subtitles",
        ["/Pages/Legacy/Franchises/Details.cshtml"] = "Franchises/{id:guid}",
        ["/Pages/Legacy/Library/AnimeRepair.cshtml"] = "Library/AnimeRepair/{id:guid}",
        ["/Pages/Legacy/Library/EpisodeSegments.cshtml"] = "Library/Episode/{id:guid}/segments",
        ["/Pages/Legacy/Library/Rename.cshtml"] = "Library/Rename/{id:guid}",
        ["/Pages/Legacy/Settings/Books.cshtml"] = "Settings/Books",
        ["/Pages/Legacy/Settings/DownloadClients/Edit.cshtml"] = "Settings/DownloadClients/Edit/{id:guid?}",
        ["/Pages/Legacy/Settings/Indexers/Edit.cshtml"] = "Settings/Indexers/Edit/{id:guid?}",
        ["/Pages/Legacy/Settings/Language.cshtml"] = "Settings/Language/Legacy",
        ["/Pages/Legacy/Settings/MappingReview.cshtml"] = "Settings/MappingReview",
        ["/Pages/Legacy/Settings/MappingSegments.cshtml"] = "Settings/MappingSegments",
        ["/Pages/Legacy/Settings/Naming.cshtml"] = "Settings/Naming",
        ["/Pages/Legacy/Settings/ReadingNaming.cshtml"] = "Settings/ReadingNaming",
        ["/Pages/Legacy/Settings/Subtitles.cshtml"] = "Settings/Subtitles",
        ["/Pages/Legacy/Watchlist/Index.cshtml"] = "Watchlist"
    };

    [TestMethod]
    public void EveryLegacyPageDeclaresAnExplicitAbsoluteRoute()
    {
        var pages = LegacyPageFiles();
        Assert.AreEqual(ExpectedRoutes.Count, pages.Count, "Every legacy page needs an entry in the URL contract above.");

        foreach (var file in pages)
        {
            var directive = PageDirective().Match(File.ReadAllText(file));
            Assert.IsTrue(directive.Success && directive.Groups["route"].Value.StartsWith('/'), $"{RelativePage(file)} must start with @page \"/original/route\".");
        }
    }

    [TestMethod]
    public void LegacyPagesKeepTheNamespaceOfTheirFeatureFolder()
    {
        foreach (var folder in Directory.EnumerateDirectories(LegacyRoot))
        {
            var feature = Path.GetFileName(folder);
            var imports = File.ReadAllText(Path.Combine(folder, "_ViewImports.cshtml"));
            StringAssert.Contains(imports, $"@namespace Jularr.Web.Pages.{feature}", $"Legacy/{feature} must pin its original namespace.");
        }

        foreach (var file in LegacyPageFiles())
        {
            var relative = Path.GetRelativePath(LegacyRoot, Path.GetDirectoryName(file)!).Replace(Path.DirectorySeparatorChar, '.');
            var expected = $"Jularr.Web.Pages.{relative}";
            var model = ModelDirective().Match(File.ReadAllText(file)).Groups["type"].Value;
            Assert.IsTrue(model.StartsWith(expected + ".", StringComparison.Ordinal), $"{RelativePage(file)} @model {model} must live in {expected}.");
            StringAssert.Contains(File.ReadAllText(file + ".cs"), $"namespace {expected};", $"{RelativePage(file)}.cs must keep the namespace {expected}.");
        }
    }

    [TestMethod]
    public void LegacyPagesResolveEveryPartialTheyUse()
    {
        foreach (var file in LegacyPageFiles())
        {
            foreach (var name in PartialName().Matches(File.ReadAllText(file)).Select(match => match.Groups["name"].Value).Distinct())
            {
                Assert.IsTrue(PartialExists(file, name), $"{RelativePage(file)} uses the partial '{name}', which view lookup cannot find from the legacy folder.");
            }
        }
    }

    [TestMethod]
    public void NoPageOutsideLegacyReferencesALegacyPartial()
    {
        foreach (var file in Directory.EnumerateFiles(PagesRoot, "*.cshtml", SearchOption.AllDirectories).Where(file => !IsUnderLegacy(file)))
        {
            var references = PartialName().Matches(File.ReadAllText(file)).Select(match => match.Groups["name"].Value).Where(name => name.Contains("/Legacy/", StringComparison.Ordinal));
            Assert.AreEqual(0, references.Count(), $"{Path.GetRelativePath(PagesRoot, file)} must not depend on a legacy partial.");
        }
    }

    [TestMethod]
    public void EveryLiteralPageReferenceResolvesToARealPage()
    {
        var unresolved = new List<string>();
        var sources = Directory.EnumerateFiles(WebRoot, "*.*", SearchOption.AllDirectories)
            .Where(file => (file.EndsWith(".cshtml", StringComparison.Ordinal) || file.EndsWith(".cs", StringComparison.Ordinal)) && !IsBuildOutput(file));
        foreach (var file in sources)
        {
            foreach (Match reference in PageReference().Matches(File.ReadAllText(file)))
            {
                var page = reference.Groups["page"].Value;
                if (!File.Exists(Path.Combine(PagesRoot, page.TrimStart('/') + ".cshtml")))
                {
                    unresolved.Add($"{Path.GetRelativePath(WebRoot, file)} -> {page}");
                }
            }
        }

        Assert.AreEqual(0, unresolved.Count, "Page names follow the folder, so every asp-page/RedirectToPage/Url.Page literal must name an existing page:" + Environment.NewLine + string.Join(Environment.NewLine, unresolved));
    }

    [TestMethod]
    public async Task LegacyPagesAnswerTheirOriginalUrlsAndNeverExposeTheLegacyFolder()
    {
        var endpoints = await DiscoverPageEndpointsAsync();

        Assert.IsFalse(endpoints.Any(endpoint => endpoint.Route.TrimStart('/').StartsWith("Legacy", StringComparison.OrdinalIgnoreCase)), "The Legacy folder is never part of a URL.");

        var legacy = endpoints.Where(endpoint => endpoint.RelativePath.StartsWith("/Pages/Legacy/", StringComparison.Ordinal)).ToArray();
        CollectionAssert.AreEquivalent(ExpectedRoutes.Keys.ToArray(), legacy.Select(endpoint => endpoint.RelativePath).ToArray(), "Every legacy page serves exactly one route.");
        foreach (var endpoint in legacy)
        {
            Assert.AreEqual(ExpectedRoutes[endpoint.RelativePath], endpoint.Route.TrimStart('/'), $"{endpoint.RelativePath} changed its URL.");
        }
    }

    private static async Task<IReadOnlyList<(string Route, string RelativePath)>> DiscoverPageEndpointsAsync()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .UseContentRoot(WebRoot)
                .ConfigureServices(services =>
                {
                    services.AddRazorPages().AddApplicationPart(typeof(Jularr.Web.Pages.Library.AnimeModel).Assembly);
                    services.AddAuthorization();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapRazorPages());
                }))
            .StartAsync();

        return host.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => (endpoint.RoutePattern.RawText!, endpoint.Metadata.GetMetadata<PageActionDescriptor>()!.RelativePath))
            .ToArray();
    }

    private static List<string> LegacyPageFiles()
    {
        return Directory.EnumerateFiles(LegacyRoot, "*.cshtml", SearchOption.AllDirectories)
            .Where(file => !Path.GetFileName(file).StartsWith('_'))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static bool PartialExists(string pageFile, string name)
    {
        if (name.StartsWith("~/", StringComparison.Ordinal) || name.StartsWith('/'))
        {
            return File.Exists(Path.Combine(WebRoot, name.TrimStart('~', '/').Replace('/', Path.DirectorySeparatorChar)));
        }

        // Razor Pages look a partial up in the page folder, then in every parent up to Pages, then in Pages/Shared.
        for (var directory = Path.GetDirectoryName(pageFile); directory is not null && directory.Length >= PagesRoot.Length; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, name + ".cshtml")))
            {
                return true;
            }
        }

        return File.Exists(Path.Combine(PagesRoot, "Shared", name + ".cshtml"));
    }

    private static bool IsUnderLegacy(string file) => file.StartsWith(LegacyRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static bool IsBuildOutput(string file) => file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string RelativePage(string file) => Path.GetRelativePath(PagesRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the Jularr repository root.");
    }

    [GeneratedRegex(@"^@page(?<template>[ \t]+""(?<route>[^""]*)"")?", RegexOptions.Multiline)]
    private static partial Regex PageDirective();

    [GeneratedRegex(@"^@model[ \t]+(?<type>[\w.]+)", RegexOptions.Multiline)]
    private static partial Regex ModelDirective();

    [GeneratedRegex(@"<partial\s+name=""(?<name>[^""]+)""")]
    private static partial Regex PartialName();

    [GeneratedRegex(@"(?:asp-page=|RedirectToPage(?:Permanent)?\(|Url\.Page\(|PageLink\()""(?<page>/[A-Za-z0-9_/]+)""")]
    private static partial Regex PageReference();
}
