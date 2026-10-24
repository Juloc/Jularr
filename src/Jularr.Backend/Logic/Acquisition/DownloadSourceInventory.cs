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
        public static string Fingerprint(IEnumerable<StorageFile> files)
        {
            var entries = files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
            if (entries.Length == 0 || entries.Any(file => string.IsNullOrWhiteSpace(file.RelativePath)
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
            if (!state.IsComplete || files.Length == 0)
                throw new DataValidationException(nameof(directory), "invalid_transition");
            return Fingerprint(files);
        }
    }
}
