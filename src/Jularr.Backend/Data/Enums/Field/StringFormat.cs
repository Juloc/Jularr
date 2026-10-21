namespace Jularr.Backend.Data.Enums.Field
{
    public enum StringFormat : byte
    {
        PlainText = 0,
        Email = 1,
        Locale = 2,
        HostName = 3,
        AbsolutePath = 4,
        SingleLine = 5,
        MacAddress = 6,
        IPv4Address = 7,
        RelativePath = 8,
        HttpUrl = 9,

        // HTTP or NNTP service address.
        ServiceUrl = 10,

        // Two-letter ISO 639-1 language, lower case.
        Language = 11
    }
}
