using Jularr.Integrations.Logic.Torrent;

namespace Jularr.Backend.Service.Admin.Wanted.V1
{
    // A new try at a torrent takes over the folder of an earlier try of the same torrent that no longer runs, so the pieces that were already verified are kept
    // and the download goes on where it stood instead of starting from zero.
    internal static class TorrentResume
    {
        private static readonly string[] OwnFiles = [TorrentDownloader.MagnetName, TorrentDownloader.TorrentName, "job.waiting"];

        // Moves the data, the fetched metadata and the piece state of an earlier try of the torrent into this job's folder; true when a folder was taken over.
        // `isActive` says whether the operation of a folder still runs; such a folder is never touched.
        public static async Task<bool> TakeOverAsync(string staging, byte[] infoHash, Func<long, Task<bool>> isActive, CancellationToken ct)
        {
            var category = Path.GetDirectoryName(staging);
            if (category is null || !Directory.Exists(category)) return false;
            var own = Path.GetFileName(staging);
            foreach (var earlier in Directory.EnumerateDirectories(category, "j-*"))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(earlier);
                if (name == own || !long.TryParse(name.AsSpan(2), out var operationId) || !await SameTorrentAsync(earlier, infoHash, ct) || await isActive(operationId)) continue;
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(earlier))
                    {
                        var entryName = Path.GetFileName(entry);
                        if (OwnFiles.Contains(entryName, StringComparer.Ordinal)) continue;
                        var target = Path.Combine(staging, entryName);
                        if (Directory.Exists(entry) && !Directory.Exists(target)) Directory.Move(entry, target);
                        else if (File.Exists(entry) && !File.Exists(target)) File.Move(entry, target);
                    }

                    Directory.Delete(earlier, recursive: true);
                    return true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A folder that cannot be moved is left as it is; this try starts from zero.
                    return false;
                }
            }

            return false;
        }

        private static async Task<bool> SameTorrentAsync(string directory, byte[] infoHash, CancellationToken ct)
        {
            try
            {
                var magnet = Path.Combine(directory, TorrentDownloader.MagnetName);
                var torrent = Path.Combine(directory, TorrentDownloader.TorrentName);
                if (File.Exists(torrent)) return TorrentMetainfo.Parse(await File.ReadAllBytesAsync(torrent, ct)).InfoHash.AsSpan().SequenceEqual(infoHash);
                if (File.Exists(magnet)) return MagnetLink.Parse((await File.ReadAllTextAsync(magnet, ct)).Trim()).InfoHash.AsSpan().SequenceEqual(infoHash);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
            }

            return false;
        }
    }
}
