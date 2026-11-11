using System.Threading.Channels;
using Jularr.Integrations.Data.Nas;
using SMBLibrary;
using SMBLibrary.Client;

namespace Jularr.Integrations.Logic.Storage
{
    // Where an SMB share is and who signs in; the password only lives here while a session may open.
    internal sealed record SmbAddress(string Host, string Share, string? Domain, string Username, string Password);

    // The signed-in sessions to one SMB share. Short requests such as listings and renames share the first session; every open file takes a session
    // of its own while there are fewer than eight, so a playback never waits behind a scan or an import. Requests on one session take turns.
    internal sealed class SmbShare(Func<SmbSession> connect, TimeProvider time) : IDisposable
    {
        // Sessions per share, the shared one included; beyond that open files share the least busy session.
        public const int MaximumSessions = 8;

        // A NAS whose disks spin up answers slowly.
        private const int ResponseTimeoutMilliseconds = 30000;

        private readonly Lock _lanes = new();
        private readonly List<SmbLane> _all = [];
        private bool _disposed;

        public static SmbSession Connect(SmbAddress address)
        {
            var client = new SMB2Client(ResponseTimeoutMilliseconds, true);
            try
            {
                if (!client.Connect(address.Host, SMBTransportType.DirectTCPTransport))
                {
                    throw new NasMountException("storage_unavailable");
                }

                var status = client.Login(address.Domain ?? "", address.Username, address.Password);
                Check(status);
                var store = client.TreeConnect(address.Share, out status);
                Check(status);
                return new SmbSession(store, () =>
                {
                    store.Disconnect();
                    client.Logoff();
                    client.Disconnect();
                }, client.Disconnect);
            }
            catch
            {
                if (client.IsConnected) client.Disconnect();
                throw;
            }
        }

        // Sessions with an open connection now.
        public int OpenSessions
        {
            get
            {
                lock (_lanes) return _all.Count(lane => lane.IsOpen);
            }
        }

        // A short request on the shared session.
        public Task<T> RunAsync<T>(Func<ISMBFileStore, T> request, CancellationToken ct) => Shared().RunAsync(request, SmbLane.RequestTime, ct);

        // A session for one open file until the file closes: an idle one, a new one, or the least busy once all are taken.
        public SmbLane Lease()
        {
            lock (_lanes)
            {
                Shared();
                var lane = _all.Skip(1).FirstOrDefault(item => item.OpenStreams == 0);
                if (lane is null && _all.Count < MaximumSessions)
                {
                    lane = new SmbLane(connect, time);
                    _all.Add(lane);
                }

                lane ??= _all.Skip(1).MinBy(item => item.OpenStreams)!;
                lane.Opened();
                return lane;
            }
        }

        // Closes sessions after the idle time so the NAS may sleep; a session with an open file stays. Each session closes on its own thread.
        public Task CloseIfIdleAsync(TimeSpan idle)
        {
            lock (_lanes) return Task.WhenAll(_all.Select(lane => lane.CloseIfIdleAsync(idle)).ToArray());
        }

        // Never waits for the NAS: each session closes on its own thread once its running request returns.
        public void Dispose()
        {
            lock (_lanes)
            {
                _disposed = true;
                foreach (var lane in _all) lane.Dispose();
                _all.Clear();
            }
        }

        // Missing paths and a share out of reach read as unavailable storage, refusals as storage that denies Jularr (never the caller's sign-in).
        public static void Check(NTStatus status)
        {
            switch (status)
            {
                case NTStatus.STATUS_SUCCESS:
                    return;
                case NTStatus.STATUS_ACCESS_DENIED or NTStatus.STATUS_LOGON_FAILURE:
                    throw new NasMountException(NasMountException.AccessDenied);
                case NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_NO_SUCH_FILE or NTStatus.STATUS_BAD_NETWORK_NAME
                    or NTStatus.STATUS_DELETE_PENDING:
                    throw new NasMountException("storage_unavailable");
                case NTStatus.STATUS_NETWORK_NAME_DELETED or NTStatus.STATUS_USER_SESSION_DELETED:
                    throw new SessionLostException();
                default:
                    throw new IOException("The SMB server answered " + status + ".");
            }
        }

        private SmbLane Shared()
        {
            lock (_lanes)
            {
                // A share that was closed opens no new session.
                if (_disposed) throw new NasMountException("storage_unavailable");
                if (_all.Count == 0) _all.Add(new SmbLane(connect, time));
                return _all[0];
            }
        }
    }

    // One signed-in session with a thread of its own, since SMBLibrary is not thread-safe: requests wait in a short queue and run one after another,
    // and no caller's thread ever waits inside the library. A request past its deadline breaks the lane: the thread stays with the request the NAS
    // does not answer, while everything queued and every new request fails at once with storage_unavailable. When that request returns, its session
    // is dropped together with the files opened on it, and the next request connects anew.
    internal sealed class SmbLane : IDisposable
    {
        // A request on an awake session, and one piece of a file.
        public static readonly TimeSpan RequestTime = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan PieceTime = TimeSpan.FromSeconds(30);

        // The first request on a new or long unused session may wait for the NAS's disks to spin up, so it gets longer than SMB's own 30 s.
        public static readonly TimeSpan WakeTime = TimeSpan.FromSeconds(35);
        private static readonly TimeSpan WakeIdle = TimeSpan.FromMinutes(5);

        // Requests waiting at most; the thread ends after a while without any and starts again with the next.
        private const int QueueLength = 256;
        private static readonly TimeSpan ThreadIdle = TimeSpan.FromSeconds(30);

        private readonly Func<SmbSession> _connect;
        private readonly TimeProvider _time;
        private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(QueueLength);
        private readonly Lock _state = new();
        private Thread? _thread;
        private Work? _running;
        private bool _broken;
        private bool _disposed;
        private SmbSession? _session;
        private int _generation;
        private int _openStreams;
        private DateTimeOffset _lastUse;

        public SmbLane(Func<SmbSession> connect, TimeProvider time)
        {
            _connect = connect;
            _time = time;
            _lastUse = time.GetUtcNow();
        }

        // The session that is open now; files compare it before every request.
        public int Generation => Volatile.Read(ref _generation);

        // Read without the queue, so choosing a session never waits behind a running request.
        public int OpenStreams => Volatile.Read(ref _openStreams);

        public bool IsOpen => Volatile.Read(ref _session) is not null;

        // The lane's own thread, where nothing may wait for the lane.
        public bool IsCurrent => Thread.CurrentThread == Volatile.Read(ref _thread);

        // A request on this session; one of an earlier session (generation) fails with IOException.
        public Task<T> RunAsync<T>(Func<ISMBFileStore, T> request, TimeSpan deadline, CancellationToken ct, int? generation = null) =>
            Post(new Work<T>(store => request(store!), deadline, generation, true), ct);

        public void Opened() => Interlocked.Increment(ref _openStreams);

        public void Closed() => Interlocked.Decrement(ref _openStreams);

        // A session without an open file that went unused for the idle time closes; a lane that is broken or gone is left alone.
        public async Task CloseIfIdleAsync(TimeSpan idle)
        {
            if (!IsOpen || OpenStreams > 0)
            {
                return;
            }

            try
            {
                await Post(new Work<bool>(_ =>
                {
                    if (OpenStreams == 0 && _time.GetUtcNow() - _lastUse >= idle) Reset(false);
                    return true;
                }, RequestTime, null, false), CancellationToken.None);
            }
            catch (Exception exception) when (exception is NasMountException or IOException)
            {
            }
        }

        // Fails what waits and returns at once; the thread closes the session once its running request returns.
        public void Dispose()
        {
            lock (_state)
            {
                if (_disposed) return;
                _disposed = true;
                _queue.Writer.TryComplete();
                if (_thread is null) Start();
            }

            FailQueued(() => new NasMountException("storage_unavailable"));
        }

        private Task<T> Post<T>(Work<T> work, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);
            work.Watch(ct);
            lock (_state)
            {
                if (_disposed || _broken || !_queue.Writer.TryWrite(work))
                {
                    // Gone, broken by a request the NAS does not answer, or more waiting than the session can work off.
                    work.Fail(_broken ? Hung() : new NasMountException("storage_unavailable"));
                    return work.Task;
                }

                if (_thread is null) Start();
            }

            return work.Task;
        }

        private void Start()
        {
            _thread = new Thread(Serve) { IsBackground = true, Name = "Jularr SMB lane" };
            _thread.Start();
        }

        private void Serve()
        {
            while (true)
            {
                if (_queue.Reader.TryRead(out var work))
                {
                    Execute(work);
                    continue;
                }

                var waiting = _queue.Reader.WaitToReadAsync().AsTask();
                if (waiting.Wait(ThreadIdle) && waiting.Result)
                {
                    continue;
                }

                lock (_state)
                {
                    if (!_disposed)
                    {
                        // Work that came meanwhile keeps the thread; otherwise it ends and the next request starts another.
                        if (_queue.Reader.Count > 0) continue;
                        _thread = null;
                        return;
                    }
                }

                // The lane is gone: its session goes with it.
                Reset(false);
                return;
            }
        }

        private void Execute(Work work)
        {
            // A caller that gave up while it waited needs nothing.
            if (work.IsDone)
            {
                return;
            }

            lock (_state)
            {
                if (_disposed || _broken)
                {
                    work.Fail(_disposed ? new NasMountException("storage_unavailable") : Hung());
                    return;
                }

                _running = work;
            }

            var now = _time.GetUtcNow();
            var deadline = work.Connects && (_session is null || now - _lastUse >= WakeIdle) && WakeTime > work.Deadline ? WakeTime : work.Deadline;
            if (work.Connects) _lastUse = now;
            var watch = _time.CreateTimer(static state =>
            {
                var (lane, overran) = ((SmbLane, Work))state!;
                lane.Overran(overran);
            }, (this, work), deadline, Timeout.InfiniteTimeSpan);
            bool broken;
            try
            {
                if (work.Generation is { } expected && expected != _generation)
                {
                    throw new IOException("The SMB session was renewed.");
                }

                work.Run(work.Connects ? Session().Store : null);
            }
            catch (SessionLostException)
            {
                Reset(false);
                work.Fail(new NasMountException("storage_unavailable"));
            }
            catch (Exception exception) when (exception is NasMountException or UnauthorizedAccessException or IOException)
            {
                work.Fail(exception);
            }
            catch (Exception)
            {
                // The library reports a lost connection as an invalid operation.
                Reset(false);
                work.Fail(new NasMountException("storage_unavailable"));
            }
            finally
            {
                lock (_state)
                {
                    _running = null;
                    broken = _broken;
                }

                watch.Dispose();
            }

            if (broken)
            {
                // The NAS answered after all, too late: the session goes, and with it every file opened on it; the next request connects anew.
                Reset(true);
                lock (_state) _broken = false;
            }
        }

        // A request past its deadline: its caller and everything queued behind it fail now, not when the NAS answers.
        private void Overran(Work work)
        {
            lock (_state)
            {
                if (!ReferenceEquals(_running, work)) return;
                _broken = true;
            }

            work.Fail(Hung());
            FailQueued(Hung);
        }

        private void FailQueued(Func<Exception> failure)
        {
            while (_queue.Reader.TryRead(out var queued)) queued.Fail(failure());
        }

        // The open session, or a new one; on the lane's thread only, like everything that touches the library.
        private SmbSession Session()
        {
            if (_session is { } open)
            {
                return open;
            }

            var session = _connect();
            Volatile.Write(ref _session, session);
            return session;
        }

        // On the lane's thread only.
        private void Reset(bool abort)
        {
            if (_session is not { } session)
            {
                return;
            }

            Volatile.Write(ref _session, null);
            Interlocked.Increment(ref _generation);
            try
            {
                // A session that stopped answering is cut off instead of signed out, which would wait for the NAS again.
                (abort ? session.Abort ?? session.Close : session.Close)();
            }
            catch (Exception)
            {
                // A dead connection has nothing left to close.
            }
        }

        // The NAS did not answer in time: callers read storage out of reach, the storage gate a timeout.
        private static NasMountException Hung() => new("storage_unavailable", new TimeoutException("The SMB server did not answer in time."));

        private abstract class Work(TimeSpan deadline, int? generation, bool connects)
        {
            public TimeSpan Deadline { get; } = deadline;
            public int? Generation { get; } = generation;

            // A request connects and counts as use; housekeeping such as closing an idle session does neither.
            public bool Connects { get; } = connects;

            public abstract bool IsDone { get; }

            public abstract void Run(ISMBFileStore? store);

            public abstract void Fail(Exception exception);
        }

        private sealed class Work<T>(Func<ISMBFileStore?, T> body, TimeSpan deadline, int? generation, bool connects) : Work(deadline, generation, connects)
        {
            private readonly TaskCompletionSource<T> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private CancellationTokenRegistration _cancel;

            public Task<T> Task => _answer.Task;

            public override bool IsDone => _answer.Task.IsCompleted;

            // A caller that stops waiting is free at once; a request already running finishes on the lane.
            public void Watch(CancellationToken ct)
            {
                if (ct.CanBeCanceled) _cancel = ct.UnsafeRegister(static (state, token) => ((TaskCompletionSource<T>)state!).TrySetCanceled(token), _answer);
            }

            public override void Run(ISMBFileStore? store)
            {
                var result = body(store);

                // A file opened for a caller that gave up closes again.
                if (!_answer.TrySetResult(result) && result is IDisposable abandoned) abandoned.Dispose();
                _cancel.Dispose();
            }

            public override void Fail(Exception exception)
            {
                _answer.TrySetException(exception);
                _cancel.Dispose();
            }
        }
    }

    // The server ended the session; the next request opens a new one.
    internal sealed class SessionLostException : Exception;

    // An open tree connection, how to end it, and how to cut it off when the server no longer answers.
    internal sealed record SmbSession(ISMBFileStore Store, Action Close, Action? Abort = null);
}
