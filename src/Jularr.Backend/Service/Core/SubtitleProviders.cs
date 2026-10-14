using System.Text;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Integrations.Data.Subtitles;
using Jularr.Integrations.Logic.Subtitles;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Core
{
    // One connection, its provider client and its decrypted access for one call.
    internal sealed class SubtitleProviders(OpenSubtitlesClient openSubtitles, SubDlClient subDl, IDataProtectionProvider protection)
    {
        private readonly IDataProtector _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);

        public Task<IReadOnlyList<SubtitleOffer>> SearchAsync(AcquisitionConnectionData connection, SubtitleQuery query, CancellationToken ct) => connection.AcquisitionConnectionTypeId switch
        {
            AcquisitionConnectionType.OpenSubtitles => openSubtitles.SearchAsync(Access(connection), query, ct),
            AcquisitionConnectionType.SubDl => subDl.SearchAsync(Access(connection), query, ct),
            _ => throw new InvalidOperationException("The connection provides no subtitles.")
        };

        public Task<byte[]> DownloadAsync(AcquisitionConnectionData connection, SubtitleOffer offer, SubtitleQuery query, CancellationToken ct) => connection.AcquisitionConnectionTypeId switch
        {
            AcquisitionConnectionType.OpenSubtitles => openSubtitles.DownloadAsync(Access(connection), offer, ct),
            AcquisitionConnectionType.SubDl => subDl.DownloadAsync(offer, query, ct),
            _ => throw new InvalidOperationException("The connection provides no subtitles.")
        };

        public Task TestAsync(AcquisitionConnectionData connection, CancellationToken ct) => connection.AcquisitionConnectionTypeId switch
        {
            AcquisitionConnectionType.OpenSubtitles => openSubtitles.TestAsync(Access(connection), ct),
            AcquisitionConnectionType.SubDl => subDl.TestAsync(Access(connection), ct),
            _ => throw new InvalidOperationException("The connection provides no subtitles.")
        };

        private SubtitleAccess Access(AcquisitionConnectionData connection) => new(connection.BaseUrl, AcquisitionConnectionInput.Unprotect(_protector, connection.ProtectedApiKey),
            connection.Username, connection.ProtectedPassword is null ? null : AcquisitionConnectionInput.Unprotect(_protector, connection.ProtectedPassword));
    }
}
