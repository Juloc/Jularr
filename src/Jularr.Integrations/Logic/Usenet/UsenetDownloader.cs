using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using Jularr.Integrations.Data.Usenet;

namespace Jularr.Integrations.Logic.Usenet
{
    // Downloads the open articles of a job with several sessions until a deadline; decoded parts are written where they belong.
    internal sealed class UsenetDownloader
    {
        private const long MaximumFileBytes = 200L * 1024 * 1024 * 1024;

        public async Task<UsenetProgress> DownloadAsync(UsenetServer server, IReadOnlyList<NzbFile> files, string directory, IReadOnlySet<string> finished, DateTimeOffset until,
            CancellationToken ct)
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
                    handles[file.FileName] = File.OpenHandle(Path.Combine(directory, file.FileName), FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous);
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
                            // Missing or broken articles are left to the repair files.
                            damaged.Add(item.Segment.MessageId);
                            continue;
                        }
                        catch (UsenetException)
                        {
                            // A dropped session hands its article back.
                            queue.Enqueue(item);
                            return;
                        }

                        if (part.Offset < 0 || part.Offset + part.Data.Length > MaximumFileBytes)
                        {
                            damaged.Add(item.Segment.MessageId);
                            continue;
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
