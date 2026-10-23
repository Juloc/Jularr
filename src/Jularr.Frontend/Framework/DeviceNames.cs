namespace Jularr.Frontend.Framework
{
    // A device without its own name is shown with the default name in the reader's language.
    public static class DeviceNames
    {
        public static string Show(string? name, UiText texts) => name ?? texts["devices.name.browser"];
    }
}
