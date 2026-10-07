using System.Text.Json;
using Jularr.Web.Features.Tracking;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class AniListProfileIsolationTests
{
    [TestMethod]
    public async Task StoresAniListAccountsPerProfileAsync()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = CreateStore(directory);
            var owner = Account(42, "owner-anilist", "owner-token");
            var learner = Account(77, "learner-anilist", "learner-token");

            await store.SaveAsync("owner", owner, CancellationToken.None);
            await store.SaveAsync("learner-1", learner, CancellationToken.None);

            var loadedOwner = await store.LoadAsync("owner", CancellationToken.None);
            var loadedLearner = await store.LoadAsync("learner-1", CancellationToken.None);

            Assert.IsNotNull(loadedOwner);
            Assert.IsNotNull(loadedLearner);
            Assert.AreEqual(owner.ViewerId, loadedOwner.ViewerId);
            Assert.AreEqual(owner.AccessToken, loadedOwner.AccessToken);
            Assert.AreEqual(learner.ViewerId, loadedLearner.ViewerId);
            Assert.AreEqual(learner.AccessToken, loadedLearner.AccessToken);

            Assert.IsTrue(File.Exists(Path.Combine(
                directory.FullName,
                "anilist",
                "accounts",
                "owner.json")));
            Assert.IsTrue(File.Exists(Path.Combine(
                directory.FullName,
                "anilist",
                "accounts",
                "learner-1.json")));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public async Task LegacyServerWideConnectionMigratesOnlyToOwnerAsync()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var provider = new EphemeralDataProtectionProvider();
            var protector = provider.CreateProtector("Jularr.AniList.AccessToken.v1");
            var connectedAt = new DateTimeOffset(
                2026,
                9,
                22,
                12,
                0,
                0,
                TimeSpan.Zero);

            var legacy = new
            {
                ClientId = 12345,
                ViewerId = 42,
                ViewerName = "legacy-owner",
                ViewerAvatarUrl = (string?)null,
                ProtectedAccessToken = protector.Protect("legacy-owner-token"),
                ConnectedAt = connectedAt,
                TokenExpiresAt = (DateTimeOffset?)null
            };

            var legacyPath = Path.Combine(directory.FullName, "anilist.json");
            await File.WriteAllTextAsync(
                legacyPath,
                JsonSerializer.Serialize(
                    legacy,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        WriteIndented = true
                    }));

            var store = new AniListAccountStore(
                provider,
                NullLogger<AniListAccountStore>.Instance,
                directory);

            Assert.IsNull(await store.LoadAsync(
                "learner-1",
                CancellationToken.None));
            Assert.IsTrue(File.Exists(legacyPath));

            var owner = await store.LoadAsync(
                "owner",
                CancellationToken.None);

            Assert.IsNotNull(owner);
            Assert.AreEqual(42, owner.ViewerId);
            Assert.AreEqual("legacy-owner-token", owner.AccessToken);
            Assert.IsFalse(File.Exists(legacyPath));
            Assert.IsTrue(File.Exists(Path.Combine(
                directory.FullName,
                "anilist",
                "accounts",
                "owner.json")));

            Assert.IsNull(await store.LoadAsync(
                "learner-1",
                CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public async Task ProgressBackupsIdentifyLocalProfileAsync()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = CreateStore(directory);
            await store.AppendProgressBackupAsync(
                new AniListProgressBackup(
                    "learner-1",
                    DateTimeOffset.UtcNow,
                    "learner-anilist",
                    4,
                    Remote()),
                CancellationToken.None);

            var path = Path.Combine(
                directory.FullName,
                "anilist-progress-backups.ndjson");
            var line = (await File.ReadAllLinesAsync(path)).Single();

            using var document = JsonDocument.Parse(line);
            Assert.AreEqual(
                "learner-1",
                document.RootElement.GetProperty("profileId").GetString());
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static AniListAccountStore CreateStore(DirectoryInfo directory) =>
        new(
            new EphemeralDataProtectionProvider(),
            NullLogger<AniListAccountStore>.Instance,
            directory);

    private static StoredAniListAccount Account(
        int viewerId,
        string viewerName,
        string token) =>
        new(
            ClientId: 12345,
            ViewerId: viewerId,
            ViewerName: viewerName,
            ViewerAvatarUrl: null,
            AccessToken: token,
            ConnectedAt: DateTimeOffset.UtcNow,
            TokenExpiresAt: null);

    private static AniListRemoteListEntry Remote() =>
        new(
            Id: 123,
            UserId: 77,
            MediaId: 999,
            Status: "CURRENT",
            Progress: 3,
            Score: null,
            Repeat: 0,
            Priority: 0,
            Private: false,
            Notes: null,
            HiddenFromStatusLists: false,
            CustomLists: null,
            AdvancedScores: null,
            StartedAt: null,
            CompletedAt: null,
            UpdatedAt: null);

    private static DirectoryInfo CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "jularr-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new DirectoryInfo(path);
    }

    private static void DeleteTemporaryDirectory(DirectoryInfo directory)
    {
        if (directory.Exists)
        {
            directory.Delete(recursive: true);
        }
    }
}
