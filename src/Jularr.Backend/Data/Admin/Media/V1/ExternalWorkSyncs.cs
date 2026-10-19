using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    public sealed class ReadExternalWorkSyncsParametersV1 : BaseParameters
    {
    }

    public sealed class ExternalWorkSyncParametersV1 : BaseParameters
    {
        public ExternalWorkSyncParametersV1(string @namespace)
        {
            Namespace = @namespace;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }
    }

    // A window a catalog may sync in; one past midnight ends the next day.
    public sealed class ExternalWorkSyncWindowV1 : BaseData
    {
        public ExternalWorkSyncWindowV1(TimeOnly startsAt, TimeOnly endsAt)
        {
            StartsAt = startsAt;
            EndsAt = endsAt;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.StartsAt))]
        public TimeOnly StartsAt { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.EndsAt))]
        public TimeOnly EndsAt { get; }
    }

    // A kept catalog with the Owner's choices and how far its full sync has come.
    public sealed class ExternalWorkSyncV1 : BaseResponse
    {
        public ExternalWorkSyncV1(string @namespace, bool isEnabled, int requestsPerMinute, bool pausesWhileUsed, DateOnly? listedOn, long entries, long pending, long version,
            BaseDataList<ExternalWorkSyncWindowV1> windows)
        {
            Namespace = @namespace;
            IsEnabled = isEnabled;
            RequestsPerMinute = requestsPerMinute;
            PausesWhileUsed = pausesWhileUsed;
            ListedOn = listedOn;
            Entries = entries;
            Pending = pending;
            Version = version;
            Windows = windows;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.RequestsPerMinute))]
        public int RequestsPerMinute { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.PausesWhileUsed))]
        public bool PausesWhileUsed { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.ListedOn))]
        public DateOnly? ListedOn { get; }

        // Titles kept, and how many of them still wait to be read.
        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Entries))]
        public long Entries { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Pending))]
        public long Pending { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Version))]
        public long Version { get; }

        public BaseDataList<ExternalWorkSyncWindowV1> Windows { get; }
    }

    public sealed class UpdateExternalWorkSyncDataV1 : BaseData
    {
        public UpdateExternalWorkSyncDataV1(bool isEnabled, int requestsPerMinute, bool pausesWhileUsed, long version, BaseDataList<ExternalWorkSyncWindowV1>? windows = null)
        {
            IsEnabled = isEnabled;
            RequestsPerMinute = requestsPerMinute;
            PausesWhileUsed = pausesWhileUsed;
            Version = version;
            Windows = windows ?? new([]);
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.RequestsPerMinute))]
        public int RequestsPerMinute { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.PausesWhileUsed))]
        public bool PausesWhileUsed { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Version))]
        public long Version { get; }

        public BaseDataList<ExternalWorkSyncWindowV1> Windows { get; }
    }
}
