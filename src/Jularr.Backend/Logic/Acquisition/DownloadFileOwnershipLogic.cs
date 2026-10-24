using Jularr.Integrations.Data.Sabnzbd;

namespace Jularr.Backend.Logic.Acquisition
{
    // Missing a name from a paged history does not make its directory safe to delete.
    internal static class DownloadFileOwnershipLogic
    {
        internal static string Assignment(string area, string relative, SabnzbdConsoleSnapshot snapshot)
        {
            if (snapshot.Queue.Count >= 200 || snapshot.History.Count >= 200)
            {
                return "unchecked";
            }
            var top = relative.Replace('\\', '/').Split('/')[0];
            var queue = snapshot.Queue.Any(job => string.Equals(job.Name, top, StringComparison.OrdinalIgnoreCase));
            var history = snapshot.History.Any(job => string.Equals(Path.GetFileName(job.StoragePath?.TrimEnd('/', '\\')), top, StringComparison.OrdinalIgnoreCase));
            if (queue)
            {
                return "queue";
            }
            if (history)
            {
                return "history";
            }
            return area == "incomplete" && relative == top && snapshot.OrphanFolders.Contains(top, StringComparer.OrdinalIgnoreCase) ? "orphan" : "unassigned";
        }

    }
}
