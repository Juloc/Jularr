using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Logic.Storage
{
    internal sealed class StorageMountProbeData : BaseData
    {
        public StorageMountProbeData(long storageMountId, StorageAvailabilityType storageAvailabilityTypeId, string? diagnosticCode, long? freeBytes, long? totalBytes)
        {
            StorageMountId = storageMountId;
            StorageAvailabilityTypeId = storageAvailabilityTypeId;
            DiagnosticCode = diagnosticCode;
            FreeBytes = freeBytes;
            TotalBytes = totalBytes;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(StorageMountStateFields), nameof(StorageMountStateFields.StorageAvailabilityTypeId))]
        public StorageAvailabilityType StorageAvailabilityTypeId { get; }

        [DataField(typeof(StorageMountStateFields), nameof(StorageMountStateFields.DiagnosticCode))]
        public string? DiagnosticCode { get; }

        [DataField(typeof(StorageMountStateFields), nameof(StorageMountStateFields.FreeBytes))]
        public long? FreeBytes { get; }

        [DataField(typeof(StorageMountStateFields), nameof(StorageMountStateFields.TotalBytes))]
        public long? TotalBytes { get; }
    }

    internal sealed class StorageConnectRequestData : BaseData
    {
        public StorageConnectRequestData(long storageMountId, bool isConnectRequested)
        {
            StorageMountId = storageMountId;
            IsConnectRequested = isConnectRequested;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(StorageMountStateFields), nameof(StorageMountStateFields.IsConnectRequested))]
        public bool IsConnectRequested { get; }
    }

    internal sealed class StorageReconnectCandidateData : BaseData
    {
        public StorageReconnectCandidateData(long storageMountId, int reconnectAttempts)
        {
            StorageMountId = storageMountId;
            ReconnectAttempts = reconnectAttempts;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(StorageMountStateFields), nameof(StorageMountStateFields.ReconnectAttempts))]
        public int ReconnectAttempts { get; }
    }

    internal sealed class StorageReconnectAttemptData : BaseData
    {
        public StorageReconnectAttemptData(int reconnectAttempts, int delayMinutes)
        {
            ReconnectAttempts = reconnectAttempts;
            DelayMinutes = delayMinutes;
        }

        [DataField(typeof(StorageMountStateFields), nameof(StorageMountStateFields.ReconnectAttempts))]
        public int ReconnectAttempts { get; }

        [DataField(typeof(StorageMountStateFields), nameof(StorageMountStateFields.DelayMinutes))]
        public int DelayMinutes { get; }
    }

    internal sealed class StorageSynologyData : BaseData
    {
        public StorageSynologyData(long storageMountId, string? synologyUrl, string? synologyUsername, byte[]? synologyProtectedPassword, string? synologyCertificateSha256)
        {
            SynologyCertificateSha256 = synologyCertificateSha256;
            StorageMountId = storageMountId;
            SynologyUrl = synologyUrl;
            SynologyUsername = synologyUsername;
            SynologyProtectedPassword = synologyProtectedPassword;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(SynologyFields), nameof(SynologyFields.SynologyUrl))]
        public string? SynologyUrl { get; }

        [DataField(typeof(SynologyFields), nameof(SynologyFields.SynologyUsername))]
        public string? SynologyUsername { get; }

        [JsonIgnore]
        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.ProtectedPassword))]
        public byte[]? SynologyProtectedPassword { get; }

        [DataField(typeof(SynologyFields), nameof(SynologyFields.SynologyCertificateSha256))]
        public string? SynologyCertificateSha256 { get; }
    }

    internal sealed class StorageSynologyCertificateData : BaseData
    {
        public StorageSynologyCertificateData(long storageMountId, string synologyUrl, string synologyCertificateSha256)
        {
            StorageMountId = storageMountId;
            SynologyUrl = synologyUrl;
            SynologyCertificateSha256 = synologyCertificateSha256;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        // The address the certificate was read from.
        [DataField(typeof(SynologyFields), nameof(SynologyFields.SynologyUrl))]
        public string SynologyUrl { get; }

        [DataField(typeof(SynologyFields), nameof(SynologyFields.SynologyCertificateSha256))]
        public string SynologyCertificateSha256 { get; }
    }
}
