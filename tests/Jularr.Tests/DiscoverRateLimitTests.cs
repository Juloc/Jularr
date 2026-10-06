using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Jularr.Web.Features.Discovery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jularr.Tests;

/// <summary>
/// The per-account limits of the Discover handlers: separate budgets for the body, the live status of requested titles and the page, counted per
/// signed-in account, applied after authentication, and answered with a status the page can back off from.
/// </summary>
[TestClass]
public sealed class DiscoverRateLimitTests
{
    private static HttpContext Context(string account, string handler)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, account)], "test"));
        context.Request.QueryString = handler.Length == 0 ? QueryString.Empty : new QueryString($"?handler={handler}");
        return context;
    }

    private static int Permits(RateLimitPartition<string> partition) => (int)partition.Factory(partition.PartitionKey).GetStatistics()!.CurrentAvailablePermits;

    [TestMethod]
    public void EveryKindOfRequestHasItsOwnBudgetPerAccountAndAnUnknownHandlerIsThePage()
    {
        var body = DiscoveryRegistration.PartitionFor(Context("alice", "Body"));
        var status = DiscoveryRegistration.PartitionFor(Context("alice", "RequestStatus"));
        var page = DiscoveryRegistration.PartitionFor(Context("alice", ""));
        var unknown = DiscoveryRegistration.PartitionFor(Context("alice", "SomethingElse"));
        var other = DiscoveryRegistration.PartitionFor(Context("bob", "Body"));

        Assert.AreEqual(DiscoveryRegistration.BodyRequestsPerMinute, Permits(body));
        Assert.AreEqual(DiscoveryRegistration.StatusRequestsPerMinute, Permits(status));
        Assert.AreEqual(DiscoveryRegistration.PageRequestsPerMinute, Permits(page));
        Assert.AreEqual(page.PartitionKey, unknown.PartitionKey, "A handler that does not exist cannot pick a bigger budget.");
        CollectionAssert.AllItemsAreUnique(new[] { body.PartitionKey, status.PartitionKey, page.PartitionKey, other.PartitionKey });
        Assert.IsTrue(body.PartitionKey.StartsWith("alice|", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task APollingClientCannotUseUpTheBudgetOfTheBodyAndOneAccountNeverLimitsAnother()
    {
        using var host = await StartAsync();
        using var client = host.GetTestClient();

        async Task<HttpStatusCode> Get(string account, string handler)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, handler.Length == 0 ? "/Discover" : $"/Discover?handler={handler}");
            request.Headers.Add("X-Account", account);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        for (var index = 0; index < DiscoveryRegistration.StatusRequestsPerMinute; index++)
        {
            Assert.AreEqual(HttpStatusCode.OK, await Get("alice", "RequestStatus"));
        }

        Assert.AreEqual(HttpStatusCode.TooManyRequests, await Get("alice", "RequestStatus"), "The status budget is spent.");
        Assert.AreEqual(HttpStatusCode.OK, await Get("alice", "Body"), "The body has a budget of its own.");
        Assert.AreEqual(HttpStatusCode.OK, await Get("alice", ""));
        Assert.AreEqual(HttpStatusCode.OK, await Get("bob", "RequestStatus"), "Another account has its own budgets.");
    }

    [TestMethod]
    public void TheLimiterRunsAfterAuthenticationSoItsPartitionIsTheAccountAndNotTheAddress()
    {
        var program = File.ReadAllText(Path.Combine(PlayerControlsTests.RepositoryRoot(), "src", "Jularr.Web", "Program.cs"));

        var authentication = program.IndexOf("app.UseAuthentication();", StringComparison.Ordinal);
        var limiter = program.IndexOf("app.UseRateLimiter();", StringComparison.Ordinal);
        var authorization = program.IndexOf("app.UseAuthorization();", StringComparison.Ordinal);

        Assert.IsTrue(authentication > 0 && authentication < limiter, "Without the signed-in user the partition falls back to the client address, which is shared by a household.");
        Assert.IsTrue(limiter < authorization);
        StringAssert.Contains(program, "context.Lease.TryGetMetadata(MetadataName.RetryAfter", "A rejected client is told for how long to back off.");
    }

    private static async Task<IHost> StartAsync() =>
        await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddRateLimiter(options => options.RejectionStatusCode = StatusCodes.Status429TooManyRequests);
                    services.AddDiscovery();
                })
                .Configure(app =>
                {
                    app.Use((context, next) =>
                    {
                        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, context.Request.Headers["X-Account"].ToString())], "test"));
                        return next();
                    });
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints => endpoints.MapGet("/Discover", () => "ok").RequireRateLimiting(DiscoveryRegistration.RateLimitPolicy));
                }))
            .StartAsync();
}
