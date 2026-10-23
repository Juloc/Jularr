namespace Jularr.Backend.Data.Enums.Media
{
    /// <summary>
    /// What a title offers the profile: the state of the local library, the state of its
    /// acquisition and whether the audio is in the profile's language. The Frontend renders
    /// this state; it never derives one of its own.
    /// </summary>
    public enum MediaAvailabilityState : byte
    {
        /// <summary>Everything the profile monitors is here, and the audio language is unknown.</summary>
        Available = 1,

        /// <summary>Some of the monitored units are here, with audio in the profile's language.</summary>
        Partial = 2,

        /// <summary>Nothing of the title is here and nothing is on the way.</summary>
        Missing = 3,

        /// <summary>A download of the title runs.</summary>
        Downloading = 4,

        /// <summary>The title is wanted and its search runs.</summary>
        Searching = 5,

        /// <summary>The title is requested and waits for its approval or its search.</summary>
        Requested = 6,

        /// <summary>Everything the profile monitors is here, with audio in the profile's language.</summary>
        ProfileAudio = 7,

        /// <summary>Everything the profile monitors is here, but only in other audio languages.</summary>
        OtherAudio = 8,

        /// <summary>Some of the monitored units are here, with audio in other languages.</summary>
        PartialOtherAudio = 9
    }
}
