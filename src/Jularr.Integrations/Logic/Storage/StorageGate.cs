using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Storage;

namespace Jularr.Integrations.Logic.Storage
{
    // Every storage call of one connection passes here: at most 16 at a time, and each within its deadline. A call past its deadline means the
    // storage hangs; the connection then answers storage_unavailable at once for 30 s, and after that one call tries again (a small breaker). A call
    // whose caller gave up keeps its place until the storage really returns, so a hanging mount never ties up more than 16 threads, and those are
    // the connection's own (Threads), never the thread pool's.
    internal sealed class StorageGate(TimeProvider time, TimeSpan callTime)
    {
        public const int MaximumCalls = 16;

        // Above SMB's own 30 s, so a NAS whose disks spin up is not taken for gone.
        public static readonly TimeSpan CallTime = TimeSpan.FromSeconds(35);

        // A person waits this long for a free place, background work as long as a call may take.
        public static readonly TimeSpan InteractiveWait = TimeSpan.FromSeconds(2);

        public static readonly TimeSpan ClosedTime = TimeSpan.FromSeconds(30);

        // When a caller may try again while another call tries the storage.
        private static readonly TimeSpan TryingTime = TimeSpan.FromSeconds(5);

        private readonly SemaphoreSlim _places = new(MaximumCalls, MaximumCalls);
        private readonly Lock _state = new();
        private DateTimeOffset? _closedUntil;
        private bool _trying;

        public StorageGate(TimeProvider time) : this(time, CallTime)
        {
        }

        // Where a folder on this host runs its blocking calls.
        public TaskScheduler Threads { get; } = new StorageThreads();

        public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, bool interactive, CancellationToken ct)
        {
            var trial = Admit();
            Task<T> work;
            try
            {
                await EnterAsync(interactive ? InteractiveWait : callTime, ct);
                work = Start(call, ct);
            }
            catch (NasMountException) when (trial)
            {
                // Calls the storage never answered still hold every place: it still hangs.
                throw Trip(null);
            }
            catch
            {
                if (trial) Untried();
                throw;
            }

            try
            {
                var result = await work.WaitAsync(callTime, time, ct);
                if (trial) Reopen();
                return result;
            }
            catch (TimeoutException exception)
            {
                Abandon(work);
                throw Trip(exception);
            }
            catch (NasMountException exception) when (exception.InnerException is TimeoutException timeout)
            {
                // The SMB session's own deadline passed.
                throw Trip(timeout);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Abandon(work);
                if (trial) Untried();
                throw;
            }
            catch
            {
                // The storage answered, if only with an error.
                if (trial) Reopen();
                throw;
            }
        }

        // A probe takes a place but leaves the breaker alone, except that storage that reads opens the connection again.
        public async Task<StorageProbe> ProbeAsync(Func<CancellationToken, Task<StorageProbe>> probe, CancellationToken ct)
        {
            try
            {
                await EnterAsync(InteractiveWait, ct);
            }
            catch (NasMountException)
            {
                return new StorageProbe(false, "probe_timeout", null, null);
            }

            var result = await Start(probe, ct);
            if (result.IsReadable) Reopen();
            return result;
        }

        // Callers fail at once while the connection is closed; once the time is up, the first of them tries.
        private bool Admit()
        {
            lock (_state)
            {
                if (_closedUntil is not { } until)
                {
                    return false;
                }

                var now = time.GetUtcNow();
                if (now < until) throw new NasMountException("storage_unavailable", null, until - now);
                if (_trying) throw new NasMountException("storage_unavailable", null, TryingTime);
                _trying = true;
                return true;
            }
        }

        private async Task EnterAsync(TimeSpan wait, CancellationToken ct)
        {
            if (_places.Wait(0))
            {
                return;
            }

            using var timeout = new CancellationTokenSource(wait, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                await _places.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Every place is taken, by calls the storage does not answer or by more than it can do.
                throw new NasMountException("storage_unavailable");
            }
        }

        // The place is held until the storage returns, whether or not anyone still waits.
        private Task<T> Start<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
        {
            Task<T> work;
            try
            {
                work = call(ct);
            }
            catch (Exception exception)
            {
                work = Task.FromException<T>(exception);
            }

            _ = work.ContinueWith(static (done, places) =>
            {
                _ = done.Exception;
                ((SemaphoreSlim)places!).Release();
            }, _places, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return work;
        }

        // A file that opens after its caller gave up closes again.
        private static void Abandon<T>(Task<T> work) => _ = work.ContinueWith(static done =>
        {
            if (done.Result is IDisposable abandoned) abandoned.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        private NasMountException Trip(Exception? cause)
        {
            lock (_state)
            {
                _closedUntil = time.GetUtcNow() + ClosedTime;
                _trying = false;
            }

            return new NasMountException("storage_unavailable", cause, ClosedTime);
        }

        private void Reopen()
        {
            lock (_state)
            {
                _closedUntil = null;
                _trying = false;
            }
        }

        // The trial ended without an answer; the next caller tries.
        private void Untried()
        {
            lock (_state) _trying = false;
        }
    }

    // Threads of one connection for calls that block inside the kernel, such as a file opened on a mount that stopped answering: a hanging call
    // holds one of these, and a call queued behind it gets another. The gate lets 16 calls through at most, so there are never more busy threads;
    // an idle thread ends after a while.
    internal sealed class StorageThreads : TaskScheduler
    {
        private static readonly TimeSpan ThreadIdle = TimeSpan.FromSeconds(30);

        private readonly object _lock = new();
        private readonly Queue<Task> _queue = new();
        private int _idle;
        private int _wakeups;

        protected override void QueueTask(Task task)
        {
            lock (_lock)
            {
                _queue.Enqueue(task);

                // An idle thread nobody woke yet takes it; otherwise a new one, so nothing waits behind a call that hangs.
                if (_idle > _wakeups)
                {
                    _wakeups++;
                    Monitor.Pulse(_lock);
                    return;
                }
            }

            new Thread(Serve) { IsBackground = true, Name = "Jularr storage" }.Start();
        }

        // Never on the caller's thread, which may be the thread pool's.
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks()
        {
            lock (_lock) return _queue.ToArray();
        }

        private void Serve()
        {
            while (true)
            {
                Task? task;
                lock (_lock)
                {
                    if (!_queue.TryDequeue(out task))
                    {
                        _idle++;
                        Monitor.Wait(_lock, ThreadIdle);
                        _idle--;
                        if (_wakeups > 0) _wakeups--;
                        if (!_queue.TryDequeue(out task)) return;
                    }
                }

                TryExecuteTask(task);
            }
        }
    }
}
