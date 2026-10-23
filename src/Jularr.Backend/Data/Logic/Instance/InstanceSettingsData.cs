using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Logic.Instance
{
    internal sealed class InstanceSettingsUpdateData : BaseData
    {
        public InstanceSettingsUpdateData(string name, long uiLocaleId, long version, int? readingCacheMegabytes, int? readingCacheWorks, bool? usersConfirmSources, int? libraryScanIntervalHours,
            int? historyRetentionDays, int? historyMegabytes, int? collectionLimit, int? unitGroupLimit, string[]? subtitleLanguages, string? publicUrl,
            string[]? trustedProxies, int? backupIntervalHours = null, int? backupKeep = null, int? providerSyncIntervalMinutes = null, int? wantedSearchIntervalMinutes = null)
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
        public string[]? SubtitleLanguages { get; set; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.PublicUrl))]
        public string? PublicUrl { get; set; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.TrustedProxies), false)]
        public string[]? TrustedProxies { get; set; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Name))]
        public string Name { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.UiLocaleId))]
        public long UiLocaleId { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Version))]
        public long Version { get; }
    }

    internal sealed class InstancePresentationSettingsData : BaseData
    {
        public InstancePresentationSettingsData(string name, long uiLocaleId, string? publicUrl)
        {
            Name = name;
            UiLocaleId = uiLocaleId;
            PublicUrl = publicUrl;
        }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Name))]
        public string Name { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.UiLocaleId))]
        public long UiLocaleId { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.PublicUrl))]
        public string? PublicUrl { get; }
    }

    internal sealed class TrustedProxiesData : BaseData
    {
        public TrustedProxiesData(string[] trustedProxies)
        {
            TrustedProxies = trustedProxies;
        }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.TrustedProxies))]
        public string[] TrustedProxies { get; }
    }
}
