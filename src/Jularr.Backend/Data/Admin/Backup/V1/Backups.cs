using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.Admin.Backup.V1
{
    public sealed class BackupsParametersV1 : BaseParameters
    {
    }

    public sealed class BackupParametersV1 : BaseParameters
    {
        public BackupParametersV1(string name)
        {
            Name = name;
        }

        [DataField(typeof(BackupFields), nameof(BackupFields.Name))]
        public string Name { get; }
    }

    public sealed class BackupV1 : BaseResponse
    {
        public BackupV1(string name, DateTime createdAt, long sizeBytes, bool isCompatible, bool isSealed)
        {
            IsSealed = isSealed;
            Name = name;
            CreatedAt = createdAt;
            SizeBytes = sizeBytes;
            IsCompatible = isCompatible;
        }

        [DataField(typeof(BackupFields), nameof(BackupFields.Name))]
        public string Name { get; }

        [DataField(typeof(BackupFields), nameof(BackupFields.CreatedAt))]
        public DateTime CreatedAt { get; }

        [DataField(typeof(BackupFields), nameof(BackupFields.SizeBytes))]
        public long SizeBytes { get; }

        // Made for this version of Jularr's schema.
        [DataField(typeof(BackupFields), nameof(BackupFields.IsCompatible))]
        public bool IsCompatible { get; }

        // The key ring needs the passphrase to come back.
        [DataField(typeof(BackupFields), nameof(BackupFields.IsSealed))]
        public bool IsSealed { get; }
    }

    public sealed class RestoreBackupDataV1 : BaseData
    {
        public RestoreBackupDataV1(string? passphrase)
        {
            Passphrase = passphrase;
        }

        [DataField(typeof(BackupFields), nameof(BackupFields.Passphrase), false)]
        public string? Passphrase { get; }
    }

    public sealed class BackupsV1 : BaseResponse
    {
        public BackupsV1(BaseDataList<BackupV1> items, bool isConfigured, bool isRunning, string? errorKey, bool canRestore)
        {
            Items = items;
            IsConfigured = isConfigured;
            IsRunning = isRunning;
            ErrorKey = errorKey;
            CanRestore = canRestore;
        }

        public BaseDataList<BackupV1> Items { get; }

        [DataField(typeof(BackupFields), nameof(BackupFields.IsConfigured))]
        public bool IsConfigured { get; }

        [DataField(typeof(BackupFields), nameof(BackupFields.IsRunning))]
        public bool IsRunning { get; }

        // The last backup failed with this reason.
        [DataField(typeof(OperationFields), nameof(OperationFields.ErrorKey))]
        public string? ErrorKey { get; }

        // Only an instance that has not finished setup is replaced by a backup.
        [DataField(typeof(BackupFields), nameof(BackupFields.CanRestore))]
        public bool CanRestore { get; }
    }
}
