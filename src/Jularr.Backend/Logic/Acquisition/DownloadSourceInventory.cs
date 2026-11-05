using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Common;
using Jularr.Integrations.Data.Storage;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Logic.Acquisition
{
    // Durable proof of the source contents at import time, including non-media sidecars.
    // A different inventory must never authorize an automatic source-folder deletion.
    internal static class DownloadSourceInventory
    {
        // A folder a move emptied: it may go, and a file that appears in it later keeps it.
        public static readonly string Empty = Convert.ToHexString(SHA256.HashData([]));

        public static string Fingerprint(IEnumerable<StorageFile> files)
        {
            var entries = files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
            if (entries.Length == 0)
                return Empty;
            if (entries.Any(file => string.IsNullOrWhiteSpace(file.RelativePath)
                || file.RelativePath.Any(char.IsControl)))
                throw new DataValidationException(nameof(files), "invalid_transition");

            var manifest = new StringBuilder();
            foreach (var file in entries)
            {
                manifest.Append(file.RelativePath.Length.ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(file.RelativePath).Append('|')
                    .Append(file.SizeBytes.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(file.ModifiedAt.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString())));
        }

        public static string Scan(string directory)
        {
            var state = new StorageScanState();
            var files = StorageFileSystem.EnumerateFiles(directory, null, state, CancellationToken.None, includeHidden: true).ToArray();
            if (!state.IsComplete)
                throw new DataValidationException(nameof(directory), "invalid_transition");
            return Fingerprint(files);
        }
    }
}
