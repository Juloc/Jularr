using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Admin.Instance.V1
{
    public sealed class ReadInstanceSettingsParametersV1 : BaseParameters
    {
    }

    public sealed class UpdateInstanceSettingsParametersV1 : BaseParameters
    {
    }

    public sealed class InstanceSettingsDetailsV1 : BaseResponse
    {
        public InstanceSettingsDetailsV1(string name, long uiLocaleId, long version, int readingCacheMegabytes, int readingCacheWorks, bool usersConfirmSources, int libraryScanIntervalHours,
            int historyRetentionDays, int historyMegabytes, int collectionLimit, int unitGroupLimit, string[] subtitleLanguages, string? publicUrl, string[] trustedProxies,
            int backupIntervalHours, int backupKeep, int providerSyncIntervalMinutes, int wantedSearchIntervalMinutes)
        {
            WantedSearchIntervalMinutes = wantedSearchIntervalMinutes;
            ProviderSyncIntervalMinutes = providerSyncIntervalMinutes;
            BackupKeep = backupKeep;
            BackupIntervalHours = backupIntervalHours;
            TrustedProxies = trustedProxies;
            PublicUrl = publicUrl;
            SubtitleLanguages = subtitleLanguages;
            CollectionLimit = collectionLimit;
            UnitGroupLimit = unitGroupLimit;
            HistoryRetentionDays = historyRetentionDays;
            HistoryMegabytes = historyMegabytes;
            LibraryScanIntervalHours = libraryScanIntervalHours;
            UsersConfirmSources = usersConfirmSources;
            Name = name;
            UiLocaleId = uiLocaleId;
            Version = version;
            ReadingCacheMegabytes = readingCacheMegabytes;
            ReadingCacheWorks = readingCacheWorks;
        }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Name))]
        public string Name { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.UiLocaleId))]
        public long UiLocaleId { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Version))]
        public long Version { get; }

        // Reading cache budget in the database.
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.ReadingCacheMegabytes))]
        public int ReadingCacheMegabytes { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.ReadingCacheWorks))]
        public int ReadingCacheWorks { get; }

        // Users with the account switch may confirm source checks for their own requests.
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.UsersConfirmSources))]
        public bool UsersConfirmSources { get; }

        // Libraries rescan after this many hours; 0 turns it off.
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.LibraryScanIntervalHours))]
        public int LibraryScanIntervalHours { get; }

        // Automatic backups every this many hours; 0 turns them off.
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.BackupIntervalHours))]
        public int BackupIntervalHours { get; }

        // The newest backups that are kept.
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.BackupKeep))]
        public int BackupKeep { get; }

        // Minutes between two reads of the connected Plex, Jellyfin and Emby servers.
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.ProviderSyncIntervalMinutes))]
        public int ProviderSyncIntervalMinutes { get; }

        // Minutes between two searches of the wanted titles that are due.
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.WantedSearchIntervalMinutes))]
        public int WantedSearchIntervalMinutes { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.HistoryRetentionDays))]
        public int HistoryRetentionDays { get; }

        // Decimal megabytes.
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.HistoryMegabytes))]
        public int HistoryMegabytes { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.CollectionLimit))]
        public int CollectionLimit { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.UnitGroupLimit))]
        public int UnitGroupLimit { get; }

        // Two-letter codes in the order subtitles are wanted.
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.SubtitleLanguages))]
        public string[] SubtitleLanguages { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.PublicUrl))]
        public string? PublicUrl { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.TrustedProxies))]
        public string[] TrustedProxies { get; }
    }

    public sealed class UpdateInstanceSettingsDataV1 : BaseData
    {
        public UpdateInstanceSettingsDataV1(string name, long uiLocaleId, long version, int? readingCacheMegabytes = null, int? readingCacheWorks = null, bool? usersConfirmSources = null,
            int? libraryScanIntervalHours = null, int? historyRetentionDays = null, int? historyMegabytes = null, int? collectionLimit = null, int? unitGroupLimit = null,
            string[]? subtitleLanguages = null, string? publicUrl = null, string[]? trustedProxies = null,
            int? backupIntervalHours = null, int? backupKeep = null, int? providerSyncIntervalMinutes = null, int? wantedSearchIntervalMinutes = null)
        {
            WantedSearchIntervalMinutes = wantedSearchIntervalMinutes;
            ProviderSyncIntervalMinutes = providerSyncIntervalMinutes;
            BackupKeep = backupKeep;
            BackupIntervalHours = backupIntervalHours;
            TrustedProxies = trustedProxies;
            PublicUrl = publicUrl;
            SubtitleLanguages = subtitleLanguages;
            CollectionLimit = collectionLimit;
            UnitGroupLimit = unitGroupLimit;
            HistoryRetentionDays = historyRetentionDays;
            HistoryMegabytes = historyMegabytes;
            LibraryScanIntervalHours = libraryScanIntervalHours;
            UsersConfirmSources = usersConfirmSources;
            Name = name;
            UiLocaleId = uiLocaleId;
            Version = version;
            ReadingCacheMegabytes = readingCacheMegabytes;
            ReadingCacheWorks = readingCacheWorks;
        }

        // Empty keeps the current budget.
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.ReadingCacheMegabytes), false)]
        public int? ReadingCacheMegabytes { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.ReadingCacheWorks), false)]
        public int? ReadingCacheWorks { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.UsersConfirmSources), false)]
        public bool? UsersConfirmSources { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.LibraryScanIntervalHours), false)]
        public int? LibraryScanIntervalHours { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.BackupIntervalHours), false)]
        public int? BackupIntervalHours { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.BackupKeep), false)]
        public int? BackupKeep { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.ProviderSyncIntervalMinutes), false)]
        public int? ProviderSyncIntervalMinutes { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.WantedSearchIntervalMinutes), false)]
        public int? WantedSearchIntervalMinutes { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.HistoryRetentionDays), false)]
        public int? HistoryRetentionDays { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.HistoryMegabytes), false)]
        public int? HistoryMegabytes { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.CollectionLimit), false)]
        public int? CollectionLimit { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.UnitGroupLimit), false)]
        public int? UnitGroupLimit { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.SubtitleLanguages), false)]
        public string[]? SubtitleLanguages { get; }

        // Empty: QR codes use the address a device opened.
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.PublicUrl))]
        public string? PublicUrl { get; }

        // Addresses or networks; missing keeps the current ones, empty trusts none.
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.TrustedProxies), false)]
        public string[]? TrustedProxies { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Name))]
        public string Name { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.UiLocaleId))]
        public long UiLocaleId { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Version))]
        public long Version { get; }
    }
}
