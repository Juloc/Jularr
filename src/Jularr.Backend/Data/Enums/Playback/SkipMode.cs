namespace Jularr.Backend.Data.Enums.Playback
{
    // What the player does at an intro or credits: nothing, a Skip button, or skipping by itself.
    public enum SkipMode : byte
    {
        Off = 1,
        Button = 2,
        Automatic = 3
    }
}
