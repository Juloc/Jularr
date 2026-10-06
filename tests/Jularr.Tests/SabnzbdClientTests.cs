using Jularr.Web.Features.Acquisition.Sabnzbd;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Tests;

[TestClass]
public sealed class SabnzbdClientTests
{
    [TestMethod]
    public async Task SettingsStoreProtectsApiKeyAtRest()
    {
        var directory = SabnzbdTestSupport.CreateTemporaryDirectory();
        try
        {
            var provider = new EphemeralDataProtectionProvider();
            var store = new SabnzbdSettingsStore(provider, directory);
            await store.SaveAsync(
                new SabnzbdStoredSettings(
                    "http://sabnzbd:8080/",
                    "secret-key",
                    " books ",
                    "anime"));

            var raw = await File.ReadAllTextAsync(
                Path.Combine(directory.FullName, SabnzbdSettingsStore.FileName));

            Assert.IsFalse(raw.Contains("secret-key", StringComparison.Ordinal));

            var loaded = await store.LoadAsync();
            Assert.AreEqual("secret-key", loaded.ApiKey);
            Assert.AreEqual("http://sabnzbd:8080", loaded.BaseUrl);
            Assert.AreEqual("books", loaded.BooksCategory);
            Assert.AreEqual("anime", loaded.AnimeCategory);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task ResolverAppliesCanonicalConfigurationOverridesPerField()
    {
        var directory = SabnzbdTestSupport.CreateTemporaryDirectory();
        try
        {
            var store = new SabnzbdSettingsStore(new EphemeralDataProtectionProvider(), directory);
            await store.SaveAsync(
                new SabnzbdStoredSettings("http://stored:8080", "stored-key", "books", null));

            var resolver = new SabnzbdConnectionResolver(
                store,
                SabnzbdTestSupport.Configuration(new Dictionary<string, string?>
                {
                    [SabnzbdConfigurationKeys.ApiKey] = "env-key",
                    [SabnzbdConfigurationKeys.AnimeCategory] = "tv-anime"
                }));

            var resolved = await resolver.ResolveAsync();

            Assert.IsNotNull(resolved.Connection);
            Assert.AreEqual("http://stored:8080", resolved.Connection.Settings.BaseUrl);
            Assert.AreEqual("env-key", resolved.Connection.ApiKey);
            Assert.AreEqual("books", resolved.Connection.Settings.CategoryFor(SabnzbdPurpose.Books));
            Assert.AreEqual("tv-anime", resolved.Connection.Settings.CategoryFor(SabnzbdPurpose.Anime));
            Assert.IsTrue(resolved.ApiKeyFromConfiguration);
            Assert.IsFalse(resolved.BaseUrlFromConfiguration);
            Assert.AreEqual("stored-key", resolved.Stored.ApiKey);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task RequestsKeepApiKeyOutOfTheUrl()
    {
        var handler = new RecordingHandler(
            """{"version":"4.5.3"}""",
            """{"queue":{"slots":[]}}""");
        var client = new SabnzbdClient(new HttpClient(handler));

        var result = await client.TestAsync(
            SabnzbdTestSupport.Connection(),
            CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.CanMonitor);
        Assert.AreEqual("4.5.3", result.Version);
        Assert.AreEqual(2, handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("http://sabnzbd:8080/api", request.Uri);
            Assert.IsFalse(request.Uri.Contains("secret-key", StringComparison.Ordinal));
            StringAssert.Contains(request.Body, "apikey=secret-key");
            StringAssert.Contains(request.Body, "output=json");
        }

        StringAssert.Contains(handler.Requests[0].Body, "mode=version");
        StringAssert.Contains(handler.Requests[1].Body, "mode=queue");
    }

    [TestMethod]
    public async Task TestReportsNzbOnlyKeyAsUnableToMonitor()
    {
        var client = new SabnzbdClient(
            new HttpClient(
                new RecordingHandler(
                    """{"version":"4.5.3"}""",
                    """{"status":false,"error":"API Key Incorrect"}""")));

        var result = await client.TestAsync(
            SabnzbdTestSupport.Connection(),
            CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.IsFalse(result.CanMonitor);
        StringAssert.Contains(result.Error, "API Key Incorrect");
        StringAssert.Contains(result.Error, "full API key");
    }

    [TestMethod]
    public async Task GrabAddsUrlWithPurposeCategory()
    {
        var handler = new RecordingHandler(
            """{"status":true,"nzo_ids":["SABnzbd_nzo_123"]}""");
        var client = new SabnzbdClient(new HttpClient(handler));
        var connection = SabnzbdTestSupport.Connection();

        var result = await client.GrabAsync(
            connection,
            new SabnzbdGrabRequest(
                new Uri("https://prowlarr.example/download?id=123"),
                "Anime - 01",
                connection.Settings.CategoryFor(SabnzbdPurpose.Anime)),
            CancellationToken.None);

        Assert.IsTrue(result.Success);
        CollectionAssert.AreEqual(
            new[] { "SABnzbd_nzo_123" },
            result.NzoIds.ToArray());
        var body = Uri.UnescapeDataString(handler.Requests[0].Body.Replace('+', ' '));
        StringAssert.Contains(body, "mode=addurl");
        StringAssert.Contains(body, "cat=anime");
        StringAssert.Contains(body, "nzbname=Anime - 01");
        StringAssert.Contains(body, "name=https://prowlarr.example/download?id=123");
    }

    [TestMethod]
    public async Task GrabReportsRejectionMessage()
    {
        var client = new SabnzbdClient(
            new HttpClient(
                new RecordingHandler("""{"status":false,"error":"Invalid NZB"}""")));

        var result = await client.GrabAsync(
            SabnzbdTestSupport.Connection(),
            new SabnzbdGrabRequest(new Uri("https://indexer.example/a.nzb")),
            CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("Invalid NZB", result.Error);
    }

    [TestMethod]
    public async Task AddFileUploadsMultipartNzbWithCategory()
    {
        var handler = new RecordingHandler(
            """{"status":true,"nzo_ids":["SABnzbd_nzo_file"]}""");
        var client = new SabnzbdClient(new HttpClient(handler));
        await using var nzb = new MemoryStream("<nzb/>"u8.ToArray());

        var result = await client.AddFileAsync(
            SabnzbdTestSupport.Connection(),
            nzb,
            "book.nzb",
            "books",
            CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("SABnzbd_nzo_file", result.NzoIds.Single());
        StringAssert.Contains(handler.Requests[0].Body, "addfile");
        StringAssert.Contains(handler.Requests[0].Body, "book.nzb");
        StringAssert.Contains(handler.Requests[0].Body, "books");
        Assert.IsFalse(handler.Requests[0].Uri.Contains("secret-key", StringComparison.Ordinal));
    }

    [TestMethod]
    public void QueueParserHandlesStringMetricsSpeedAndTimeLeft()
    {
        const string json = """
        {
          "queue": {
            "paused": false,
            "kbpersec": "512.5",
            "timeleft": "0:01:20",
            "slots": [
              {
                "nzo_id": "SABnzbd_nzo_1",
                "filename": "Anime - 01",
                "status": "Downloading",
                "cat": "anime",
                "percentage": "52.4",
                "timeleft": "1:02:03:04",
                "mb": "1000",
                "mbleft": "476"
              }
            ]
          }
        }
        """;

        var queue = SabnzbdClient.ParseQueueResponse(json);

        Assert.IsFalse(queue.Paused);
        Assert.AreEqual(512.5 * 1024d, queue.BytesPerSecond);
        Assert.AreEqual(TimeSpan.FromSeconds(80), queue.TimeLeft);
        Assert.AreEqual(1, queue.Jobs.Count);
        Assert.AreEqual(52.4, queue.Jobs[0].Percentage);
        Assert.AreEqual(new TimeSpan(1, 2, 3, 4), queue.Jobs[0].TimeLeft);
        Assert.AreEqual(1000L * 1024 * 1024, queue.Jobs[0].SizeBytes);
        Assert.AreEqual(476L * 1024 * 1024, queue.Jobs[0].SizeLeftBytes);
    }

    [TestMethod]
    public void HistoryParserNormalizesOutcomes()
    {
        const string json = """
        {
          "history": {
            "slots": [
              { "nzo_id": "done", "name": "Finished", "status": "Completed", "bytes": 10485760, "completed": 1790000000 },
              { "nzo_id": "pp", "name": "Unpacking", "status": "Extracting" },
              { "nzo_id": "pw", "name": "Passworded", "status": "Failed", "fail_message": "Encrypted archive requires a password" },
              { "nzo_id": "unpack", "name": "Broken", "status": "Failed", "fail_message": "Unpacking failed, archive requires a newer version" },
              { "nzo_id": "corrupt", "name": "Corrupt", "status": "Failed", "fail_message": "Repair failed, not enough repair blocks (12 short)" },
              { "nzo_id": "articles", "name": "Incomplete", "status": "Failed", "fail_message": "Download failed - Out of your server's retention?" },
              { "nzo_id": "unknown", "name": "Mystery", "status": "Failed" }
            ]
          }
        }
        """;

        var jobs = SabnzbdClient.ParseHistoryResponse(json).Jobs
            .ToDictionary(job => job.NzoId);

        Assert.IsTrue(jobs["done"].IsCompleted);
        Assert.AreEqual(10L * 1024 * 1024, jobs["done"].SizeBytes);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1790000000), jobs["done"].CompletedAt);

        Assert.IsFalse(jobs["pp"].IsCompleted);
        Assert.IsFalse(jobs["pp"].IsFailed);

        Assert.AreEqual(SabnzbdFailureKind.Password, jobs["pw"].FailureKind);
        Assert.AreEqual(SabnzbdFailureKind.Unpack, jobs["unpack"].FailureKind);
        Assert.AreEqual(SabnzbdFailureKind.Verification, jobs["corrupt"].FailureKind);
        Assert.AreEqual(SabnzbdFailureKind.Download, jobs["articles"].FailureKind);
        Assert.AreEqual(SabnzbdFailureKind.Unknown, jobs["unknown"].FailureKind);
        Assert.IsTrue(jobs.Values.Where(job => job.NzoId is not ("done" or "pp")).All(job => job.IsFailed));
    }

    [TestMethod]
    public void QueueErrorPayloadIsReported()
    {
        var exception = Assert.ThrowsExactly<System.Text.Json.JsonException>(() =>
            SabnzbdClient.ParseQueueResponse("""{"status":false,"error":"API Key Required"}"""));

        StringAssert.Contains(exception.Message, "API Key Required");
    }

    [TestMethod]
    public async Task CancelRetryAndHistoryDeleteUseSabJobId()
    {
        var handler = new RecordingHandler(
            """{"status":true,"nzo_ids":["job-1"]}""",
            """{"status":true}""",
            """{"status":true,"nzo_id":"job-2"}""");
        var client = new SabnzbdClient(new HttpClient(handler));

        var cancelled = await client.CancelAsync(
            SabnzbdTestSupport.Connection(),
            "job-1",
            deleteFiles: true,
            CancellationToken.None);
        var deleted = await client.DeleteHistoryAsync(
            SabnzbdTestSupport.Connection(),
            "job-1",
            deleteFiles: true,
            CancellationToken.None);
        var retried = await client.RetryAsync(
            SabnzbdTestSupport.Connection(),
            "job-1",
            CancellationToken.None);

        Assert.IsTrue(cancelled.Success);
        Assert.IsTrue(deleted.Success);
        Assert.IsTrue(retried.Success);
        Assert.AreEqual("job-2", retried.NewNzoId);
        StringAssert.Contains(handler.Requests[0].Body, "mode=queue");
        StringAssert.Contains(handler.Requests[0].Body, "name=delete");
        StringAssert.Contains(handler.Requests[0].Body, "del_files=1");
        StringAssert.Contains(handler.Requests[1].Body, "mode=history");
        StringAssert.Contains(handler.Requests[2].Body, "mode=retry");
        StringAssert.Contains(handler.Requests[2].Body, "value=job-1");
    }

    [TestMethod]
    public void JobIdsAreValidatedBeforeUse()
    {
        var client = new SabnzbdClient(new HttpClient(new RecordingHandler()));

        Assert.ThrowsExactly<ArgumentException>(() =>
            client.CancelAsync(
                SabnzbdTestSupport.Connection(),
                "job&apikey=x",
                deleteFiles: false,
                CancellationToken.None));
    }

    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> pending = new(responses);

        public List<(HttpMethod Method, string Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body));
            return SabnzbdTestSupport.JsonResponse(pending.Dequeue());
        }
    }
}
