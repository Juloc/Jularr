using Jularr.Backend.Logic.Library;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.Core
{
    // Puts a new file into a library: written aside under its own name, then moved in, never over an existing one.
    internal static class LibraryFileWriter
    {
        // The relative path must already be a checked library path. Size and the modification time as stored files keep it; none when the name is taken.
        public static async Task<(long Length, DateTime ModifiedAt)?> WriteAsync(StorageFiles files, string directory, string relativePath, Stream content, CancellationToken ct)
        {
            var path = StoragePath.Combine(directory, relativePath);
            if (await files.ExistsAsync(path, ct))
            {
                return null;
            }

            var partial = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await using (var stream = await files.CreateAsync(partial, ct) ?? throw new IOException("The partial name is taken."))
                {
                    await content.CopyToAsync(stream, ct);
                }

                if (!await files.MoveAsync(partial, path, ct))
                {
                    return null;
                }
            }
            finally
            {
                if (await files.ExistsAsync(partial, CancellationToken.None)) await files.DeleteFileAsync(partial, CancellationToken.None);
            }

            var entry = await files.GetAsync(path, ct) ?? throw new IOException("The written file is missing.");
            return (entry.Length, StoredFileLogic.StoredTime(entry.ModifiedAt));
        }
    }
}
