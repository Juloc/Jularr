using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Library;

namespace Jularr.Backend.Data.Logic.Library
{
    internal sealed class NamingPatternData : BaseData
    {
        public NamingPatternData(MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string pattern, string? dailyFile = null)
        {
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            Pattern = pattern;
            DailyFile = dailyFile;
        }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.Pattern))]
        public string Pattern { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.DailyFile))]
        public string? DailyFile { get; }
    }

    internal sealed class NamingPatternSaveData : BaseData
    {
        public NamingPatternSaveData(MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string pattern, long version, string? dailyFile = null)
        {
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            Pattern = pattern;
            DailyFile = dailyFile;
            Version = version;
        }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.Pattern))]
        public string Pattern { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.DailyFile))]
        public string? DailyFile { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.Version))]
        public long Version { get; }
    }

    // How names are made of values for one media type.
    internal sealed class NamingSettingsData : BaseData
    {
        public NamingSettingsData(MediaType mediaTypeId, bool replacesIllegalCharacters, ColonReplacement colonReplacementId, string? customColonReplacement, string? specialsFolder, long version, bool renamesFiles = true, MultiEpisodeStyle multiEpisodeStyleId = MultiEpisodeStyle.PrefixedRange)
        {
            MediaTypeId = mediaTypeId;
            ReplacesIllegalCharacters = replacesIllegalCharacters;
            ColonReplacementId = colonReplacementId;
            CustomColonReplacement = customColonReplacement;
            SpecialsFolder = specialsFolder;
            RenamesFiles = renamesFiles;
            MultiEpisodeStyleId = multiEpisodeStyleId;
            Version = version;
        }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.ReplacesIllegalCharacters))]
        public bool ReplacesIllegalCharacters { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.ColonReplacementId))]
        public ColonReplacement ColonReplacementId { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.CustomColonReplacement))]
        public string? CustomColonReplacement { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.SpecialsFolder))]
        public string? SpecialsFolder { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.RenamesFiles))]
        public bool RenamesFiles { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.MultiEpisodeStyleId))]
        public MultiEpisodeStyle MultiEpisodeStyleId { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.Version))]
        public long Version { get; }
    }
}
