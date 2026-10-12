namespace Jularr.Backend.Logic.Acquisition
{
    // Media-type-agnostic release parsing model. Scene/usenet naming is shared across media types
    // (anime, movies, TV, ...), so the parsed shape lives here and each media type registers a parser
    // (see IReleaseParser / MediaAcquisitionRegistry). Anime is one registration on top of this.

    internal enum ReleaseSource
    {
        Unknown,
        WebDl,
        WebRip,
        BluRay,
        BluRayRip,
        Hdtv
    }

    internal enum ReleaseVideoCodec
    {
        Unknown,
        Avc,
        Hevc,
        Av1
    }

    internal enum ReleaseHdrFormat
    {
        None,
        Hdr,
        Hdr10,
        Hdr10Plus,
        DolbyVision
    }

    internal enum ReleaseAudioCodec
    {
        Unknown,
        Aac,
        Flac,
        Opus,
        Ac3,
        Eac3,
        Dts,
        DtsHd,
        TrueHd
    }

    internal sealed record ReleaseEvidence(string Field, string Value, string MatchedText);

    internal sealed record ReleaseInfo(
        string RawTitle,
        string SeriesTitle,
        int? SeasonNumber,
        int? EpisodeStart,
        int? EpisodeEnd,
        int? AbsoluteEpisodeStart,
        int? AbsoluteEpisodeEnd,
        DateOnly? AirDate,
        bool IsSeasonPack,
        bool IsMultiEpisode,
        int? Resolution,
        ReleaseSource Source,
        ReleaseVideoCodec VideoCodec,
        int? BitDepth,
        ReleaseHdrFormat HdrFormat,
        ReleaseAudioCodec AudioCodec,
        string? AudioChannels,
        IReadOnlyList<string> AudioLanguages,
        IReadOnlyList<string> SubtitleLanguages,
        bool IsDualAudio,
        bool IsMultiAudio,
        string? ReleaseGroup,
        int Version,
        bool IsProper,
        bool IsRepack,
        string? ImdbId,
        string ReleaseKey,
        double Confidence,
        IReadOnlyList<ReleaseEvidence> Evidence)
    {
        /// <summary>
        /// Optional non-video container/document quality supplied by a media-specific parser.
        /// The shared scene parser leaves this null; Books use values such as EPUB/PDF so the generic
        /// quality-profile engine does not collapse reading releases into UNKNOWN-UNKNOWN.
        /// </summary>
        public string? DocumentFormat { get; init; }
    }
}
