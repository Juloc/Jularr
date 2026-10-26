namespace Jularr.Backend.Data.Enums.Media
{
    /// <summary>
    /// What a title offers the profile: the state of the local library, the state of its
    /// acquisition and whether content or subtitles match the profile's content languages. The Frontend renders
    /// this state; it never derives one of its own.
    /// </summary>
    public enum MediaAvailabilityState : byte
    {
        /// <summary>Everything monitored is here; no content preference or a file language is unknown.</summary>
        Available = 1,

        /// <summary>Some monitored units are here, with matching or unknown content language.</summary>
        Partial = 2,

        /// <summary>Nothing of the title is here and nothing is on the way.</summary>
        Missing = 3,

        /// <summary>A download of the title runs.</summary>
        Downloading = 4,

        /// <summary>The title is wanted and its search runs.</summary>
        Searching = 5,

        /// <summary>The title is requested and waits for its approval or its search.</summary>
        Requested = 6,

        /// <summary>Everything monitored is here in either preferred content language.</summary>
        ProfileAudio = 7,

        /// <summary>Everything monitored is here; at least one unit has only other known content languages.</summary>
        OtherAudio = 8,

        /// <summary>Some monitored units are here; at least one has only other known content languages.</summary>
        PartialOtherAudio = 9,

        /// <summary>Complete media with foreign audio and subtitles in an accepted content language.</summary>
        PreferredSubtitles = 10,

        /// <summary>Some monitored units are here; at least one needs matching subtitles.</summary>
        PartialPreferredSubtitles = 11,

        /// <summary>No usable media; all acquisition targets are paused.</summary>
        Paused = 12
    }
}
