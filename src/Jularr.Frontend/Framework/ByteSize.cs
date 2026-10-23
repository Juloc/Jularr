using System.Globalization;

namespace Jularr.Frontend.Framework
{
    // File and volume sizes in decimal units, as disks and NAS systems label them.
    public static class ByteSize
    {
        private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

        public static string Format(long bytes)
        {
            double value = bytes;
            var unit = 0;
            while (value >= 1000 && unit < Units.Length - 1)
            {
                value /= 1000;
                unit++;
            }

            return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + Units[unit];
        }
    }
}
