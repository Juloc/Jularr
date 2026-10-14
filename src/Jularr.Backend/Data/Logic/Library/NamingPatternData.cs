using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Library;

namespace Jularr.Backend.Data.Logic.Library
{
    internal sealed class NamingPatternData : BaseData
    {
        public NamingPatternData(MediaType mediaTypeId, string pattern)
        {
            MediaTypeId = mediaTypeId;
            Pattern = pattern;
        }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.Pattern))]
        public string Pattern { get; }
    }

    internal sealed class NamingPatternSaveData : BaseData
    {
        public NamingPatternSaveData(MediaType mediaTypeId, string pattern, long version)
        {
            MediaTypeId = mediaTypeId;
            Pattern = pattern;
            Version = version;
        }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.Pattern))]
        public string Pattern { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.Version))]
        public long Version { get; }
    }
}
