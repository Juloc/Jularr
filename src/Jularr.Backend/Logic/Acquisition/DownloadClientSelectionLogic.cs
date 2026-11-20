using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // Downloaders are chosen by compatible transport, media coverage and configured priority.
    internal static class DownloadClientSelectionLogic
    {
        // A download without a work (no media type) goes to the first client of its kind, whatever media types the client is limited to.
        public static AcquisitionConnectionData? Select(IReadOnlyList<AcquisitionConnectionData> clients, MediaType? mediaType, DateTime now, params AcquisitionConnectionType[] compatibleTypes)
        {
            return clients
                .Where(client => client.IsEnabled && (client.RetryAt is null || client.RetryAt <= now)
                    && compatibleTypes.Contains(client.AcquisitionConnectionTypeId)
                    && (client.MediaTypeIds.Length == 0 || mediaType is null || client.MediaTypeIds.Contains((int)mediaType)))
                .OrderBy(client => client.Priority)
                .ThenBy(client => client.Id)
                .FirstOrDefault();
        }
    }
}
