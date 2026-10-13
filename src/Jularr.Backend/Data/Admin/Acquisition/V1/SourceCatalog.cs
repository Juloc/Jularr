using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class SourceCatalogParametersV1 : BaseParameters
    {
    }

    public sealed class SourceCatalogItemV1 : BaseResponse
    {
        public SourceCatalogItemV1(string key, string name, SourceGroup sourceGroupId, string[] capabilities, SourceAvailability sourceAvailabilityId, AcquisitionConnectionType? acquisitionConnectionTypeId,
            string? defaultBaseUrl, bool isConfigured)
        {
            Key = key;
            Name = name;
            SourceGroupId = sourceGroupId;
            Capabilities = capabilities;
            SourceAvailabilityId = sourceAvailabilityId;
            AcquisitionConnectionTypeId = acquisitionConnectionTypeId;
            DefaultBaseUrl = defaultBaseUrl;
            IsConfigured = isConfigured;
        }

        [DataField(typeof(SourceCatalogFields), nameof(SourceCatalogFields.Key))]
        public string Key { get; }

        [DataField(typeof(SourceCatalogFields), nameof(SourceCatalogFields.Name))]
        public string Name { get; }

        [DataField(typeof(SourceCatalogFields), nameof(SourceCatalogFields.SourceGroupId))]
        public SourceGroup SourceGroupId { get; }

        // Metadata, Search, Download, Monitoring or Link.
        [DataField(typeof(SourceCatalogFields), nameof(SourceCatalogFields.Capabilities))]
        public string[] Capabilities { get; }

        [DataField(typeof(SourceCatalogFields), nameof(SourceCatalogFields.SourceAvailabilityId))]
        public SourceAvailability SourceAvailabilityId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.AcquisitionConnectionTypeId), false)]
        public AcquisitionConnectionType? AcquisitionConnectionTypeId { get; }

        [DataField(typeof(SourceCatalogFields), nameof(SourceCatalogFields.DefaultBaseUrl))]
        public string? DefaultBaseUrl { get; }

        [DataField(typeof(SourceCatalogFields), nameof(SourceCatalogFields.IsConfigured))]
        public bool IsConfigured { get; }
    }

    public sealed class SourceSetupResultV1 : BaseResponse
    {
        public SourceSetupResultV1(string[] added, string[] needsAccount)
        {
            Added = added;
            NeedsAccount = needsAccount;
        }

        [DataField(typeof(SourceCatalogFields), nameof(SourceCatalogFields.Keys))]
        public string[] Added { get; }

        // Ready sources that wait for a login or key from the Owner.
        [DataField(typeof(SourceCatalogFields), nameof(SourceCatalogFields.Keys))]
        public string[] NeedsAccount { get; }
    }
}
