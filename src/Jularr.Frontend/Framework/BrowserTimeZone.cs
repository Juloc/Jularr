using Microsoft.AspNetCore.Http;

namespace Jularr.Frontend.Framework
{
    // Calendar dates are civil dates, not UTC instants. The browser publishes its IANA zone
    // in a non-authentication cookie, and pages use it solely to calculate "today".
    public static class BrowserTimeZone
    {
        public const string CookieName = "jularr_time_zone";

        public static DateOnly Today(HttpRequest request) =>
            TodayAt(DateTimeOffset.UtcNow, request.Cookies[CookieName]);

        public static DateOnly TodayAt(DateTimeOffset instant, string? zoneId)
        {
            if (string.IsNullOrEmpty(zoneId) || zoneId.Length > 80
                || zoneId.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '/' or '_' or '-' or '+' or '.')))
                return DateOnly.FromDateTime(instant.UtcDateTime);

            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
                return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
            }
            catch (TimeZoneNotFoundException)
            {
                return DateOnly.FromDateTime(instant.UtcDateTime);
            }
            catch (InvalidTimeZoneException)
            {
                return DateOnly.FromDateTime(instant.UtcDateTime);
            }
        }
    }
}
