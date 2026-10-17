using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Data.Enums.Acquisition
{
    // The condition kinds of Sonarr's and Radarr's custom formats.
    public enum FormatConditionKind : byte
    {
        ReleaseTitle,
        Edition,
        Language,
        ReleaseGroup,
        Source,
        Resolution,
        QualityModifier,
        IndexerFlag,
        Size,
        ReleaseType,
        Year
    }

    public static class FormatConditionKinds
    {
        public static bool Supports(MediaType mediaType, FormatConditionKind kind) => kind switch
        {
            FormatConditionKind.Edition => mediaType == MediaType.Movie,
            FormatConditionKind.Source or FormatConditionKind.Resolution or FormatConditionKind.QualityModifier => mediaType is MediaType.Movie or MediaType.Series,
            FormatConditionKind.ReleaseType => mediaType == MediaType.Series,
            _ => Enum.IsDefined(mediaType) && Enum.IsDefined(kind)
        };

        public static IReadOnlyList<FormatConditionKind> For(MediaType mediaType) => Enum.GetValues<FormatConditionKind>().Where(kind => Supports(mediaType, kind)).ToArray();
    }
}
