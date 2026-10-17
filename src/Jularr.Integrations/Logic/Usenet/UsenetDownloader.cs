using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using Jularr.Integrations.Data.Usenet;

namespace Jularr.Integrations.Logic.Usenet
{
    internal sealed class UsenetStorageBudgetException : Exception;

    // Downloads the open articles of a job with several sessions until a deadline; decoded parts are written where they belong.
    internal sealed class UsenetDownloader
    {
        private const long MaximumFileBytes = 200L * 1024 * 1024 * 1024;
        private const long Slack = 1024 * 1024;

        public async Task<UsenetProgress> DownloadAsync(UsenetServer server, IReadOnlyList<NzbFile> files, string directory, IReadOnlySet<string> finished, DateTimeOffset until,
            long maximumJobBytes, CancellationToken ct)
        {
            var queue = new ConcurrentQueue<(NzbFile File, NzbSegment Segment)>(files.SelectMany(file => file.Segments.Where(segment => !finished.Contains(segment.MessageId))
                .Select(segment => (file, segment))));
            var done = new ConcurrentBag<string>();
            var damaged = new ConcurrentBag<string>();
            long downloaded = 0;
            if (queue.IsEmpty)
            {
                return new UsenetProgress([], [], 0);
            }

            // The first session proves the server; further sessions are a bonus.
            var sessions = new List<NntpConnection> { await NntpConnection.OpenAsync(server, ct) };
            var handles = new Dictionary<string, SafeFileHandle>(StringComparer.Ordinal);
            var fileLengths = new Dictionary<string, long>(StringComparer.Ordinal);
            long allocated = 0;
            try
            {
                for (var index = 1; index < Math.Clamp(server.Connections, 1, 50) && index < queue.Count; index++)
                {
                    try
                    {
                        sessions.Add(await NntpConnection.OpenAsync(server, ct));
                    }
                    catch (UsenetException)
                    {
                        break;
                    }
                }

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

                await Task.WhenAll(sessions.Select(async session =>
                {
                    while (DateTimeOffset.UtcNow < until && queue.TryDequeue(out var item))
                    {
                        YencPart part;
                        try
                        {
                            part = YencDecoder.Decode(await session.ReadBodyAsync(item.Segment.MessageId, ct));
                        }
                        catch (UsenetException exception) when (exception.Failure is UsenetFailure.ArticleMissing or UsenetFailure.InvalidResponse)
                        {
                            // Missing or broken articles are left to the repair files; a session left mid-answer stops.
                            damaged.Add(item.Segment.MessageId);
                            if (session.IsBroken)
                            {
                                return;
                            }

                            continue;
                        }
                        catch (UsenetException)
                        {
                            // A dropped session hands its article back.
                            queue.Enqueue(item);
                            return;
                        }

                        // A part never grows its file beyond what the NZB announced.
                        var limit = item.File.Bytes > 0 ? item.File.Bytes + Slack : MaximumFileBytes;
                        if (part.Offset < 0 || part.Offset + part.Data.Length > limit || item.Segment.Bytes > 0 && part.Data.Length > item.Segment.Bytes + Slack)
                        {
                            damaged.Add(item.Segment.MessageId);
                            continue;
                        }

                        // Reserve the resulting physical file extent under a shared lock, before
                        // concurrent NNTP sessions write. Files can arrive out of offset order.
                        lock (fileLengths)
                        {
                            var end = part.Offset + part.Data.Length;
                            var growth = Math.Max(0, end - fileLengths[item.File.FileName]);
                            if (growth > maximumJobBytes - allocated)
                            {
                                throw new UsenetStorageBudgetException();
                            }

                            allocated += growth;
                            fileLengths[item.File.FileName] = Math.Max(fileLengths[item.File.FileName], end);
                        }

                        await RandomAccess.WriteAsync(handles[item.File.FileName], part.Data, part.Offset, ct);
                        Interlocked.Add(ref downloaded, part.Data.Length);
                        (part.IsIntact ? done : damaged).Add(item.Segment.MessageId);
                    }
                }));
            }
            finally
            {
                foreach (var handle in handles.Values)
                {
                    handle.Dispose();
                }

                foreach (var session in sessions)
                {
                    await session.DisposeAsync();
                }
            }

            return new UsenetProgress([.. done], [.. damaged], downloaded);
        }

        public async Task TestAsync(UsenetServer server, CancellationToken ct)
        {
            await using var session = await NntpConnection.OpenAsync(server, ct);
            await session.CheckAsync(ct);
        }
    }
}
