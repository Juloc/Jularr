using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.MediaCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class PlexIdentityTests
{
    [TestMethod]
    public async Task ExternalAccount_ProvisionedWithoutPassword_CannotUseLocalLogin()
    {
        await WithDatabaseAsync(async (db, service) =>
        {
            var account = await service.CreateExternalAccountAsync(
                "PLEX", "42", "plex-viewer", isEnabled: true);

            Assert.AreEqual(AccountRole.User, account.Role);
            Assert.AreEqual(string.Empty, account.PasswordHash);
            Assert.AreNotEqual(OwnerAccount.SingletonId, account.Id);
            Assert.IsNull(await service.ValidateCredentialsAsync(
                "plex-viewer", "some-password-string"));

            var viaPlex = await service.GetByExternalIdentityAsync("plex", "42");
            Assert.IsNotNull(viaPlex);
            Assert.AreEqual(account.Id, viaPlex.Id);
            Assert.AreEqual(1, await db.AccountLoginIdentities.CountAsync());

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.CreateExternalAccountAsync(
                    "plex", "42", "another-viewer", isEnabled: true));
            Assert.AreEqual(2, await db.OwnerAccounts.CountAsync());
        });
    }

    [TestMethod]
    public async Task ExistingLocalAccount_LinkPreservesIdentityAndPassword()
    {
        await WithDatabaseAsync(async (db, service) =>
        {
            var user = await service.CreateUserAsync(
                "local-user", "a sufficiently long password");

            await service.LinkExternalIdentityAsync(user.Id, "plex", "126");
            await service.LinkExternalIdentityAsync(user.Id, "PLEX", "126");

            Assert.AreEqual(user.Id,
                (await service.GetByExternalIdentityAsync("plex", "126"))?.Id);
            Assert.AreEqual(user.Id,
                (await service.ValidateCredentialsAsync(
                    "local-user", "a sufficiently long password"))?.Id);
            Assert.AreEqual(1, await db.AccountLoginIdentities.CountAsync());

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.LinkExternalIdentityAsync(user.Id, "plex", "127"));
        });
    }

    [TestMethod]
    public async Task SameExternalIdentity_CannotAttachToAnotherLocalAccount()
    {
        await WithDatabaseAsync(async (db, service) =>
        {
            var first = await service.CreateUserAsync(
                "first-user", "a sufficiently long password");
            var second = await service.CreateUserAsync(
                "second-user", "a sufficiently long password");

            await service.LinkExternalIdentityAsync(first.Id, "plex", "123");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.LinkExternalIdentityAsync(second.Id, "plex", "123"));

            Assert.AreEqual(first.Id,
                (await service.GetByExternalIdentityAsync("plex", "123"))?.Id);
            Assert.AreEqual(1, await db.AccountLoginIdentities.CountAsync());
        });
    }

    [TestMethod]
    public async Task Unlink_RequiresAnotherWorkingSignInMethod()
    {
        await WithDatabaseAsync(async (db, service) =>
        {
            var local = await service.CreateUserAsync(
                "local-user", "a sufficiently long password");
            await service.LinkExternalIdentityAsync(local.Id, "plex", "abc");
            var session = await service.GetEnabledAccountAsync(local.Id);
            Assert.IsNotNull(session);

            await service.UnlinkExternalIdentityAsync(local.Id, "plex");

            Assert.IsNull(await service.GetEnabledAccountAsync(
                local.Id, session.SessionVersion));
            Assert.IsNull(await service.GetByExternalIdentityAsync("plex", "abc"));
            Assert.IsNotNull(await service.ValidateCredentialsAsync(
                "local-user", "a sufficiently long password"));

            var external = await service.CreateExternalAccountAsync(
                "plex", "only-plex", "plex-only", isEnabled: true);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => service.UnlinkExternalIdentityAsync(external.Id, "plex"));
            Assert.AreEqual(1, await db.AccountLoginIdentities.CountAsync());
        });
    }

    [TestMethod]
    public async Task DisabledExternalAccount_CannotSignIn()
    {
        await WithDatabaseAsync(async (_, service) =>
        {
            var account = await service.CreateExternalAccountAsync(
                "plex", "9", "pending-plex-user", isEnabled: false);

            Assert.IsNull(await service.GetByExternalIdentityAsync("plex", "9"));

            await service.SetEnabledAsync(account.Id, true);

            Assert.IsNotNull(await service.GetByExternalIdentityAsync("plex", "9"));

            await service.SetEnabledAsync(account.Id, false);

            Assert.IsNull(await service.GetByExternalIdentityAsync("plex", "9"));
        });
    }

    [TestMethod]
    public async Task ExternalIdentity_InvalidIdentifiers_AreRejected()
    {
        await WithDatabaseAsync(async (_, service) =>
        {
            var account = await service.CreateUserAsync(
                "local-user", "a sufficiently long password");

            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                service.LinkExternalIdentityAsync(account.Id, "plex!", "123"));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                service.LinkExternalIdentityAsync(account.Id, "plex", "  "));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                service.LinkExternalIdentityAsync(
                    account.Id, "plex", new string('a', 161)));
        });
    }

    [TestMethod]
    public async Task AutoProvision_CapsInstantRoleDefaultsWithoutGrantingHiddenMedia()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"jularr-plex-caps-{Guid.NewGuid():N}");
        try
        {
            var store = new MediaCapabilityStore(root);
            await store.SetRoleDefaultAsync(
                AccountRole.User,
                WorkMediaType.Movie,
                MediaCapability.Instant);
            await store.SetRoleDefaultAsync(
                AccountRole.User,
                WorkMediaType.Anime,
                MediaCapability.Hidden);

            await store.ConstrainNewExternalAccountAsync("new-plex-account");
            var policy = await store.LoadAsync();

            Assert.AreEqual(
                MediaCapability.Request,
                policy.Resolve(
                    AccountRole.User,
                    "new-plex-account",
                    WorkMediaType.Movie));
            Assert.AreEqual(
                MediaCapability.Hidden,
                policy.Resolve(
                    AccountRole.User,
                    "new-plex-account",
                    WorkMediaType.Anime));

            await store.SetRoleDefaultAsync(
                AccountRole.User,
                WorkMediaType.Anime,
                MediaCapability.Instant);
            policy = await store.LoadAsync();
            Assert.AreEqual(
                MediaCapability.Hidden,
                policy.Resolve(
                    AccountRole.User,
                    "new-plex-account",
                    WorkMediaType.Anime));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task WithDatabaseAsync(
        Func<AppDbContext, OwnerAuthService, Task> test)
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"jularr-plex-identity-{Guid.NewGuid():N}.db");

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={path};Foreign Keys=True")
                .Options;
            await using var db = new AppDbContext(options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            var service = new OwnerAuthService(
                db, new PasswordHasher<OwnerAccount>());
            await service.CreateOwnerAsync(
                "owner", "a sufficiently long owner password");
            await test(db, service);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
