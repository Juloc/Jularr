using Microsoft.AspNetCore.DataProtection;
using Jularr.Web.Features.ExternalPlayback.Plex;

namespace Jularr.Tests;

[TestClass]
public sealed class PlexProfileConnectionTests
{
    [TestMethod]
    public async Task ExplicitProfileConnectionStoresEncryptedTokenWithoutSync()
    {
        var root = NewDirectory();
        try
        {
            var protection = new EphemeralDataProtectionProvider();
            var store = new PlexProfileConnectionStore(protection, root);

            Assert.IsNull(await store.GetStatusAsync("profile-one"));
            await store.SaveVerifiedAsync(
                "profile-one", "123456", "Test Viewer",
                "temporary-verified-token");

            var status = await store.GetStatusAsync("profile-one");
            Assert.IsNotNull(status);
            Assert.AreEqual("123456", status.PlexUserId);
            Assert.AreEqual("Test Viewer", status.PlexUserName);
            Assert.IsTrue(status.IsUsable);
            Assert.IsFalse(status.SyncEnabled);

            var persisted = await File.ReadAllTextAsync(
                Path.Combine(root, "profile-one.json"));
            Assert.IsFalse(persisted.Contains(
                "temporary-verified-token", StringComparison.Ordinal));

            var reloaded = new PlexProfileConnectionStore(protection, root);
            Assert.IsTrue((await reloaded.GetStatusAsync("profile-one"))!.IsUsable);
            Assert.IsNull(await reloaded.GetStatusAsync("profile-two"));
        }
        finally
        {
            Remove(root);
        }
    }

    [TestMethod]
    public async Task ReconnectAndDisconnectRemainScopedToOneProfile()
    {
        var root = NewDirectory();
        try
        {
            var store = new PlexProfileConnectionStore(
                new EphemeralDataProtectionProvider(), root);
            await store.SaveVerifiedAsync(
                "profile-one", "100", null, "first-token");
            await store.SaveVerifiedAsync(
                "profile-two", "200", null, "second-token");
            await store.SaveVerifiedAsync(
                "profile-one", "101", null, "replacement-token");

            Assert.AreEqual("101",
                (await store.GetStatusAsync("profile-one"))?.PlexUserId);
            Assert.AreEqual("200",
                (await store.GetStatusAsync("profile-two"))?.PlexUserId);
            Assert.IsTrue(await store.DisconnectAsync("profile-one"));
            Assert.IsFalse(await store.DisconnectAsync("profile-one"));
            Assert.IsNull(await store.GetStatusAsync("profile-one"));
            Assert.IsTrue((await store.GetStatusAsync("profile-two"))!.IsUsable);
        }
        finally
        {
            Remove(root);
        }
    }

    [TestMethod]
    public async Task LostEncryptionKeysRequireMediaReconnection()
    {
        var root = NewDirectory();
        try
        {
            var store = new PlexProfileConnectionStore(
                new EphemeralDataProtectionProvider(), root);
            await store.SaveVerifiedAsync(
                "profile-one", "100", null, "verified-secret");

            var restored = new PlexProfileConnectionStore(
                new EphemeralDataProtectionProvider(), root);
            var status = await restored.GetStatusAsync("profile-one");

            Assert.IsNotNull(status);
            Assert.IsFalse(status.IsUsable);
            Assert.IsFalse(status.SyncEnabled);
            Assert.AreEqual("100", status.PlexUserId);
        }
        finally
        {
            Remove(root);
        }
    }

    [TestMethod]
    public async Task InvalidIdentityOrProfileCannotPersistAConnection()
    {
        var root = NewDirectory();
        try
        {
            var store = new PlexProfileConnectionStore(
                new EphemeralDataProtectionProvider(), root);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                store.SaveVerifiedAsync(
                    "../another-profile", "100", null, "secret"));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                store.SaveVerifiedAsync(
                    "profile-one", "invalid-id", null, "secret"));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                store.SaveVerifiedAsync(
                    "profile-one", "100", null,
                    "secret\r\nX-Plex-Token: invalid"));

            Assert.IsFalse(Directory.Exists(root));
        }
        finally
        {
            Remove(root);
        }
    }

    private static string NewDirectory() => Path.Combine(
        Path.GetTempPath(), $"jularr-plex-profile-{Guid.NewGuid():N}");

    private static void Remove(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
