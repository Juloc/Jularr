using System.Security.Claims;
using Jularr.Web.Features.InstantPlay;
using Microsoft.AspNetCore.Http;

namespace Jularr.Tests;

/// <summary>Per-account rate limits only work when the account is known: one household behind one address must not share a limit.</summary>
[TestClass]
public sealed class InstantPlayRateLimitTests
{
    [TestMethod]
    public void ThePartitionIsTheSignedInAccountAndOnlyAnonymousCallersShareTheirAddress()
    {
        var address = System.Net.IPAddress.Parse("192.168.1.20");
        HttpContext Context(string? profile) => new DefaultHttpContext
        {
            User = profile is null ? new ClaimsPrincipal(new ClaimsIdentity()) : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, profile)], "test")),
            Connection = { RemoteIpAddress = address }
        };

        Assert.AreEqual("alice", InstantPlayRegistration.AccountPartitionKey(Context("alice")));
        Assert.AreEqual("bob", InstantPlayRegistration.AccountPartitionKey(Context("bob")));
        Assert.AreEqual("192.168.1.20", InstantPlayRegistration.AccountPartitionKey(Context(null)));
    }

    [TestMethod]
    public void AuthenticationRunsBeforeTheRateLimiterSoAccountPartitionsSeeTheAccount()
    {
        var program = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Jularr.Web", "Program.cs"));

        var authentication = program.IndexOf("app.UseAuthentication();", StringComparison.Ordinal);
        var limiter = program.IndexOf("app.UseRateLimiter();", StringComparison.Ordinal);

        Assert.IsTrue(authentication > 0 && limiter > authentication, "UseRateLimiter must come after UseAuthentication.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
