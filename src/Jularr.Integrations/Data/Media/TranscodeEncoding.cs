namespace Jularr.Integrations.Data.Media
{
    // The ways FFmpeg converts video on this server.
    internal enum TranscodePipeline
    {
        Software,
        Vaapi,
        Qsv,
        Nvenc,
        VideoToolbox
    }

    // The FFmpeg arguments of one way to convert video; the scale filter names {height} where the cap goes, none leaves the picture's size to the encoder.
    internal sealed record TranscodeEncoding(IReadOnlyList<string> HardwareArguments, IReadOnlyList<string> VideoArguments, string? ScaleFilter);

    // What FFmpeg and the machine offer for one pipeline.
    internal sealed class TranscodeHardwareData
    {
        public TranscodeHardwareData(TranscodePipeline pipeline, string encoder, string? device, bool isAvailable)
        {
            Pipeline = pipeline;
            Encoder = encoder;
            Device = device;
            IsAvailable = isAvailable;
        }

        public TranscodePipeline Pipeline { get; }
        public string Encoder { get; }
        public string? Device { get; }
        public bool IsAvailable { get; }
    }

    // How fast one pipeline converted a clip of this picture height, in seconds of picture per second; none when it did not run.
    internal sealed class TranscodeSpeedData
    {
        public TranscodeSpeedData(TranscodePipeline pipeline, int height, decimal? speedFactor)
        {
            Pipeline = pipeline;
            Height = height;
            SpeedFactor = speedFactor;
        }

        public TranscodePipeline Pipeline { get; }
        public int Height { get; }
        public decimal? SpeedFactor { get; }
    }
}
