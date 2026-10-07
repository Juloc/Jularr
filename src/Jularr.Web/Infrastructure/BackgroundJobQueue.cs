using System.Collections.Concurrent;
using System.Data.Common;
using System.Threading.Channels;
using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Performance;

namespace Jularr.Web.Infrastructure;

internal sealed record QueuedBackgroundWork(Guid OperationId);

internal sealed class RuntimeBackgroundWork(
    Func<OperationExecutionContext, IServiceProvider, CancellationToken, Task> work,
    bool retryable)
{
    public Func<OperationExecutionContext, IServiceProvider, CancellationToken, Task> Work { get; } = work;
    public bool Retryable { get; } = retryable;
    public CancellationTokenSource Cancellation { get; private set; } = new();

    public void RenewCancellation()
    {
        Cancellation.Dispose();
        Cancellation = new CancellationTokenSource();
    }
}

public abstract class BackgroundJobQueueBase(
    IServiceScopeFactory scopeFactory)
{
    private readonly Channel<QueuedBackgroundWork> channel =
        Channel.CreateBounded<QueuedBackgroundWork>(
            new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

    // Operations this process queued itself are never recovered as abandoned, even when a
    // producer queues before the worker has finished recovering the previous process.
    private readonly DateTime ownedSinceUtc = DateTime.UtcNow;

    private readonly ConcurrentDictionary<Guid, RuntimeBackgroundWork> runtime =
        new();

    protected abstract OperationDescriptor DefaultDescriptor { get; }

    protected abstract IReadOnlyList<OperationLane> RecoveryLanes { get; }

    protected virtual OperationDescriptor NormalizeDescriptor(
        OperationDescriptor descriptor) =>
        descriptor;

    public ValueTask<Guid> QueueAsync(
        Func<IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken = default) =>
        QueueAsync(
            DefaultDescriptor,
            (_, services, workerToken) => work(services, workerToken),
            cancellationToken);

    public ValueTask<Guid> QueueAsync(
        OperationDescriptor descriptor,
        Func<IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken = default) =>
        QueueAsync(
            descriptor,
            (_, services, workerToken) => work(services, workerToken),
            cancellationToken);

    public async ValueTask<Guid> QueueAsync(
        OperationDescriptor descriptor,
        Func<OperationExecutionContext, IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(work);

        descriptor = NormalizeDescriptor(descriptor);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = new OperationStore(db);
        var operationId = await store.CreateAsync(descriptor, cancellationToken);

        var runtimeWork = new RuntimeBackgroundWork(work, descriptor.Retryable);
        if (!runtime.TryAdd(operationId, runtimeWork))
        {
            await store.MarkFailedAsync(
                operationId,
                "Could not reserve runtime state for the queued operation.",
                cancellationToken);
            throw new InvalidOperationException(
                "Could not reserve runtime state for the queued operation.");
        }

        try
        {
            await channel.Writer.WriteAsync(
                new QueuedBackgroundWork(operationId),
                cancellationToken);
            return operationId;
        }
        catch
        {
            runtime.TryRemove(operationId, out _);
            await store.MarkCancelledAsync(
                operationId,
                "Operation was cancelled before it entered the worker queue.",
                CancellationToken.None);
            throw;
        }
    }

    public async Task<bool> CancelAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!runtime.TryGetValue(operationId, out var work))
        {
            return false;
        }

        work.Cancellation.Cancel();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = new OperationStore(db);
        var operation = await store.GetAsync(operationId, cancellationToken);

        if (operation?.Status == OperationStatus.Queued)
        {
            await store.MarkCancelledAsync(
                operationId,
                "Cancelled by administrator.",
                cancellationToken);
        }

        return operation is not null;
    }

    public async Task<bool> RetryAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!runtime.TryGetValue(operationId, out var work) || !work.Retryable)
        {
            return false;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = new OperationStore(db);

        if (!await store.PrepareRetryAsync(operationId, cancellationToken))
        {
            return false;
        }

        work.RenewCancellation();
        await channel.Writer.WriteAsync(
            new QueuedBackgroundWork(operationId),
            cancellationToken);
        return true;
    }

    public bool HasRuntimeWork(Guid operationId) =>
        runtime.ContainsKey(operationId);

    internal bool TryGetRuntimeWork(
        Guid operationId,
        out RuntimeBackgroundWork? work) =>
        runtime.TryGetValue(operationId, out work);

    internal void CompleteRuntimeWork(
        Guid operationId,
        bool keepForRetry)
    {
        if (keepForRetry)
        {
            return;
        }

        if (runtime.TryRemove(operationId, out var removed))
        {
            removed.Cancellation.Dispose();
        }
    }

    internal ChannelReader<QueuedBackgroundWork> Reader => channel.Reader;

    internal async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = new OperationStore(db);

        foreach (var lane in RecoveryLanes)
        {
            await store.RecoverInterruptedAsync(lane, ownedSinceUtc, cancellationToken);
        }
    }
}

public sealed class BackgroundJobQueue(IServiceScopeFactory scopeFactory)
    : BackgroundJobQueueBase(scopeFactory)
{
    protected override OperationDescriptor DefaultDescriptor =>
        OperationDescriptor.Background();

    protected override IReadOnlyList<OperationLane> RecoveryLanes =>
        [OperationLane.Normal, OperationLane.Maintenance];

    protected override OperationDescriptor NormalizeDescriptor(
        OperationDescriptor descriptor) =>
        descriptor.Lane == OperationLane.Interactive
            ? descriptor with { Lane = OperationLane.Normal }
            : descriptor;
}

public sealed class PlaybackJobQueue(IServiceScopeFactory scopeFactory)
    : BackgroundJobQueueBase(scopeFactory)
{
    protected override OperationDescriptor DefaultDescriptor =>
        OperationDescriptor.Playback();

    protected override IReadOnlyList<OperationLane> RecoveryLanes =>
        [OperationLane.Interactive];

    protected override OperationDescriptor NormalizeDescriptor(
        OperationDescriptor descriptor) =>
        descriptor with { Lane = OperationLane.Interactive };
}

public abstract class BackgroundJobWorkerBase<TQueue>(
    TQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger logger) : BackgroundService
    where TQueue : BackgroundJobQueueBase
{
    // How much queued work the worker looks at when it chooses the next job; work beyond that waits in
    // the (bounded) queue, so producers are held back as before.
    private const int MaxWaiting = 32;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await queue.RecoverAsync(stoppingToken);

        var waiting = new List<QueuedBackgroundWork>();
        while (await TakeNextAsync(waiting, stoppingToken) is { } queued)
        {
            if (!queue.TryGetRuntimeWork(queued.OperationId, out var runtime)
                || runtime is null)
            {
                continue;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = new OperationStore(db);
            var operation = await store.GetAsync(
                queued.OperationId,
                stoppingToken);

            if (operation is null ||
                operation.Status is OperationStatus.Cancelled
                    or OperationStatus.Succeeded)
            {
                queue.CompleteRuntimeWork(
                    queued.OperationId,
                    keepForRetry: false);
                continue;
            }

            await store.MarkRunningAsync(
                queued.OperationId,
                stoppingToken);

            using var executionCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken,
                    runtime.Cancellation.Token);

            var context = new OperationExecutionContext(
                queued.OperationId,
                scope.ServiceProvider);
            using var operationProfileScope =
                scope.ServiceProvider
                    .GetService<OperationProfileContext>()
                    ?.Enter(operation.ProfileId);

            try
            {
                await RunWorkAsync(
                    operation,
                    token => runtime.Work(context, scope.ServiceProvider, token),
                    executionCancellation.Token);

                await store.MarkSucceededAsync(
                    queued.OperationId,
                    cancellationToken: CancellationToken.None);

                queue.CompleteRuntimeWork(
                    queued.OperationId,
                    keepForRetry: false);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                await store.MarkInterruptedAsync(
                    queued.OperationId,
                    "Interrupted because Jularr is stopping.",
                    CancellationToken.None);
                break;
            }
            catch (OperationCanceledException)
                when (runtime.Cancellation.IsCancellationRequested)
            {
                await store.MarkCancelledAsync(
                    queued.OperationId,
                    "Cancelled by administrator.",
                    CancellationToken.None);
                queue.CompleteRuntimeWork(
                    queued.OperationId,
                    keepForRetry: runtime.Retryable);
            }
            catch (Exception exception)
            {
                var safeMessage =
                    $"{exception.GetType().Name}: {exception.Message}";
                await store.MarkFailedAsync(
                    queued.OperationId,
                    safeMessage,
                    CancellationToken.None);

                logger.LogError(
                    exception,
                    "Background operation {OperationId} failed.",
                    queued.OperationId);

                queue.CompleteRuntimeWork(
                    queued.OperationId,
                    keepForRetry: runtime.Retryable);
            }
        }
    }

    /// <summary>Runs one operation's work; a worker that shares the server with interactive requests overrides this to yield to them.</summary>
    protected virtual Task RunWorkAsync(
        OperationSnapshot operation,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken) =>
        work(cancellationToken);

    // The next job to run: the highest priority among the queued work (read fresh, so a change made
    // while the job waited counts), the earliest first among equals. Null once the queue is closed.
    private async Task<QueuedBackgroundWork?> TakeNextAsync(
        List<QueuedBackgroundWork> waiting,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            while (waiting.Count < MaxWaiting && queue.Reader.TryRead(out var item))
            {
                waiting.Add(item);
            }

            if (waiting.Count > 0)
            {
                break;
            }

            if (!await queue.Reader.WaitToReadAsync(cancellationToken))
            {
                return null;
            }
        }

        var index = waiting.Count == 1 ? 0 : await PickAsync(waiting, cancellationToken);
        var next = waiting[index];
        waiting.RemoveAt(index);
        return next;
    }

    private async Task<int> PickAsync(
        List<QueuedBackgroundWork> waiting,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = new OperationStore(scope.ServiceProvider.GetRequiredService<AppDbContext>());
            var priorities = await store.GetPrioritiesAsync(
                waiting.Select(work => work.OperationId).Distinct().ToArray(),
                cancellationToken);
            return Math.Max(
                0,
                OperationPriorities.PickNext(
                    waiting
                        .Select(work => priorities.GetValueOrDefault(work.OperationId, OperationPriority.Normal))
                        .ToArray()));
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Could not read operation priorities; running queued work in order.");
            return 0;
        }
    }
}

/// <summary>
/// The worker of the background lanes. It runs one operation at a time already; through the governor each one also yields to interactive
/// requests (scans as <see cref="BackgroundWorkClass.Scan"/>, the maintenance lane as <see cref="BackgroundWorkClass.Maintenance"/>, the rest as
/// provider/AI work) and is timed under its operation kind.
/// </summary>
public sealed class BackgroundJobWorker(
    BackgroundJobQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<BackgroundJobWorker> logger,
    BackgroundWorkGovernor? governor = null)
    : BackgroundJobWorkerBase<BackgroundJobQueue>(
        queue,
        scopeFactory,
        logger)
{
    protected override Task RunWorkAsync(OperationSnapshot operation, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        var workClass = operation.Kind == LibraryScanCoordinator.OperationKind
            ? BackgroundWorkClass.Scan
            : operation.Lane == OperationLane.Maintenance ? BackgroundWorkClass.Maintenance : BackgroundWorkClass.ProviderRefresh;
        return governor.RunGovernedAsync(workClass, $"Operation.{operation.Kind}", work, cancellationToken);
    }
}

public sealed class PlaybackJobWorker(
    PlaybackJobQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<PlaybackJobWorker> logger)
    : BackgroundJobWorkerBase<PlaybackJobQueue>(
        queue,
        scopeFactory,
        logger)
{
}
