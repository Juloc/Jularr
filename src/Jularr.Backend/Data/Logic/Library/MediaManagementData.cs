using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Library;

namespace Jularr.Backend.Data.Logic.Library
{
    internal sealed class MediaManagementSettingsData : BaseData
    {
        public MediaManagementSettingsData(MediaType mediaTypeId, bool usesHardlinks, bool skipsFreeSpaceCheck, long minimumFreeSpaceMegabytes, bool analysesMedia, long version)
        {
            MediaTypeId = mediaTypeId;
            UsesHardlinks = usesHardlinks;
            SkipsFreeSpaceCheck = skipsFreeSpaceCheck;
            MinimumFreeSpaceMegabytes = minimumFreeSpaceMegabytes;
            AnalysesMedia = analysesMedia;
            Version = version;
        }

        [DataField(typeof(MediaManagementFields), nameof(MediaManagementFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(MediaManagementFields), nameof(MediaManagementFields.UsesHardlinks))]
        public bool UsesHardlinks { get; }

        [DataField(typeof(MediaManagementFields), nameof(MediaManagementFields.SkipsFreeSpaceCheck))]
        public bool SkipsFreeSpaceCheck { get; }

        [DataField(typeof(MediaManagementFields), nameof(MediaManagementFields.MinimumFreeSpaceMegabytes))]
        public long MinimumFreeSpaceMegabytes { get; }

        [DataField(typeof(MediaManagementFields), nameof(MediaManagementFields.AnalysesMedia))]
        public bool AnalysesMedia { get; }

        [DataField(typeof(MediaManagementFields), nameof(MediaManagementFields.Version))]
        public long Version { get; }
    }

    internal sealed class MediaManagementParameters : BaseData
    {
        public MediaManagementParameters(MediaType mediaTypeId)
        {
            MediaTypeId = mediaTypeId;
        }

        [DataField(typeof(MediaManagementFields), nameof(MediaManagementFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }
    }
}
