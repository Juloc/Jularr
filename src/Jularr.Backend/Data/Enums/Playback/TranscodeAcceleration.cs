namespace Jularr.Backend.Data.Enums.Playback
{
    // How video is converted; Auto follows the last benchmark.
    public enum TranscodeAcceleration : byte
    {
        Auto = 1,
        Software = 2,
        Vaapi = 3,
        Qsv = 4,
        Nvenc = 5,
        VideoToolbox = 6
    }
}
