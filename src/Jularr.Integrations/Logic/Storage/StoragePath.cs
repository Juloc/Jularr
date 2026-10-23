using Jularr.Integrations.Data.Nas;

namespace Jularr.Integrations.Logic.Storage
{
    // Paths inside a connection: relative, separated by '/', "" for the connection itself; nothing reaches above it.
    internal static class StoragePath
    {
        public static string Check(string path)
        {
            var trimmed = path.Trim('/');
            if (trimmed.Length == 0)
            {
                return trimmed;
            }

            foreach (var part in trimmed.Split('/'))
            {
                if (part.Length == 0 || part is "." or ".." || part.Contains('\\') || part.Contains('\0'))
                {
                    throw new NasMountException("storage_unavailable");
                }
            }

            return trimmed;
        }

        public static string Combine(string first, string second)
        {
            first = Check(first);
            second = Check(second);
            return first.Length == 0 ? second : second.Length == 0 ? first : first + "/" + second;
        }

        public static string Parent(string path)
        {
            var checkedPath = Check(path);
            var index = checkedPath.LastIndexOf('/');
            return index < 0 ? "" : checkedPath[..index];
        }

        public static string Name(string path)
        {
            var checkedPath = Check(path);
            return checkedPath[(checkedPath.LastIndexOf('/') + 1)..];
        }
    }
}
