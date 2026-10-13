using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Logic.Instance
{
    internal sealed class InstanceSettingsUpdateData : BaseData
    {
        public InstanceSettingsUpdateData(string name, long uiLocaleId, long version, int? readingCacheMegabytes, int? readingCacheWorks)
        {
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

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Name))]
        public string Name { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.UiLocaleId))]
        public long UiLocaleId { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Version))]
        public long Version { get; }
    }
}
