using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.Core
{
    // Only operation-shaped folders below the configured, UUID-scoped download workspace
    // may ever be cleaned. Scans rotate instead of inspecting the same first 32 entries forever.
    internal sealed class DirectDownloadCleanup : IDisposable
    {
        private const int MaximumPerRoot = 32;
        private static readonly TimeSpan TerminalGrace = TimeSpan.FromDays(1);
        private static readonly TimeSpan MissingGrace = TimeSpan.FromDays(7);
        private long _scanned;
        private long _deleted;
        private long _skipped;
        private long _errors;
        private long _reclaimedBytes;
        private readonly object _cursorGate = new();
        private readonly Dictionary<string, IEnumerator<string>> _cursors = new(StringComparer.Ordinal);

        internal sealed record Candidate(long OperationId, string Root, string Name, DateTime ModifiedAt);

        public (long Scanned, long Deleted, long Skipped, long Errors, long ReclaimedBytes) Stats =>
            (Interlocked.Read(ref _scanned), Interlocked.Read(ref _deleted), Interlocked.Read(ref _skipped),
                Interlocked.Read(ref _errors), Interlocked.Read(ref _reclaimedBytes));

        public void Report(int scanned, int deleted, int skipped, int errors, long reclaimedBytes)
        {
            Interlocked.Add(ref _scanned, scanned);
            Interlocked.Add(ref _deleted, deleted);
            Interlocked.Add(ref _skipped, skipped);
            Interlocked.Add(ref _errors, errors);
            Interlocked.Add(ref _reclaimedBytes, reclaimedBytes);
        }

        public IReadOnlyList<Candidate> Candidates(DownloadStoragePaths paths, DateTime now)
        {
            // Keep two lazy directory enumerators, not an unbounded array of folder names.
            // Each maintenance pass reads at most 32 entries per workspace root.
            lock (_cursorGate)
            {
                var roots = new[] { paths.IncompleteRoot, paths.CompleteRoot };
                foreach (var obsolete in _cursors.Keys.Except(roots, StringComparer.Ordinal).ToArray())
                    Reset(obsolete);

                var found = new List<Candidate>();
                foreach (var root in roots)
                {
                    if (!Directory.Exists(root))
                    {
                        Reset(root);
                        continue;
                    }

                    if (!_cursors.TryGetValue(root, out var cursor))
                    {
                        cursor = Directory.EnumerateDirectories(root).GetEnumerator();
                        _cursors[root] = cursor;
                    }

                    try
                    {
                        for (var inspected = 0; inspected < MaximumPerRoot; inspected++)
                        {
                            if (!cursor.MoveNext())
                            {
                                Reset(root);
                                break;
                            }

                            var path = cursor.Current;
                            var name = Path.GetFileName(path);
                            if (!TryParseName(name, out var operationId)) continue;
                            var entry = new DirectoryInfo(path);
                            if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if (entry.LastWriteTimeUtc > now - TerminalGrace) continue;
                            found.Add(new Candidate(operationId, root, name, entry.LastWriteTimeUtc));
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        Reset(root);
                        throw;
                    }
                }

                return found;
            }
        }

        private void Reset(string root)
        {
            if (_cursors.Remove(root, out var cursor)) cursor.Dispose();
        }

        public void Dispose()
        {
            lock (_cursorGate)
            {
                foreach (var root in _cursors.Keys.ToArray()) Reset(root);
            }
        }

        public static bool TryParseName(string name, out long operationId)
        {
            operationId = 0;
            var id = name.StartsWith("partial-", StringComparison.Ordinal) ? name[8..] : name;
            return id.Length > 0 && id.Length <= 19 && id.All(char.IsAsciiDigit)
                && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out operationId) && operationId > 0;
        }

        public static bool CanRemove(Candidate candidate, bool isFinished, bool isMissing, DateTime now) =>
            isFinished && now - candidate.ModifiedAt >= (isMissing ? MissingGrace : TerminalGrace);

        public static long Remove(DownloadStoragePaths paths, Candidate candidate, DateTime now, bool isMissing)
        {
            var root = candidate.Root;
            var expected = root == paths.IncompleteRoot ? paths.Settings.IncompleteFilesystemIdentity
                : root == paths.CompleteRoot ? paths.Settings.CompleteFilesystemIdentity : null;
            if (expected is null || !TryParseName(candidate.Name, out _))
                throw new InvalidDataException("The directory is outside the direct download workspace.");

            // Verify mount identity and symlink safety again immediately before deletion.
            var mount = Path.GetDirectoryName(root)!;
            if (StorageFileSystem.DownloadFilesystemIdentity(mount) != expected)
                throw new ServiceUnavailableException("download_storage_unavailable");
            var safeRoot = StorageFileSystem.ResolveDirectory(mount, Path.GetFileName(root));
            var directory = StorageFileSystem.ResolveDirectory(safeRoot, candidate.Name);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Symbolic links are never cleaned.");

            // Do not erase freshly written nested files just because the directory timestamp is old.
            // Inspect at most 10,000 entries in this candidate and fail closed on links or oversized trees.
            var grace = isMissing ? MissingGrace : TerminalGrace;
            var deadline = now - grace;
            var remaining = new Stack<string>();
            remaining.Push(directory);
            var entries = 0;
            long bytes = 0;
            while (remaining.TryPop(out var current))
            {
                foreach (var item in Directory.EnumerateFileSystemEntries(current))
                {
                    if (++entries > 10_000) throw new InvalidDataException("The orphaned folder is too large for safe cleanup.");
                    var attributes = File.GetAttributes(item);
                    if ((attributes & FileAttributes.ReparsePoint) != 0 || File.GetLastWriteTimeUtc(item) > deadline)
                        throw new InvalidDataException("The orphaned folder contains a link or a newer entry.");
                    if ((attributes & FileAttributes.Directory) != 0)
                        remaining.Push(item);
                    else
                        bytes = checked(bytes + new FileInfo(item).Length);
                }
            }

            Directory.Delete(directory, recursive: true);
            return bytes;
        }
    }
}
