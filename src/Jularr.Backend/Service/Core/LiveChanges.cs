using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Jularr.Backend.Service.Core
{
    // The one place the live streams of an instance meet: it counts them, caps them and tells them when the database changed.
    // Only while a stream is open does the live worker look at the database, and it publishes a new version only when something differs.
    public sealed class LiveChanges
    {
        // Active downloads move every second, other running jobs every two, an idle instance is looked at every five.
        public static readonly TimeSpan DownloadInterval = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan JobInterval = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(5);

        // Streams of one account and of the whole instance.
        public const int MaximumPerAccount = 6;
        public const int MaximumTotal = 200;

        private readonly object _gate = new();
        private readonly Dictionary<string, int> _accounts = new(StringComparer.Ordinal);
        private int _subscribers;
        private long _sequence;
        private string? _version;
        private bool _hasDownloads;
        private TaskCompletionSource _changed = NewSignal();
        private TaskCompletionSource _joined = NewSignal();

        // A quiet stream still says it is alive, so proxies and browsers keep it open.
        public TimeSpan HeartbeatInterval { get; internal set; } = TimeSpan.FromSeconds(20);

        public int Subscribers
        {
            get
            {
                lock (_gate)
                {
                    return _subscribers;
                }
            }
        }

        // Someone watches and a download runs: its progress is read from the client every second.
        public bool WantsDownloadProgress
        {
            get
            {
                lock (_gate)
                {
                    return _subscribers > 0 && _hasDownloads;
                }
            }
        }

        public static TimeSpan IntervalFor(bool hasDownloads, bool hasJobs) => hasDownloads ? DownloadInterval : hasJobs ? JobInterval : IdleInterval;

        // Null when the account or the instance has as many streams as it may have.
        public IDisposable? TryJoin(string account)
        {
            lock (_gate)
            {
                _accounts.TryGetValue(account, out var own);
                if (_subscribers >= MaximumTotal || own >= MaximumPerAccount)
                {
                    return null;
                }

                _accounts[account] = own + 1;
                if (++_subscribers == 1)
                {
                    _version = null;
                    _joined.TrySetResult();
                }

                return new Lease(this, account);
            }
        }

        public Task WaitForSubscribersAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return _subscribers > 0 ? Task.CompletedTask : _joined.Task.WaitAsync(cancellationToken);
            }
        }

        public long Sequence
        {
            get
            {
                lock (_gate)
                {
                    return _sequence;
                }
            }
        }

        // The worker found the database in this state; only a different state wakes the streams.
        public void Publish(string version, bool hasDownloads)
        {
            TaskCompletionSource done;
            lock (_gate)
            {
                _hasDownloads = hasDownloads;
                if (version == _version)
                {
                    return;
                }

                _version = version;
                _sequence++;
                done = _changed;
                _changed = NewSignal();
            }

            done.TrySetResult();
        }

        // The sequence after the one already seen, or the same one when nothing changed within the time.
        public async Task<long> WaitForChangeAsync(long seen, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Task changed;
            lock (_gate)
            {
                if (_sequence > seen)
                {
                    return _sequence;
                }

                changed = _changed.Task;
            }

            try
            {
                await changed.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                return seen;
            }

            return Sequence;
        }

        // A stream's session is checked again for every read it makes, not only when it opened.
        public static void RequireFreshSession(HttpContext http) => http.Items.Remove(typeof(SessionAuthentication));

        // Whose stream it is: the account of a browser session, the session of a paired device.
        public static string OwnerOf(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue(ServiceGate.SessionClaimType) ?? "";

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void Leave(string account)
        {
            lock (_gate)
            {
                if (_accounts.TryGetValue(account, out var own))
                {
                    if (own <= 1)
                    {
                        _accounts.Remove(account);
                    }
                    else
                    {
                        _accounts[account] = own - 1;
                    }
                }

                if (--_subscribers == 0)
                {
                    _hasDownloads = false;
                    _version = null;
                    _joined = NewSignal();
                }
            }
        }

        private sealed class Lease(LiveChanges owner, string account) : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    owner.Leave(account);
                }
            }
        }
    }
}
