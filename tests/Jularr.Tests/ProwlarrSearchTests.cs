using System.Net;
using System.Text;
using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Prowlarr;

namespace Jularr.Tests;

[TestClass]
public sealed class ProwlarrSearchTests
{
    [TestMethod]
    public void PlannerBuildsSeasonEpisodeAndAbsoluteQueriesForAliases()
    {
        var queries = ProwlarrSearchPlanner.Build(
            new ProwlarrAnimeSearchTarget(
                "Sousou no Frieren",
                ["Frieren: Beyond Journey's End", "Sousou no Frieren"],
                ProwlarrAnimeSearchMode.Episode,
                SeasonNumber: 2,
                EpisodeNumber: 3,
                AbsoluteEpisodeNumber: 31));

        CollectionAssert.Contains(
            queries.Select(item => item.Query).ToArray(),
            "Sousou no Frieren S02E03");
        CollectionAssert.Contains(
            queries.Select(item => item.Query).ToArray(),
            "Sousou no Frieren - 31");
        CollectionAssert.Contains(
            queries.Select(item => item.Query).ToArray(),
            "Frieren: Beyond Journey's End S02E03");
        Assert.AreEqual(
            queries.Count,
            queries.Select(item => item.Query).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [TestMethod]
    public void PlannerBuildsSeasonPackQueries()
    {
        var queries = ProwlarrSearchPlanner.Build(
            new ProwlarrAnimeSearchTarget(
                "Anime",
                [],
                ProwlarrAnimeSearchMode.Season,
                SeasonNumber: 2));

        CollectionAssert.AreEquivalent(
            new[] { "Anime S02", "Anime Season 2" },
            queries.Select(item => item.Query).ToArray());
    }

    [TestMethod]
    public async Task ClientUsesApiKeyHeaderAndConfiguredSearchFilters()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return JsonResponse("[]");
        });
        var client = new ProwlarrClient(new HttpClient(handler));
        var connection = Connection(
            categories: [5000, 5070],
            indexerIds: [4, 8]);

        await client.SearchAsync(
            connection,
            new ProwlarrSearchQuery("Anime S01E01"),
            CancellationToken.None);

        Assert.IsNotNull(captured);
        Assert.AreEqual("secret-key", captured.Headers.GetValues("X-Api-Key").Single());
        Assert.IsFalse(captured.RequestUri!.Query.Contains("secret-key", StringComparison.Ordinal));
        StringAssert.Contains(captured.RequestUri.Query, "query=Anime%20S01E01");
        StringAssert.Contains(captured.RequestUri.Query, "type=search");
        StringAssert.Contains(captured.RequestUri.Query, "categories=5000");
        StringAssert.Contains(captured.RequestUri.Query, "categories=5070");
        StringAssert.Contains(captured.RequestUri.Query, "indexerIds=4");
        StringAssert.Contains(captured.RequestUri.Query, "indexerIds=8");
    }

    [TestMethod]
    public async Task TestConnectionUsesAuthenticatedSystemStatus()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return JsonResponse("""{"version":"2.5.2.5491"}""");
        });
        var client = new ProwlarrClient(new HttpClient(handler));

        var result = await client.TestAsync(Connection(), CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("2.5.2.5491", result.Version);
        Assert.AreEqual(
            "https://prowlarr.example/api/v1/system/status",
            captured!.RequestUri!.ToString());
        Assert.AreEqual("secret-key", captured.Headers.GetValues("X-Api-Key").Single());
    }

    [TestMethod]
    public void ParsesNormalizedReleaseWithoutExposingDownloadUrlInJson()
    {
        const string json = """
        [
          {
            "guid": "abc-123",
            "title": "[SubsPlease] Sousou no Frieren - 03 (1080p) [ABCDEF12]",
            "indexer": "Example Indexer",
            "indexerId": 7,
            "protocol": "torrent",
            "size": 1234567890,
            "seeders": 44,
            "leechers": 3,
            "publishDate": "2026-09-25T12:30:00Z",
            "age": 0,
            "ageHours": 4.5,
            "downloadUrl": "/api/v1/indexer/7/download?link=abc&apikey=should-not-leak",
            "magnetUrl": "magnet:?xt=urn:btih:abc",
            "infoUrl": "https://example.invalid/release/abc"
          }
        ]
        """;

        var releases = ProwlarrClient.ParseSearchResponse(
            "https://prowlarr.example",
            "Sousou no Frieren - 03",
            json);

        var release = releases.Single();
        Assert.AreEqual("Example Indexer", release.Indexer);
        Assert.AreEqual(7, release.IndexerId);
        Assert.AreEqual(44, release.Seeders);
        Assert.AreEqual(3, release.AbsoluteEpisode());
        Assert.AreEqual(
            "https://prowlarr.example/api/v1/indexer/7/download?link=abc&apikey=should-not-leak",
            release.InternalDownloadUri!.ToString());

        var serialized = System.Text.Json.JsonSerializer.Serialize(release);
        Assert.IsFalse(serialized.Contains("should-not-leak", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("magnet:?xt", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MalformedEntriesDoNotBreakOtherSearchResults()
    {
        const string json = """
        [
          {"indexer":"Broken"},
          {"title":"Anime - 01 WEB-DL 1080p AVC AAC","indexer":"Good","indexerId":2}
        ]
        """;

        var releases = ProwlarrClient.ParseSearchResponse(
            "http://prowlarr:9696",
            "Anime 01",
            json);

        Assert.AreEqual(1, releases.Count);
        Assert.AreEqual("Good", releases[0].Indexer);
        Assert.AreEqual(1, releases[0].ParsedRelease.AbsoluteEpisodeStart);
    }

    [TestMethod]
    public async Task CoordinatorDeduplicatesAcrossAliasesAndPreservesMatchedQueries()
    {
        var candidate = Candidate("same-guid");
        var client = new FakeProwlarrClient(
            (_, _) => [candidate]);
        var service = new ProwlarrAnimeSearchService(client);

        var result = await service.SearchAsync(
            Connection(),
            new ProwlarrAnimeSearchTarget(
                "Anime",
                ["Anime English"],
                ProwlarrAnimeSearchMode.Episode,
                SeasonNumber: 1,
                EpisodeNumber: 1),
            CancellationToken.None);

        Assert.AreEqual(1, result.Releases.Count);
        Assert.IsTrue(result.Releases[0].MatchedQueries.Count >= 2);
        Assert.AreEqual(0, result.Warnings.Count);
    }

    [TestMethod]
    public async Task CoordinatorKeepsSuccessfulQueriesWhenAnotherQueryFails()
    {
        var client = new FakeProwlarrClient(
            (query, _) =>
            {
                if (query.Query.Contains("English", StringComparison.Ordinal))
                {
                    throw new ProwlarrException("Indexer search failed.");
                }

                return [Candidate("ok-guid")];
            });
        var service = new ProwlarrAnimeSearchService(client);

        var result = await service.SearchAsync(
            Connection(),
            new ProwlarrAnimeSearchTarget(
                "Anime",
                ["Anime English"],
                ProwlarrAnimeSearchMode.Anime),
            CancellationToken.None);

        Assert.AreEqual(1, result.Releases.Count);
        Assert.AreEqual(1, result.Warnings.Count);
        StringAssert.Contains(result.Warnings[0].Query, "English");
    }

    [TestMethod]
    public void SettingsRejectCredentialsEmbeddedInBaseUrl()
    {
        var settings = ProwlarrSettings.CreateDefault(
            "http://user:password@prowlarr:9696");

        Assert.ThrowsExactly<ArgumentException>(() =>
            ProwlarrClient.NormalizeAndValidate(settings));
    }

    private static ProwlarrConnection Connection(
        int[]? categories = null,
        int[]? indexerIds = null) =>
        new(
            new ProwlarrSettings(
                "https://prowlarr.example",
                categories ?? [5000, 5070],
                indexerIds ?? [],
                100),
            "secret-key");

    private static ProwlarrReleaseCandidate Candidate(string guid)
    {
        var parsed = AnimeReleaseParser.Parse(
            "[Group] Anime - 01 WEB-DL 1080p AVC AAC");

        return new ProwlarrReleaseCandidate(
            parsed.RawTitle,
            "Indexer",
            1,
            "usenet",
            1000,
            10,
            0,
            DateTimeOffset.UtcNow,
            0,
            1,
            guid,
            null,
            parsed,
            [],
            new Uri("https://prowlarr.example/download"),
            null);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json")
        };

    private static DirectoryInfo CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"jularr-prowlarr-{Guid.NewGuid():N}");
        return Directory.CreateDirectory(path);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class FakeProwlarrClient(
        Func<ProwlarrSearchQuery, CancellationToken, IReadOnlyList<ProwlarrReleaseCandidate>> search)
        : IProwlarrClient
    {
        public Task<ProwlarrConnectionTestResult> TestAsync(
            ProwlarrConnection connection,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProwlarrConnectionTestResult(true, "test"));

        public Task<IReadOnlyList<ProwlarrReleaseCandidate>> SearchAsync(
            ProwlarrConnection connection,
            ProwlarrSearchQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(search(query, cancellationToken));
    }
}

file static class ProwlarrReleaseCandidateTestExtensions
{
    public static int? AbsoluteEpisode(this ProwlarrReleaseCandidate candidate) =>
        candidate.ParsedRelease.AbsoluteEpisodeStart;
}
