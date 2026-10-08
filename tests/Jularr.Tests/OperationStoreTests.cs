using Jularr.Web.Data;
using Jularr.Web.Features.Operations;
using Jularr.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class OperationStoreTests
{
    [TestMethod]
    public async Task StoreTracksLifecycleDownloadsHistoryAndLogs()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var store = new OperationStore(db);

            var id = await store.CreateAsync(
                new OperationDescriptor(
                    "test-download",
                    "Downloads",
                    "Download test asset",
                    "Example",
                    "owner",
                    OperationLane.Normal,
                    IsDownload: true,
                    Retryable: true,
                    BytesTotal: 1_000));

            var queued = await store.GetAsync(id);
            Assert.IsNotNull(queued);
            Assert.AreEqual(OperationStatus.Queued, queued.Status);
            Assert.AreEqual(1, queued.Attempt);
            Assert.IsTrue(queued.IsDownload);

            await store.MarkRunningAsync(id);
            await store.ReportProgressAsync(
                id,
                40,
                "Downloading.",
                bytesCompleted: 400,
                bytesTotal: 1_000,
                bytesPerSecond: 200,
                etaUtc: DateTime.UtcNow.AddSeconds(3));
            await store.AppendLogAsync(
                id,
                OperationLogLevel.Warning,
                "Download",
                "Transient source warning.");
            await store.MarkFailedAsync(
                id,
                "HttpRequestException: test failure");

            var failed = await store.GetAsync(id);
            Assert.IsNotNull(failed);
            Assert.AreEqual(OperationStatus.Failed, failed.Status);
            Assert.AreEqual(40, failed.ProgressPercent);
            Assert.AreEqual(400L, failed.BytesCompleted);
            Assert.IsNotNull(failed.FinishedAtUtc);

            var downloads = await store.ListAsync(
                new OperationListFilter(View: "downloads"));
            CollectionAssert.Contains(downloads.Select(x => x.Id).ToList(), id);

            var history = await store.ListAsync(
                new OperationListFilter(View: "history", Search: "test asset"));
            Assert.AreEqual(1, history.Count);
            Assert.AreEqual(id, history[0].Id);

            var errors = await store.ListLogsAsync(
                new OperationLogFilter(
                    Level: OperationLogLevel.Error,
                    OperationId: id));
            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains(errors[0].Message, "test failure");

            Assert.IsTrue(await store.PrepareRetryAsync(id));

            var retried = await store.GetAsync(id);
            Assert.IsNotNull(retried);
            Assert.AreEqual(OperationStatus.Queued, retried.Status);
            Assert.AreEqual(2, retried.Attempt);
            Assert.IsNull(retried.Error);
            Assert.IsNull(retried.FinishedAtUtc);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task RestartRecoveryOnlyInterruptsTheRequestedLane()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var store = new OperationStore(db);

            var normalId = await store.CreateAsync(
                new OperationDescriptor(
                    "normal",
                    "Task",
                    "Normal task",
                    Lane: OperationLane.Normal));

            var interactiveId = await store.CreateAsync(
                new OperationDescriptor(
                    "interactive",
                    "Playback",
                    "Interactive task",
                    Lane: OperationLane.Interactive));

            await store.MarkRunningAsync(interactiveId);

            var recovered = await store.RecoverInterruptedAsync(
                OperationLane.Normal);

            Assert.AreEqual(1, recovered);

            var normal = await store.GetAsync(normalId);
            var interactive = await store.GetAsync(interactiveId);

            Assert.IsNotNull(normal);
            Assert.IsNotNull(interactive);
            Assert.AreEqual(OperationStatus.Interrupted, normal.Status);
            Assert.AreEqual(OperationStatus.Running, interactive.Status);
            Assert.AreEqual(
                "Interrupted by server restart.",
                normal.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task BackgroundQueuePersistsAndCompletesWork()
    {
        var path = TempDatabasePath();

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<AppDbContext>(
                options => options.UseSqlite(
                    $"Data Source={path};Foreign Keys=True"));

            await using var provider = services.BuildServiceProvider();

            await using (var migrationScope = provider.CreateAsyncScope())
            {
                var db = migrationScope.ServiceProvider
                    .GetRequiredService<AppDbContext>();
                await DatabaseMigrationBridge.UpgradeAsync(db);
            }

            var queue = new BackgroundJobQueue(
                provider.GetRequiredService<IServiceScopeFactory>());
            var worker = new BackgroundJobWorker(
                queue,
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<BackgroundJobWorker>.Instance);

            await worker.StartAsync(CancellationToken.None);

            var executed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var id = await queue.QueueAsync(
                new OperationDescriptor(
                    "worker-test",
                    "Test",
                    "Worker test",
                    Lane: OperationLane.Normal,
                    Retryable: true),
                async (operation, _, workerToken) =>
                {
                    await operation.ReportAsync(
                        50,
                        "Half way.",
                        cancellationToken: workerToken);
                    executed.TrySetResult();
                });

            await executed.Task.WaitAsync(TimeSpan.FromSeconds(30));

            OperationSnapshot? snapshot = null;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                await using var scope = provider.CreateAsyncScope();
                var db = scope.ServiceProvider
                    .GetRequiredService<AppDbContext>();
                snapshot = await new OperationStore(db).GetAsync(id);

                if (snapshot?.Status == OperationStatus.Succeeded)
                {
                    break;
                }

                await Task.Delay(50);
            }

            await worker.StopAsync(CancellationToken.None);

            Assert.IsNotNull(snapshot);
            Assert.AreEqual(OperationStatus.Succeeded, snapshot.Status);
            Assert.AreEqual(100, snapshot.ProgressPercent);
            Assert.IsFalse(queue.HasRuntimeWork(id));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void AdminPagesRequireAnAdminPolicy()
    {
        var pageTypes = new (Type Page, string Policy)[]
        {
            (typeof(Jularr.Web.Pages.Admin.IndexModel), Jularr.Web.Features.Auth.JularrPolicies.AdminMedia),
            (typeof(Jularr.Web.Pages.Admin.OperationsModel), Jularr.Web.Features.Auth.JularrPolicies.AdminMedia),
            (typeof(Jularr.Web.Pages.Admin.OperationModel), Jularr.Web.Features.Auth.JularrPolicies.AdminMedia),
            (typeof(Jularr.Web.Pages.Admin.LogsModel), Jularr.Web.Features.Auth.JularrPolicies.AdminMedia),
            (typeof(Jularr.Web.Pages.Admin.SystemModel), Jularr.Web.Features.Auth.JularrPolicies.AdminSystem),
            (typeof(Jularr.Web.Pages.Admin.UsersModel), Jularr.Web.Features.Auth.JularrPolicies.AdminSystem),
            (typeof(Jularr.Web.Pages.Admin.UserModel), Jularr.Web.Features.Auth.JularrPolicies.AdminSystem)
        };

        foreach (var (pageType, policy) in pageTypes)
        {
            var authorize = pageType
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>()
                .SingleOrDefault();

            Assert.IsNotNull(
                authorize,
                $"{pageType.Name} must require authorization.");
            Assert.AreEqual(
                policy,
                authorize.Policy,
                $"{pageType.Name} must require {policy}.");
        }
    }

    private static string TempDatabasePath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"jularr-operations-{Guid.NewGuid():N}.db");

    private static async Task<AppDbContext> CreateDatabaseAsync(string path)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .Options;

        var db = new AppDbContext(options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }
}
