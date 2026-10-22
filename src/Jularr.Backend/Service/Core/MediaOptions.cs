namespace Jularr.Backend.Service.Core
{
    public sealed class MediaOptions
    {
        // Native tools (FfmpegPath, FfprobePath, FpcalcPath, CjxlPath) are found on the PATH; see Executables.

        // Resource budget for conversions; the Owner's Transcoding page replaces the maximum and the encoding, these stay their defaults.
        public string TranscodeCachePath { get; set; } = Path.Combine(Path.GetTempPath(), "jularr-transcode");
        // The temporary cache stays bounded even when FFmpeg is left running by a player.
        public long MaxTranscodeCacheBytes { get; set; } = 2L * 1024 * 1024 * 1024;
        public long TranscodeCacheFreeSpaceReserveBytes { get; set; } = 1024L * 1024 * 1024;
        public int MaxTranscodes { get; set; } = 2;

        // Seek thumbnails; by default under the data folder.
        public string TrickplayPath { get; set; } = "";

        // Posters, backdrops and episode images fetched from the providers, one folder per Work; by default under the data folder.
        public string CoversPath { get; set; } = "";

        // The Bergamot engine and language models, kept once a reader needs them; by default under the data folder.
        public string TranslationPath { get; set; } = "";

        // The OCR models, kept once a scanned book needs them; by default under the data folder.
        public string OcrPath { get; set; } = "";

        // Explicitly activated speech packages and an optional catalog.json override.
        public string SpeechPath { get; set; } = "";

        // Optional hardware encoding; software stays the fallback.
        public string[] HardwareAccelerationArguments { get; set; } = [];
        public string[] VideoEncoderArguments { get; set; } = [];
    }
}
