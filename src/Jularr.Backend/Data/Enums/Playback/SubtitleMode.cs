namespace Jularr.Backend.Data.Enums.Playback
{
    // When a profile wants subtitles: never, forced ones only, always, or while the audio is not in its subtitle language.
    public enum SubtitleMode : byte
    {
        Off = 1,
        ForcedOnly = 2,
        Always = 3,
        WhenForeign = 4
    }
}
