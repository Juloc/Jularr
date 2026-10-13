using Jularr.Backend.Data.Enums.Acquisition;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class ReleaseEvidence
    {
        public ReleaseEvidence(string field, string value, string matchedText)
        {
            Field = field;
            Value = value;
            MatchedText = matchedText;
        }

        public string Field { get; }
        public string Value { get; }
        public string MatchedText { get; }
    }

    internal sealed class ReleaseInfo
    {
        public ReleaseInfo(string rawTitle, string seriesTitle, int? seasonNumber, int? episodeStart, int? episodeEnd, int? absoluteEpisodeStart, int? absoluteEpisodeEnd, DateOnly? airDate, bool isSeasonPack, bool isMultiEpisode, int? resolution, ReleaseSource source, ReleaseVideoCodec videoCodec, int? bitDepth, ReleaseHdrFormat hdrFormat, ReleaseAudioCodec audioCodec, string? audioChannels, IReadOnlyList<string> audioLanguages, IReadOnlyList<string> subtitleLanguages, bool isDualAudio, bool isMultiAudio, string? releaseGroup, int version, bool isProper, bool isRepack, string? imdbId, string releaseKey, double confidence, IReadOnlyList<ReleaseEvidence> evidence)
        {
            RawTitle = rawTitle;
            SeriesTitle = seriesTitle;
            SeasonNumber = seasonNumber;
            EpisodeStart = episodeStart;
            EpisodeEnd = episodeEnd;
            AbsoluteEpisodeStart = absoluteEpisodeStart;
            AbsoluteEpisodeEnd = absoluteEpisodeEnd;
            AirDate = airDate;
            IsSeasonPack = isSeasonPack;
            IsMultiEpisode = isMultiEpisode;
            Resolution = resolution;
            Source = source;
            VideoCodec = videoCodec;
            BitDepth = bitDepth;
            HdrFormat = hdrFormat;
            AudioCodec = audioCodec;
            AudioChannels = audioChannels;
            AudioLanguages = audioLanguages;
            SubtitleLanguages = subtitleLanguages;
            IsDualAudio = isDualAudio;
            IsMultiAudio = isMultiAudio;
            ReleaseGroup = releaseGroup;
            Version = version;
            IsProper = isProper;
            IsRepack = isRepack;
            ImdbId = imdbId;
            ReleaseKey = releaseKey;
            Confidence = confidence;
            Evidence = evidence;
        }

        public string RawTitle { get; }
        public string SeriesTitle { get; }
        public int? SeasonNumber { get; }
        public int? EpisodeStart { get; }
        public int? EpisodeEnd { get; }
        public int? AbsoluteEpisodeStart { get; }
        public int? AbsoluteEpisodeEnd { get; }
        public DateOnly? AirDate { get; }
        public bool IsSeasonPack { get; }
        public bool IsMultiEpisode { get; }
        public int? Resolution { get; }
        public ReleaseSource Source { get; }
        public ReleaseVideoCodec VideoCodec { get; }
        public int? BitDepth { get; }
        public ReleaseHdrFormat HdrFormat { get; }
        public ReleaseAudioCodec AudioCodec { get; }
        public string? AudioChannels { get; }
        public IReadOnlyList<string> AudioLanguages { get; }
        public IReadOnlyList<string> SubtitleLanguages { get; }
        public bool IsDualAudio { get; }
        public bool IsMultiAudio { get; }
        public string? ReleaseGroup { get; }
        public int Version { get; }
        public bool IsProper { get; }
        public bool IsRepack { get; }
        public string? ImdbId { get; }
        public string ReleaseKey { get; }
        public double Confidence { get; }
        public IReadOnlyList<ReleaseEvidence> Evidence { get; }

        // Optional non-video container/document quality supplied by a media-specific parser.
        public string? DocumentFormat { get; init; }

        // Volume range of a Manga release; empty for a complete or unnumbered one.
        public int? VolumeStart { get; init; }
        public int? VolumeEnd { get; init; }
    }
}
