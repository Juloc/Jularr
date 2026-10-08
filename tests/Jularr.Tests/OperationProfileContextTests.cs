using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Operations;
using Jularr.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class OperationProfileContextTests
{
    [TestMethod]
    public void CurrentAccountContextFallsBackToOperationProfile()
    {
        var operationProfile = new OperationProfileContext();
        var account = new CurrentAccountContext(
            new HttpContextAccessor(),
            operationProfile);

        using (operationProfile.Enter("background-profile"))
        {
            Assert.AreEqual("background-profile", account.ProfileId);
        }

        Assert.ThrowsExactly<InvalidOperationException>(
            () => _ = account.ProfileId);
    }

    [TestMethod]
    public void CurrentAccountContextPrefersHttpRequestProfile()
    {
        var operationProfile = new OperationProfileContext();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "request-profile")],
                        "Test"))
            }
        };
        var account = new CurrentAccountContext(accessor, operationProfile);

        using (operationProfile.Enter("background-profile"))
        {
            Assert.AreEqual("request-profile", account.ProfileId);
        }
    }

    [TestMethod]
    public async Task BackgroundWorkerExposesPersistedOperationProfile()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"jularr-operation-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "jularr.db");

        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite(
                    $"Data Source={databasePath};Foreign Keys=True"));
            services.AddHttpContextAccessor();
            services.AddScoped<OperationProfileContext>();
            services.AddScoped<CurrentAccountContext>();
            services.AddSingleton<BackgroundJobQueue>();
            services.AddSingleton(sp =>
                new BackgroundJobWorker(
                    sp.GetRequiredService<BackgroundJobQueue>(),
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<BackgroundJobWorker>.Instance));

            await using var provider =
                services.BuildServiceProvider(validateScopes: true);

            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await DatabaseMigrationBridge.UpgradeAsync(db);
            }

            var queue = provider.GetRequiredService<BackgroundJobQueue>();
            var worker = provider.GetRequiredService<BackgroundJobWorker>();
            var resolvedProfile = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            await worker.StartAsync(CancellationToken.None);
            try
            {
                await queue.QueueAsync(
                    new OperationDescriptor(
                        "test-profile-context",
                        "Tests",
                        "Resolve background profile",
                        ProfileId: "queued-profile",
                        Retryable: false),
                    (scopedServices, _) =>
                    {
                        var account =
                            scopedServices.GetRequiredService<CurrentAccountContext>();
                        resolvedProfile.TrySetResult(account.ProfileId);
                        return Task.CompletedTask;
                    });

                var actual = await resolvedProfile.Task.WaitAsync(
                    TimeSpan.FromSeconds(30));

                Assert.AreEqual("queued-profile", actual);
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
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
}
