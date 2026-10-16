namespace Jularr.Backend.Service.Core
{
    public sealed class MediaOptions
    {
        // Native tools; unset tools disable their features.
        public string? FfprobePath { get; set; }
        public string? FfmpegPath { get; set; }

        // Chromaprint's fpcalc fingerprints untagged music for AcoustID.
        public string? FpcalcPath { get; set; }

        // Resource budget for conversions.
        public string TranscodeCachePath { get; set; } = Path.Combine(Path.GetTempPath(), "jularr-transcode");
        public int MaxTranscodes { get; set; } = 2;

        // Seek thumbnails are kept here; without a folder there are none.
        public string? TrickplayPath { get; set; }

        // The Bergamot engine and language models are kept here once a reader needs them; without a folder books are not translated.
        public string? TranslationPath { get; set; }

        // The OCR models are kept here once a scanned book needs them; without a folder scanned books stay PDFs.
        public string? OcrPath { get; set; }

        // Optional hardware encoding; software stays the fallback.
        public string[] HardwareAccelerationArguments { get; set; } = [];
        public string[] VideoEncoderArguments { get; set; } = [];

        internal static IReadOnlyList<string> SoftwareEncoderArguments { get; } = ["-c:v", "libx264", "-preset", "veryfast", "-crf", "22", "-pix_fmt", "yuv420p"];
    }
}
