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

        // Every language the name states, as two letters; English when it states none.
        public IReadOnlyList<string> Languages { get; init; } = ["en"];

        // A remux, a full disc, raw HD, a regional or screener copy.
        public ReleaseQualityModifier Modifier { get; init; }
        public string? Edition { get; init; }

        // Optional non-video container/document quality supplied by a media-specific parser.
        public string? DocumentFormat { get; init; }

        // Volume range of a Manga release; empty for a complete or unnumbered one.
        public int? VolumeStart { get; init; }
        public int? VolumeEnd { get; init; }

        // What the name of a book release states; null for other media.
        public BookReleaseName? Book { get; init; }
    }

    // Everything a book release name states, read without the request.
    internal sealed class BookReleaseName
    {
        public BookReleaseName(string title, BookReleaseKind kind, string? format, int? volumeStart, int? volumeEnd, IReadOnlyList<string> editions, string? publisher, string? group, int? year,
            IReadOnlyList<string> languages)
        {
            Title = title;
            Kind = kind;
            Format = format;
            VolumeStart = volumeStart;
            VolumeEnd = volumeEnd;
            Editions = editions;
            Publisher = publisher;
            Group = group;
            Year = year;
            Languages = languages;
        }

        // The name without tags, volumes, formats and edition words.
        public string Title { get; }
        public BookReleaseKind Kind { get; }

        // The named file format (EPUB, PDF, CBZ, M4B ...); null when the name does not say.
        public string? Format { get; }

        // A single volume has no end; an unnumbered release has neither.
        public int? VolumeStart { get; }
        public int? VolumeEnd { get; }

        // Edition words in a fixed order: Digital, WebNovel, Compilation, Complete, Illustrated.
        public IReadOnlyList<string> Editions { get; }
        public string? Publisher { get; }
        public string? Group { get; }
        public int? Year { get; }
        public IReadOnlyList<string> Languages { get; }

        public bool IsComplete => Editions.Contains("Complete");

        // Volumes the name counts; null when it names none.
        public int? VolumeCount => VolumeStart is { } start ? Math.Max(start, VolumeEnd ?? start) - start + 1 : null;
    }

    // Points of one score rule, with what it saw.
    internal sealed record ScorePoint(string Code, string? Value, int Points);

    // What a book search asks besides the title: a series (light novels) or one book, the size limits per volume of the kinds, and whether a person decides (manual search).
    internal sealed record BookSearch(bool IsSeries, IReadOnlyDictionary<string, QualitySize> Sizes, bool Automatic);
}
