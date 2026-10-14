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
        public InstanceSettingsDetailsV1(string name, long uiLocaleId, long version, int readingCacheMegabytes, int readingCacheWorks, bool usersConfirmSources, int libraryScanIntervalHours)
        {
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
    }

    public sealed class UpdateInstanceSettingsDataV1 : BaseData
    {
        public UpdateInstanceSettingsDataV1(string name, long uiLocaleId, long version, int? readingCacheMegabytes = null, int? readingCacheWorks = null, bool? usersConfirmSources = null,
            int? libraryScanIntervalHours = null)
        {
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

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Name))]
        public string Name { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.UiLocaleId))]
        public long UiLocaleId { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Version))]
        public long Version { get; }
    }
}
