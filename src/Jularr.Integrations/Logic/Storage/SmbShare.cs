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
                });
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
        public T Run<T>(Func<ISMBFileStore, T> request) => Shared().Run(request);

        public Task<T> RunAsync<T>(Func<ISMBFileStore, T> request, CancellationToken ct) => Task.Run(() => Run(request), ct);

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

        // Closes sessions after the idle time so the NAS may sleep; a session with an open file stays.
        public void CloseIfIdle(TimeSpan idle)
        {
            lock (_lanes)
            {
                foreach (var lane in _all) lane.CloseIfIdle(idle);
            }
        }

        public void Dispose()
        {
            lock (_lanes)
            {
                foreach (var lane in _all) lane.Dispose();
                _all.Clear();
            }
        }

        // Missing paths and a share out of reach read as unavailable storage, refusals as denied access.
        public static void Check(NTStatus status)
        {
            switch (status)
            {
                case NTStatus.STATUS_SUCCESS:
                    return;
                case NTStatus.STATUS_ACCESS_DENIED or NTStatus.STATUS_LOGON_FAILURE:
                    throw new UnauthorizedAccessException("The SMB server refused access.");
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
                if (_all.Count == 0) _all.Add(new SmbLane(connect, time));
                return _all[0];
            }
        }
    }

    // One signed-in session: requests take turns, a lost session opens again on the next request.
    internal sealed class SmbLane(Func<SmbSession> connect, TimeProvider time) : IDisposable
    {
        private readonly Lock _turn = new();
        private SmbSession? _session;
        private int _generation;
        private int _openStreams;
        private DateTimeOffset _lastUse = time.GetUtcNow();

        // The session that is open now; files compare it before every request.
        public int Generation
        {
            get
            {
                lock (_turn) return _generation;
            }
        }

        // Read without the turn, so choosing a session never waits behind a running request.
        public int OpenStreams => Volatile.Read(ref _openStreams);

        public bool IsOpen => Volatile.Read(ref _session) is not null;

        public T Run<T>(Func<ISMBFileStore, T> request, int? generation = null)
        {
            lock (_turn)
            {
                if (generation is { } expected && expected != _generation)
                {
                    throw new IOException("The SMB session was renewed.");
                }

                _lastUse = time.GetUtcNow();
                try
                {
                    _session ??= connect();
                    return request(_session.Store);
                }
                catch (SessionLostException)
                {
                    Reset();
                    throw new NasMountException("storage_unavailable");
                }
                catch (Exception exception) when (exception is not (NasMountException or UnauthorizedAccessException or IOException))
                {
                    // The library reports a lost connection as an invalid operation.
                    Reset();
                    throw new NasMountException("storage_unavailable");
                }
            }
        }

        public void Opened() => Interlocked.Increment(ref _openStreams);

        public void Closed() => Interlocked.Decrement(ref _openStreams);

        // A session busy with a request is not idle; the check never waits for it.
        public void CloseIfIdle(TimeSpan idle)
        {
            if (!_turn.TryEnter())
            {
                return;
            }

            try
            {
                if (OpenStreams == 0 && time.GetUtcNow() - _lastUse >= idle)
                {
                    Reset();
                }
            }
            finally
            {
                _turn.Exit();
            }
        }

        public void Dispose()
        {
            lock (_turn) Reset();
        }

        private void Reset()
        {
            if (_session is { } session)
            {
                _session = null;
                _generation++;
                try
                {
                    session.Close();
                }
                catch (Exception)
                {
                    // A dead connection has nothing left to close.
                }
            }
        }
    }

    // The server ended the session; the next request opens a new one.
    internal sealed class SessionLostException : Exception;

    // An open tree connection and how to end it.
    internal sealed record SmbSession(ISMBFileStore Store, Action Close);
}
