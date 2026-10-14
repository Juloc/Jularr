using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.Core
{
    // Puts a new file into a library: written aside under its own name, then moved in, never over an existing one.
    internal static class LibraryFileWriter
    {
        // Size and the modification time as stored files keep it; none when the name is taken.
        public static async Task<(long Length, DateTime ModifiedAt)?> WriteAsync(string directory, string relativePath, Stream content, CancellationToken ct)
        {
            var folder = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "";
            Directory.CreateDirectory(Path.Combine(directory, folder));
            var path = Path.Combine(StorageFileSystem.ResolveDirectory(directory, folder), Path.GetFileName(relativePath));
            if (File.Exists(path))
            {
                return null;
            }

            var partial = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write))
                {
                    await content.CopyToAsync(stream, ct);
                }

                File.Move(partial, path, false);
            }
            catch (IOException) when (File.Exists(path))
            {
                return null;
            }
            finally
            {
                if (File.Exists(partial)) File.Delete(partial);
            }

            var info = new FileInfo(path);
            return (info.Length, Truncate(info.LastWriteTimeUtc));
        }

        // PostgreSQL keeps microseconds.
        public static DateTime Truncate(DateTime value) => DateTime.UnixEpoch.AddTicks((value.Ticks - DateTime.UnixEpoch.Ticks) / 10 * 10);
    }
}
