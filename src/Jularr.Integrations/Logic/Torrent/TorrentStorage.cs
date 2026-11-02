using System.Security.Cryptography;

namespace Jularr.Integrations.Logic.Torrent
{
    // The pieces of one job on disk: a piece is written only after its SHA-1 matched, and the job's state file names the verified ones.
    internal sealed class TorrentStorage(TorrentMetainfo meta, string dataDirectory)
    {
        private readonly bool[] _have = new bool[meta.PieceCount];
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly string _root = Path.GetFullPath(dataDirectory);
        private int _haveCount;

        public bool IsComplete => Volatile.Read(ref _haveCount) == meta.PieceCount;

        public bool Has(int index) => Volatile.Read(ref _have[index]);

        public long RemainingBytes
        {
            get
            {
                long remaining = 0;
                for (var index = 0; index < meta.PieceCount; index++)
                {
                    if (!Has(index)) remaining += meta.PieceSize(index);
                }

                return remaining;
            }
        }

        // The verified pieces from the state file; without one, every piece already on disk is checked again.
        public async Task LoadAsync(string statePath, CancellationToken ct)
        {
            if (File.Exists(statePath))
            {
                var state = await File.ReadAllBytesAsync(statePath, ct);
                if (state.Length == meta.PieceCount)
                {
                    for (var index = 0; index < state.Length; index++)
                    {
                        if (state[index] == 1 && Segments(index).All(segment => segment.Path is null || new FileInfo(segment.Path).Length >= segment.FileOffset + segment.Length)) Mark(index);
                    }

                    return;
                }
            }

            if (!Directory.Exists(_root)) return;
            for (var index = 0; index < meta.PieceCount; index++)
            {
                if (await ReadPieceAsync(index, ct) is { } data && SHA1.HashData(data).AsSpan().SequenceEqual(meta.PieceHash(index))) Mark(index);
            }
        }

        public async Task SaveStateAsync(string statePath, CancellationToken ct)
        {
            var state = new byte[meta.PieceCount];
            for (var index = 0; index < state.Length; index++) state[index] = Has(index) ? (byte)1 : (byte)0;
            var temporary = statePath + ".tmp";
            await File.WriteAllBytesAsync(temporary, state, ct);
            File.Move(temporary, statePath, overwrite: true);
        }

        public async Task<bool> WritePieceAsync(int index, byte[] data, CancellationToken ct)
        {
            if (data.Length != meta.PieceSize(index) || !SHA1.HashData(data).AsSpan().SequenceEqual(meta.PieceHash(index))) return false;
            await _gate.WaitAsync(ct);
            try
            {
                if (Has(index)) return true;
                foreach (var segment in Segments(index))
                {
                    if (segment.Path is null) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(segment.Path)!);
                    await using var file = new FileStream(segment.Path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1, FileOptions.Asynchronous);
                    file.Position = segment.FileOffset;
                    await file.WriteAsync(data.AsMemory(segment.DataOffset, segment.Length), ct);
                }

                Mark(index);
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }

        private void Mark(int index)
        {
            if (!_have[index])
            {
                Volatile.Write(ref _have[index], true);
                Interlocked.Increment(ref _haveCount);
            }
        }

        private async Task<byte[]?> ReadPieceAsync(int index, CancellationToken ct)
        {
            var data = new byte[meta.PieceSize(index)];
            foreach (var segment in Segments(index))
            {
                if (segment.Path is null) continue;
                if (!File.Exists(segment.Path) || new FileInfo(segment.Path).Length < segment.FileOffset + segment.Length) return null;
                await using var file = new FileStream(segment.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.Asynchronous);
                file.Position = segment.FileOffset;
                await file.ReadExactlyAsync(data.AsMemory(segment.DataOffset, segment.Length), ct);
            }

            return data;
        }

        // The parts of the files a piece covers; padding files have no path and stay zeros.
        private IEnumerable<(string? Path, long FileOffset, int DataOffset, int Length)> Segments(int index)
        {
            var start = (long)index * meta.PieceLength;
            var end = start + meta.PieceSize(index);
            foreach (var file in meta.Files)
            {
                var fileEnd = file.Offset + file.Length;
                if (fileEnd <= start || file.Offset >= end || file.Length == 0) continue;
                var from = Math.Max(start, file.Offset);
                var to = Math.Min(end, fileEnd);
                string? path = null;
                if (file.Path.Length > 0)
                {
                    path = Path.GetFullPath(Path.Combine([_root, .. file.Path]));
                    if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Unsafe path in torrent.");
                }

                yield return (path, from - file.Offset, (int)(from - start), (int)(to - from));
            }
        }
    }
}
