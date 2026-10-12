namespace Jularr.Backend.Data.Enums.Acquisition
{
    // How far one search goes; Deep is explicit troubleshooting, never a background default.
    internal enum SearchDepth : byte
    {
        Fast = 1,
        Normal = 2,
        Deep = 3
    }
}
