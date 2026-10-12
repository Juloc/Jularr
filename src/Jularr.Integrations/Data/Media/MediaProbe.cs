namespace Jularr.Integrations.Data.Media
{
    internal sealed class MediaProbe
    {
        public MediaProbe(string? container, long? durationMs, long? bitRate, IReadOnlyList<MediaProbeStream> streams)
        {
            Container = container;
            DurationMs = durationMs;
            BitRate = bitRate;
            Streams = streams;
        }

        public string? Container { get; }
        public long? DurationMs { get; }
        public long? BitRate { get; }
        public IReadOnlyList<MediaProbeStream> Streams { get; }
    }

    internal sealed class MediaProbeStream
    {
        public MediaProbeStream(int index, string codecType, string? codec, string? language, string? title, int? width, int? height, int? channels, bool isDefault, bool isForced)
        {
            Index = index;
            CodecType = codecType;
            Codec = codec;
            Language = language;
            Title = title;
            Width = width;
            Height = height;
            Channels = channels;
            IsDefault = isDefault;
            IsForced = isForced;
        }

        public int Index { get; }
        public string CodecType { get; }
        public string? Codec { get; }
        public string? Language { get; }
        public string? Title { get; }
        public int? Width { get; }
        public int? Height { get; }
        public int? Channels { get; }
        public bool IsDefault { get; }
        public bool IsForced { get; }
    }
}
