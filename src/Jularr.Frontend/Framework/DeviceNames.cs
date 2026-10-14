using Jularr.Backend.Data.Public.Device.V1;

namespace Jularr.Frontend.Framework
{
    // Generic device names are shown in the reader's language; names a person chose stay as they are.
    public static class DeviceNames
    {
        public static string Show(string name, UiText texts) => name == StartDevicePairingDataV1.BrowserName ? texts["devices.name.browser"] : name;

        public static string Store(string name, UiText texts) => name == texts["devices.name.browser"] ? StartDevicePairingDataV1.BrowserName : name;
    }
}
