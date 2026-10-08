using System.Net;
using Jularr.Web.Features.Acquisition.Core;
using System.Text;
using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Prowlarr;

namespace Jularr.Tests;

[TestClass]
public sealed class ProwlarrSearchTests
{
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

    private static AcquisitionCandidate Candidate(string guid)
    {
        var parsed = AnimeReleaseParser.Parse(
            "[Group] Anime - 01 WEB-DL 1080p AVC AAC");

        return new AcquisitionCandidate(
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
        Func<ProwlarrSearchQuery, CancellationToken, IReadOnlyList<AcquisitionCandidate>> search)
        : IProwlarrClient
    {
        public Task<ProwlarrConnectionTestResult> TestAsync(
            ProwlarrConnection connection,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProwlarrConnectionTestResult(true, "test"));

        public Task<IReadOnlyList<AcquisitionCandidate>> SearchAsync(
            ProwlarrConnection connection,
            ProwlarrSearchQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(search(query, cancellationToken));
    }
}

file static class AcquisitionCandidateTestExtensions
{
    public static int? AbsoluteEpisode(this AcquisitionCandidate candidate) =>
        candidate.ParsedRelease.AbsoluteEpisodeStart;
}
