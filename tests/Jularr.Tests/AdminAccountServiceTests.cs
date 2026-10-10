using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Microsoft.AspNetCore.Identity;

namespace Jularr.Tests;

[TestClass]
public sealed class AdminAccountServiceTests
{
    [TestMethod]
    public async Task ReadUsersV1_AdminGetsStableDatabasePages()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var auth = new OwnerAuthService(db, new PasswordHasher<OwnerAccount>());
        var owner = await auth.CreateOwnerAsync("owner", "a sufficiently long owner password");
        await auth.CreateUserAsync("charlie", "a sufficiently long user password");
        await auth.CreateUserAsync("alpha", "a sufficiently long user password");
        await auth.CreateUserAsync("bravo", "a sufficiently long user password");
        await auth.CreateUserAsync("delta", "a sufficiently long user password");

        var actor = OwnerAuthService.CreatePrincipal(owner);
        var service = new AdminAccountService(db);

        var first = await service.ReadUsersV1(actor, new PageRequest(1, 2));
        var second = await service.ReadUsersV1(actor, new PageRequest(2, 2));
        var third = await service.ReadUsersV1(actor, new PageRequest(3, 2));

        CollectionAssert.AreEqual(
            new[] { "owner", "alpha" },
            first.Items.Select(item => item.UserName).ToArray());
        CollectionAssert.AreEqual(
            new[] { "bravo", "charlie" },
            second.Items.Select(item => item.UserName).ToArray());
        CollectionAssert.AreEqual(
            new[] { "delta" },
            third.Items.Select(item => item.UserName).ToArray());
        Assert.AreEqual(5L, first.TotalCount);
        Assert.AreEqual(2, second.Page);
        Assert.AreEqual(2, third.PageSize);
        Assert.AreEqual(true, first.HasMore);
        Assert.AreEqual(false, third.HasMore);
    }

    [TestMethod]
    public async Task ReadUsersV1_SearchAndCountUseTheSameLiteralCaseInsensitiveFilter()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        db.OwnerAccounts.AddRange(
            new OwnerAccount { Id = "owner", UserName = "Owner", NormalizedUserName = "OWNER", PasswordHash = "test-only", Role = AccountRole.Owner },
            new OwnerAccount { Id = "alpha", UserName = "Demo Alpha", NormalizedUserName = "DEMO ALPHA", PasswordHash = "test-only", Role = AccountRole.User },
            new OwnerAccount { Id = "beta", UserName = "Demo Beta", NormalizedUserName = "DEMO BETA", PasswordHash = "test-only", Role = AccountRole.User, IsEnabled = false },
            new OwnerAccount { Id = "literal", UserName = "Demo %_", NormalizedUserName = "DEMO %_", PasswordHash = "test-only", Role = AccountRole.User });
        await db.SaveChangesAsync();
        var actor = OwnerAuthService.CreatePrincipal(await db.OwnerAccounts.FindAsync("owner") ?? throw new InvalidOperationException());
        var service = new AdminAccountService(db);
        var result = await service.ReadUsersV1(actor, new PageRequest(2, 2), query: " deMO ");
        Assert.AreEqual(3L, result.TotalCount);
        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual(false, result.HasMore);
        var literal = await service.ReadUsersV1(actor, new PageRequest(), query: "%_");
        Assert.AreEqual(1L, literal.TotalCount);
        Assert.AreEqual("literal", literal.Items.Single().Id);
        var disabled = await service.ReadUsersV1(actor, new PageRequest(), query: "beta");
        Assert.IsFalse(disabled.Items.Single().IsEnabled);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => service.ReadUsersV1(actor, new PageRequest(), query: new string('x', 81)));
    }

    [TestMethod]
    public async Task ReadUsersV1_NonAdminCannotReadDirectory()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var auth = new OwnerAuthService(db, new PasswordHasher<OwnerAccount>());
        await auth.CreateOwnerAsync("owner", "a sufficiently long owner password");
        var user = await auth.CreateUserAsync("learner", "a sufficiently long user password");
        var service = new AdminAccountService(db);

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
            service.ReadUsersV1(OwnerAuthService.CreatePrincipal(user), new PageRequest()));

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
            service.ReadUsersV1(new ClaimsPrincipal(), new PageRequest()));
    }
}
