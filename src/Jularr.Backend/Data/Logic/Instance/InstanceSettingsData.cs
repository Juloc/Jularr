using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Logic.Instance
{
    internal sealed class InstanceSettingsUpdateData : BaseData
    {
        public InstanceSettingsUpdateData(string name, long uiLocaleId, long version, int? readingCacheMegabytes, int? readingCacheWorks, bool? usersConfirmSources, int? libraryScanIntervalHours,
            int? historyRetentionDays, int? historyMegabytes, int? collectionLimit, int? unitGroupLimit)
        {
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

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.HistoryRetentionDays), false)]
        public int? HistoryRetentionDays { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.HistoryMegabytes), false)]
        public int? HistoryMegabytes { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.CollectionLimit), false)]
        public int? CollectionLimit { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.UnitGroupLimit), false)]
        public int? UnitGroupLimit { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Name))]
        public string Name { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.UiLocaleId))]
        public long UiLocaleId { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Version))]
        public long Version { get; }
    }
}
