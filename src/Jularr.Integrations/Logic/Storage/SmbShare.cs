using Jularr.Integrations.Data.Nas;
using SMBLibrary;
using SMBLibrary.Client;

namespace Jularr.Integrations.Logic.Storage
{
    // Where an SMB share is and who signs in; the password only lives here while a session may open.
    internal sealed record SmbAddress(string Host, string Share, string? Domain, string Username, string Password);

    // One signed-in session to an SMB share. Requests take turns; a lost session opens again on the next request. Open streams keep it in use.
    internal sealed class SmbShare(Func<SmbSession> connect, TimeProvider time) : IDisposable
    {
        // A NAS whose disks spin up answers slowly.
        private const int ResponseTimeoutMilliseconds = 30000;

        private readonly Lock _turn = new();
        private SmbSession? _session;
        private int _generation;
        private int _openStreams;
        private DateTimeOffset _lastUse = time.GetUtcNow();

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

        // The session that is open now; streams compare it before every request.
        public int Generation
        {
            get
            {
                lock (_turn) return _generation;
            }
        }

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

        public Task<T> RunAsync<T>(Func<ISMBFileStore, T> request, CancellationToken ct) => Task.Run(() => Run(request), ct);

        public void Opened()
        {
            lock (_turn) _openStreams++;
        }

        public void Closed()
        {
            lock (_turn) _openStreams--;
        }

        // Closes the session after the idle time so the NAS may sleep.
        public void CloseIfIdle(TimeSpan idle)
        {
            lock (_turn)
            {
                if (_openStreams == 0 && time.GetUtcNow() - _lastUse >= idle)
                {
                    Reset();
                }
            }
        }

        public void Dispose()
        {
            lock (_turn) Reset();
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
