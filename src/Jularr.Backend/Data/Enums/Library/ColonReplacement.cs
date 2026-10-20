namespace Jularr.Backend.Data.Enums.Library
{
    // What replaces a colon in a name, as in the *arrs: smart turns ": " into " - " and any other colon into a dash.
    public enum ColonReplacement : byte
    {
        Delete = 1,
        Dash = 2,
        SpaceDash = 3,
        SpaceDashSpace = 4,
        Smart = 5,
        Custom = 6
    }
}
