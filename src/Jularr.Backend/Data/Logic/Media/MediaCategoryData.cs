using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class MediaCategoryUpdateData : BaseData
    {
        public MediaCategoryUpdateData(string key, bool isEnabled, long version)
        {
            Key = key;
            IsEnabled = isEnabled;
            Version = version;
        }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.Key))]
        public string Key { get; }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.Version))]
        public long Version { get; }
    }

    // A null list keeps that kind of rule; a list replaces it.
    internal sealed class ProfileMediaRulesData : BaseData
    {
        public ProfileMediaRulesData(long profileId, string[]? blockedKeys, string[]? hiddenKeys)
        {
            ProfileId = profileId;
            BlockedKeys = blockedKeys;
            HiddenKeys = hiddenKeys;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.ChangedKeys))]
        public string[]? BlockedKeys { get; }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.ChangedKeys))]
        public string[]? HiddenKeys { get; }
    }
}
