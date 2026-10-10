using System.Net;
using System.Security.Claims;
using System.Text;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.ExternalPlayback.Plex;
using Jularr.Web.Features.MediaCore;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Tests;

[TestClass]
public sealed class PlexCatalogReconciliationTests
{
    [TestMethod]
    public async Task ResumesInterruptedCatalogPagesWithoutStoringPlexTokens()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"jularr-plex-reconcile-{Guid.NewGuid():N}");
        try
        {
            await using var db = await MediaCoreTestSupport.CreateDbAsync();
            var works = new WorkService(db);
            var work = await works.CreateWorkAsync(
                WorkMediaType.Movie, "Dune", 2021, CancellationToken.None);
            await works.LinkExternalIdentityAsync(
                work.Id, WorkMediaType.Movie, "tmdb", "438631",
                1, "verified", true, false,
                MappingReviewState.Confirmed, CancellationToken.None);

            var scanned = new List<int>();
            var failPageOne = true;
            using var http = new HttpClient(new Handler(request =>
            {
                Assert.AreEqual("server-secret-token",
                    request.Headers.GetValues("X-Plex-Token").Single());

                var uri = request.RequestUri!;
                if (uri.AbsolutePath == "/identity")
                {
                    return Json(
                        """{"MediaContainer":{"machineIdentifier":"plex-machine-1234"}}""");
                }

                if (uri.AbsolutePath == "/library/sections")
                {
                    return Json(
                        """{"MediaContainer":{"Directory":[{"key":"1","type":"movie","title":"Movies"}]}}""");
                }

                Assert.AreEqual("/library/sections/1/all", uri.AbsolutePath);
                var start = uri.Query.Contains("Start=0&", StringComparison.Ordinal) ? 0 :
                    uri.Query.Contains("Start=1&", StringComparison.Ordinal) ? 1 : 2;
                scanned.Add(start);
                if (start == 1 && failPageOne)
                {
                    failPageOne = false;
                    throw new HttpRequestException("Temporary Plex network error.");
                }

                return start switch
                {
                    0 => Json(
                        """{"MediaContainer":{"totalSize":3,"Metadata":[{"ratingKey":"44","type":"movie","title":"Dune","Guid":[{"id":"tmdb://438631"}]}]}}"""),
                    1 => Json(
                        """{"MediaContainer":{"totalSize":3,"Metadata":[{"ratingKey":"45","type":"movie","title":"Unknown"}]}}"""),
                    _ => Json(
                        """{"MediaContainer":{"totalSize":3,"Metadata":[{"ratingKey":"46","type":"movie","title":"Unknown 2"}]}}""")
                };
            }));
            var client = new PlexLibraryClient(http);
            var grants = new PlexServerGrantStore(
                new EphemeralDataProtectionProvider(), TimeProvider.System,
                Path.Combine(root, "grants"));
            var server = new PlexServerCandidate(
                "plex-machine-1234", "Home", true, "server-secret-token",
                [new PlexServerConnection(
                    new Uri("https://server.plex.direct:32400/"), true, false)]);
            var owner = Principal(AccountRole.Owner);
            await new PlexServerSelectionService(client, grants).ApproveAsync(
                owner, server, server.Connections[0].Url, ["1"],
                "client-instance", CancellationToken.None);

            var disk = Path.Combine(root, "catalog");
            var checkpoints = new PlexCatalogCheckpointStore(
                TimeProvider.System, disk);
            var reconciler = new PlexCatalogReconciliationService(
                grants,
                new PlexServerCatalogScanService(
                    grants, client, new PlexWorkMatcher(db)),
                checkpoints);

            var first = await reconciler.RunBatchAsync(
                owner, server.MachineIdentifier, "1", "client-instance",
                pageSize: 1, maxPages: 1);
            Assert.AreEqual(1, first.PagesProcessed);
            Assert.AreEqual(1, first.Checkpoint.NextStart);
            Assert.AreEqual(work.Id, (await checkpoints.ReadPageAsync(
                first.Checkpoint, 0)).Single().WorkId);

            await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
                reconciler.RunBatchAsync(
                    owner, server.MachineIdentifier, "1", "client-instance",
                    pageSize: 1, maxPages: 2));
            Assert.AreEqual(1, (await checkpoints.GetAsync(
                server.MachineIdentifier, "1"))?.NextStart);

            var afterRestart = new PlexCatalogReconciliationService(
                grants, new PlexServerCatalogScanService(
                    grants, client, new PlexWorkMatcher(db)),
                new PlexCatalogCheckpointStore(TimeProvider.System, disk));
            var final = await afterRestart.RunBatchAsync(
                owner, server.MachineIdentifier, "1", "client-instance",
                pageSize: 1, maxPages: 2);
            Assert.IsTrue(final.Checkpoint.Complete);
            Assert.AreEqual(3, final.Checkpoint.PagesScanned);
            Assert.AreEqual(2, final.PagesProcessed);
            CollectionAssert.AreEqual(
                new[] { 0, 1, 1, 2 }, scanned);

            var noRescan = await afterRestart.RunBatchAsync(
                owner, server.MachineIdentifier, "1", "client-instance",
                pageSize: 1);
            Assert.AreEqual(0, noRescan.PagesProcessed);
            var files = Directory.GetFiles(disk, "*.json",
                SearchOption.AllDirectories);
            Assert.IsTrue(files.Length >= 4);
            foreach (var file in files)
            {
                var contents = await File.ReadAllTextAsync(file);
                Assert.IsFalse(contents.Contains(
                    "server-secret-token", StringComparison.Ordinal));
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ScanningUnapprovedScopeIsDeniedWithoutWritingCheckpoints()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"jularr-plex-reconcile-{Guid.NewGuid():N}");
        try
        {
            await using var db = await MediaCoreTestSupport.CreateDbAsync();
            using var http = new HttpClient(new Handler(_ =>
                throw new AssertFailedException(
                    "Unauthorized scan must never call Plex.")));
            var grants = new PlexServerGrantStore(
                new EphemeralDataProtectionProvider(), TimeProvider.System,
                root);
            var checkpoints = new PlexCatalogCheckpointStore(
                TimeProvider.System, Path.Combine(root, "catalog"));
            var service = new PlexCatalogReconciliationService(
                grants,
                new PlexServerCatalogScanService(
                    grants, new PlexLibraryClient(http), new PlexWorkMatcher(db)),
                checkpoints);

            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                service.RunBatchAsync(
                    Principal(AccountRole.User), "machine-123456",
                    "1", "instance"));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                service.RunBatchAsync(
                    Principal(AccountRole.Owner), "machine-123456",
                    "1", "instance"));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "catalog")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task UpdatedServerGrantRestartsScanAndRetiresPreviousSnapshots()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"jularr-plex-reconcile-reset-{Guid.NewGuid():N}");
        try
        {
            var store = new PlexCatalogCheckpointStore(TimeProvider.System, root);
            var firstGrant = DateTimeOffset.Parse("2026-10-09T10:00:00Z");
            var started = await store.BeginAsync(
                "plex-machine-1234", "1", firstGrant, 100, false);
            var item = new PlexLibraryItem(
                "44", "movie", "Dune", 2021, []);
            var page = new PlexCatalogScanPage(
                "plex-machine-1234", "1", 0, 1, null,
                [new PlexWorkMatch(item, 123)]);
            var complete = await store.CommitAsync(started, page);

            Assert.IsTrue(complete.Complete);
            Assert.AreEqual(123L,
                (await store.ReadPageAsync(complete, 0)).Single().WorkId);
            var unchanged = await store.BeginAsync(
                "plex-machine-1234", "1", firstGrant, 100, false);
            Assert.AreEqual(complete.Generation, unchanged.Generation);

            var newer = await store.BeginAsync(
                "plex-machine-1234", "1",
                firstGrant.AddMinutes(10), 100, false);
            Assert.AreNotEqual(complete.Generation, newer.Generation);
            Assert.AreEqual(0, newer.NextStart);
            Assert.IsFalse(newer.Complete);
            Assert.AreEqual(0, (await store.ReadPageAsync(
                complete, 0)).Count);
            Assert.IsFalse(Directory.Exists(
                Path.Combine(root, "plex-machine-1234", "1",
                    complete.Generation.ToString("N"))));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ClaimsPrincipal Principal(AccountRole role) =>
        OwnerAuthService.CreatePrincipal(new OwnerAccount
        {
            Id = Guid.NewGuid().ToString("N"),
            UserName = role.ToString(),
            Role = role,
            IsEnabled = true
        });

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                content, Encoding.UTF8, "application/json")
        };

    private sealed class Handler(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
