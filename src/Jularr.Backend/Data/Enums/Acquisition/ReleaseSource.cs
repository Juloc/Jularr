namespace Jularr.Backend.Data.Enums.Acquisition
{
    // Source of a parsed release name.
    internal enum ReleaseSource
    {
        Unknown,
        WebDl,
        WebRip,
        BluRay,
        BluRayRip,
        Hdtv,
        Dvd,
        Sdtv,

        // Copies made before a film's home release.
        Cam,
        Telesync,
        Telecine,
        Workprint
    }

    // What a release is beyond its source, as Sonarr and Radarr name it.
    internal enum ReleaseQualityModifier
    {
        None,
        Remux,
        BrDisk,
        RawHd,
        Regional,
        Screener
    }
}
