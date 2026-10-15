namespace Jularr.Backend.Data.Enums.Acquisition
{
    public enum ReleaseRuleField : byte
    {
        RawTitle,
        ReleaseGroup,
        Source,
        Resolution,
        VideoCodec,
        BitDepth,
        HdrFormat,
        AudioCodec,
        AudioLanguage,
        SubtitleLanguage,
        DualAudio,
        MultiAudio,
        Proper,
        Repack,

        // The name of the indexer entry the release came from.
        Indexer
    }
}
