using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Logic.Instance
{
    internal sealed class InstanceSettingsUpdateData : BaseData
    {
        public InstanceSettingsUpdateData(string name, long uiLocaleId, long version, int? readingCacheMegabytes, int? readingCacheWorks, bool? usersConfirmSources, int? libraryScanIntervalHours,
            int? historyRetentionDays, int? historyMegabytes, int? collectionLimit, int? unitGroupLimit, string[]? subtitleLanguages, string? publicUrl,
            string[]? trustedProxies,
            int? metadataTrendingHours = null, int? metadataRatingsHours = null, int? metadataFreshDays = null, int? discoveryCacheEntries = null, int? discoveryCacheMegabytes = null)
        {
            MetadataTrendingHours = metadataTrendingHours;
            MetadataRatingsHours = metadataRatingsHours;
            MetadataFreshDays = metadataFreshDays;
            DiscoveryCacheEntries = discoveryCacheEntries;
            DiscoveryCacheMegabytes = discoveryCacheMegabytes;
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

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.MetadataTrendingHours), false)]
        public int? MetadataTrendingHours { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.MetadataRatingsHours), false)]
        public int? MetadataRatingsHours { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.MetadataFreshDays), false)]
        public int? MetadataFreshDays { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.DiscoveryCacheEntries), false)]
        public int? DiscoveryCacheEntries { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.DiscoveryCacheMegabytes), false)]
        public int? DiscoveryCacheMegabytes { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.ReadingCacheMegabytes), false)]
        public int? ReadingCacheMegabytes { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.ReadingCacheWorks), false)]
        public int? ReadingCacheWorks { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.UsersConfirmSources), false)]
        public bool? UsersConfirmSources { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.LibraryScanIntervalHours), false)]
        public int? LibraryScanIntervalHours { get; }

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
