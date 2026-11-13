using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using Jularr.Integrations.Data.Usenet;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Integrations.Logic.Usenet
{
    // Downloads the open articles of a job with several sessions until a deadline; decoded parts are written where they belong.
    // Like SABnzbd: sessions stay open between passes, each keeps several requests in flight, and disk writes never hold up the line.
    internal sealed class UsenetDownloader : IAsyncDisposable
    {
        public const int MaximumConnections = 100;
        private const long MaximumFileBytes = 200L * 1024 * 1024 * 1024;
        private const long Slack = 1024 * 1024;

        // Requests one session keeps in flight; SABnzbd sends two for new servers.
        private const int Pipeline = 2;

        // Free space is checked and reserved once per block, not for every article.
        private const long ReservationBlock = 256L * 1024 * 1024;

        // Decoded parts waiting for the disk, and the writers that take them.
        private const int WriteQueue = 64;
        private const int Writers = 4;

        // A session that fails is replaced this often per pass before its slot gives up.
        private const int Reconnects = 3;

        // Open sessions wait this long for the next pass, well below the usual server idle timeout.
        private static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(30);
        private readonly ConcurrentDictionary<string, ConcurrentStack<(NntpConnection Session, long Since)>> _idle = new(StringComparer.Ordinal);
        private readonly Timer _sweeper;

        public UsenetDownloader()
        {
            _sweeper = new Timer(_ => _ = SweepAsync(), null, IdleLimit, IdleLimit);
        }

        public async Task<UsenetProgress> DownloadAsync(UsenetServer server, IReadOnlyList<NzbFile> files, string directory, IReadOnlySet<string> finished, DateTimeOffset until,
            long maximumJobBytes, CancellationToken ct, long minimumFreeBytes = 0, Func<long, IDisposable>? reserveWrite = null, Func<long, CancellationToken, ValueTask>? throttle = null, Func<long, Task>? report = null)
        {
            var queue = new ConcurrentQueue<(NzbFile File, NzbSegment Segment)>(files.SelectMany(file => file.Segments.Where(segment => !finished.Contains(segment.MessageId))
                .Select(segment => (file, segment))));
            var done = new ConcurrentBag<string>();
            var damaged = new ConcurrentBag<string>();
            long downloaded = 0;

            // Completed/resumed files still consume the budget, even with no articles left.
            long existingBytes = 0;
            if (maximumJobBytes <= 0)
            {
                throw new UsenetStorageBudgetException();
            }

            foreach (var file in files)
            {
                var existing = new FileInfo(Path.Combine(directory, file.FileName));
                if (existing.Exists)
                {
                    if (existing.Length > maximumJobBytes - existingBytes)
                    {
                        throw new UsenetStorageBudgetException();
                    }

                    existingBytes += existing.Length;
                }
            }

            if (queue.IsEmpty)
            {
                return new UsenetProgress([], [], 0, 0);
            }

            // The first session proves the server; further sessions are a bonus.
            var key = Key(server);
            NntpConnection? first = await TakeAsync(server, key, ct);
            var handles = new Dictionary<string, SafeFileHandle>(StringComparer.Ordinal);
            var fileLengths = new Dictionary<string, long>(StringComparer.Ordinal);
            var reservations = new List<IDisposable?>();
            using var reserving = new SemaphoreSlim(1);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var writes = Channel.CreateBounded<(SafeFileHandle Handle, byte[] Data, int Length, long Offset, string MessageId, bool Intact)>(WriteQueue);
            long allocated = 0;
            long reserved = 0;
            var sessions = 0;
            Exception? failure = null;
            Task[] writers = [];

            // The first real failure ends the pass for every session and writer and is what the caller sees.
            void Fail(Exception exception)
            {
                if (exception is OperationCanceledException && stop.IsCancellationRequested) return;
                Interlocked.CompareExchange(ref failure, exception, null);
                try
                {
                    stop.Cancel();
                }
                catch (AggregateException)
                {
                }
            }

            try
            {
                foreach (var file in files)
                {
                    var handle = File.OpenHandle(Path.Combine(directory, file.FileName), FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous);
                    handles[file.FileName] = handle;
                    var existingLength = RandomAccess.GetLength(handle);
                    if (maximumJobBytes <= 0 || existingLength > maximumJobBytes - allocated)
                    {
                        throw new UsenetStorageBudgetException();
                    }

                    fileLengths[file.FileName] = existingLength;
                    allocated += existingLength;
                }

                // Existing files are on disk already; only growth needs space, and never more than the open articles announce.
                reserved = allocated;
                var needed = Math.Min(maximumJobBytes, allocated + queue.Sum(item => item.Segment.Bytes + Slack));
                writers = [.. Enumerable.Range(0, Writers).Select(_ => Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var part in writes.Reader.ReadAllAsync(stop.Token))
                        {
                            try
                            {
                                await RandomAccess.WriteAsync(part.Handle, part.Data.AsMemory(0, part.Length), part.Offset, stop.Token);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(part.Data);
                            }

                            var received = Interlocked.Add(ref downloaded, part.Length);
                            if (report is not null) await report(received);
                            (part.Intact ? done : damaged).Add(part.MessageId);
                        }
                    }
                    catch (Exception exception)
                    {
                        Fail(exception);
                    }
                }, CancellationToken.None))];

                // Reserves the next block of space outside the accounting lock; a slow storage check holds up no other session.
                async Task ReserveAsync(long growth)
                {
                    await reserving.WaitAsync(stop.Token);
                    try
                    {
                        long block;
                        lock (fileLengths)
                        {
                            if (reserved - allocated >= growth) return;
                            block = Math.Max(growth, Math.Min(ReservationBlock, needed - reserved));
                        }

                        if (reserveWrite is null)
                        {
                            if (StorageFileSystem.AvailableFreeSpace(directory) is not { } available || block > available - minimumFreeBytes)
                            {
                                throw new UsenetStorageBudgetException();
                            }

                            reservations.Add(null);
                        }
                        else
                        {
                            reservations.Add(reserveWrite(block));
                        }

                        // Blocks before the last one are written and already count in the free space.
                        for (var index = 0; index < reservations.Count - 2; index++)
                        {
                            reservations[index]?.Dispose();
                            reservations[index] = null;
                        }

                        lock (fileLengths) reserved += block;
                    }
                    finally
                    {
                        reserving.Release();
                    }
                }

                // Hands the decoded part to the writers once its extent fits the budget and the reserved space.
                async Task<bool> QueueAsync(NzbFile file, NzbSegment segment, byte[] data, long offset, int length, bool intact)
                {
                    // A part never grows its file beyond what the NZB announced.
                    var limit = file.Bytes > 0 ? file.Bytes + Slack : MaximumFileBytes;
                    if (offset < 0 || offset > limit - length || segment.Bytes > 0 && length > segment.Bytes + Slack)
                    {
                        return false;
                    }

                    // The physical extent is accounted under a lock before concurrent sessions write. Files can arrive out of offset order.
                    while (true)
                    {
                        long growth;
                        lock (fileLengths)
                        {
                            var end = offset + length;
                            growth = Math.Max(0, end - fileLengths[file.FileName]);
                            if (growth > maximumJobBytes - allocated)
                            {
                                throw new UsenetStorageBudgetException();
                            }

                            if (growth <= reserved - allocated)
                            {
                                allocated += growth;
                                fileLengths[file.FileName] = Math.Max(fileLengths[file.FileName], end);
                                break;
                            }
                        }

                        await ReserveAsync(growth);
                    }

                    await writes.Writer.WriteAsync((handles[file.FileName], data, length, offset, segment.MessageId, intact), stop.Token);
                    return true;
                }

                // One slot: a session, replaced when it drops, that keeps the pipeline full until the deadline.
                async Task RunAsync(NntpConnection? session)
                {
                    var inFlight = new Queue<(NzbFile File, NzbSegment Segment)>();
                    var replaced = 0;
                    var counted = session is not null;
                    try
                    {
                        while (true)
                        {
                            if (session is null || session.IsBroken)
                            {
                                // Unread requests of a lost session go back to the queue.
                                while (inFlight.TryDequeue(out var lost)) queue.Enqueue(lost);
                                if (session is not null)
                                {
                                    await session.DisposeAsync();
                                    session = null;
                                    if (++replaced > Reconnects) return;
                                }

                                if (DateTimeOffset.UtcNow >= until || queue.IsEmpty) return;
                                try
                                {
                                    session = await TakeAsync(server, key, stop.Token);
                                }
                                catch (UsenetException)
                                {
                                    return;
                                }

                                if (!counted) Interlocked.Increment(ref sessions);
                                counted = true;
                            }

                            // Keep the pipeline full while the pass lasts.
                            var requests = new List<string>();
                            while (inFlight.Count < Pipeline && DateTimeOffset.UtcNow < until && queue.TryDequeue(out var next))
                            {
                                if (next.Segment.MessageId.AsSpan().IndexOfAny('\r', '\n') >= 0)
                                {
                                    damaged.Add(next.Segment.MessageId);
                                    continue;
                                }

                                inFlight.Enqueue(next);
                                requests.Add(next.Segment.MessageId);
                            }

                            if (requests.Count > 0)
                            {
                                try
                                {
                                    await session.RequestBodiesAsync(requests, stop.Token);
                                }
                                catch (UsenetException)
                                {
                                    continue;
                                }
                            }

                            if (!inFlight.TryPeek(out var item))
                            {
                                return;
                            }

                            ArticleBody body;
                            try
                            {
                                body = await session.ReadBodyAsync(stop.Token);
                            }
                            catch (UsenetException exception) when (exception.Failure is UsenetFailure.ArticleMissing or UsenetFailure.InvalidResponse)
                            {
                                // Missing or broken articles are left to the repair files; a session left mid-answer is replaced.
                                inFlight.Dequeue();
                                damaged.Add(item.Segment.MessageId);
                                continue;
                            }
                            catch (UsenetException)
                            {
                                // A dropped session hands its articles back.
                                continue;
                            }

                            inFlight.Dequeue();
                            byte[] data;
                            long offset;
                            int length;
                            bool intact;
                            using (body)
                            {
                                data = ArrayPool<byte>.Shared.Rent(Math.Max(1, body.Length));
                                if (!YencDecoder.TryDecode(body.Span, data, out offset, out length, out intact))
                                {
                                    ArrayPool<byte>.Shared.Return(data);
                                    damaged.Add(item.Segment.MessageId);
                                    continue;
                                }
                            }

                            var queued = false;
                            try
                            {
                                // The client's speed limit holds every session back by what it read.
                                if (throttle is not null)
                                {
                                    await throttle(length, stop.Token);
                                }

                                queued = await QueueAsync(item.File, item.Segment, data, offset, length, intact);
                            }
                            finally
                            {
                                if (!queued) ArrayPool<byte>.Shared.Return(data);
                            }

                            if (!queued)
                            {
                                damaged.Add(item.Segment.MessageId);
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        while (inFlight.TryDequeue(out var lost)) queue.Enqueue(lost);
                        Fail(exception);
                    }
                    finally
                    {
                        // A session in step with the server waits for the next pass.
                        if (session is not null)
                        {
                            if (inFlight.Count == 0 && !session.IsBroken && failure is null) Return(key, session);
                            else await session.DisposeAsync();
                        }
                    }
                }

                // All further sessions connect at once and start as soon as each is ready.
                sessions = 1;
                var slots = Math.Min(Math.Clamp(server.Connections, 1, MaximumConnections), queue.Count);
                var workers = new List<Task>(slots) { RunAsync(first) };
                first = null;
                for (var index = 1; index < slots; index++)
                {
                    workers.Add(Task.Run(() => RunAsync(null), CancellationToken.None));
                }

                await Task.WhenAll(workers);
                writes.Writer.TryComplete();
                await Task.WhenAll(writers);
            }
            finally
            {
                writes.Writer.TryComplete();
                if (writers.Length > 0 && writers.Any(writer => !writer.IsCompleted))
                {
                    await stop.CancelAsync();
                    await Task.WhenAll(writers);
                }

                while (writes.Reader.TryRead(out var left)) ArrayPool<byte>.Shared.Return(left.Data);
                foreach (var reservation in reservations) reservation?.Dispose();
                foreach (var handle in handles.Values)
                {
                    handle.Dispose();
                }

                if (first is not null) await first.DisposeAsync();
            }

            ct.ThrowIfCancellationRequested();
            if (failure is not null) ExceptionDispatchInfo.Throw(failure);
            return new UsenetProgress([.. done], [.. damaged], downloaded, sessions);
        }

        public async Task TestAsync(UsenetServer server, CancellationToken ct)
        {
            await using var session = await NntpConnection.OpenAsync(server, ct);
            await session.CheckAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            await _sweeper.DisposeAsync();
            foreach (var stack in _idle.Values)
            {
                while (stack.TryPop(out var idle)) await idle.Session.DisposeAsync();
            }
        }

        // An open session of the same account is reused while it is fresh; otherwise a new one logs in.
        private async Task<NntpConnection> TakeAsync(UsenetServer server, string key, CancellationToken ct)
        {
            if (_idle.TryGetValue(key, out var stack))
            {
                while (stack.TryPop(out var idle))
                {
                    if (TimeProvider.System.GetElapsedTime(idle.Since) < IdleLimit) return idle.Session;
                    await idle.Session.DisposeAsync();
                }
            }

            return await NntpConnection.OpenAsync(server, ct);
        }

        private void Return(string key, NntpConnection session) =>
            _idle.GetOrAdd(key, _ => new ConcurrentStack<(NntpConnection, long)>()).Push((session, TimeProvider.System.GetTimestamp()));

        // Sessions nobody took for a while are closed, so they do not hold the account's connections.
        private async Task SweepAsync()
        {
            foreach (var stack in _idle.Values)
            {
                var keep = new List<(NntpConnection Session, long Since)>();
                while (stack.TryPop(out var idle))
                {
                    if (TimeProvider.System.GetElapsedTime(idle.Since) < IdleLimit) keep.Add(idle);
                    else await idle.Session.DisposeAsync();
                }

                for (var index = keep.Count - 1; index >= 0; index--) stack.Push(keep[index]);
            }
        }

        // Sessions are shared only between passes for the same server and login.
        private static string Key(UsenetServer server) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', server.Host, server.Port, server.UseTls, server.Username, server.Password))));
    }
}
