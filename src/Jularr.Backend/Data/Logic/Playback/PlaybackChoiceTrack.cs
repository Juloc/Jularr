using Jularr.Backend.Data.Enums.Library;

namespace Jularr.Backend.Data.Logic.Playback
{
    // An audio or subtitle choice the preferences pick from; only playable ones are offered to the choice.
    internal sealed record PlaybackChoiceTrack(string Key, MediaTrackType Type, string? Language, bool IsDefault, bool IsForced);
}
