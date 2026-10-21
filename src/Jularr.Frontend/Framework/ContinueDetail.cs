using System.Globalization;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Discover.V1;

namespace Jularr.Frontend.Framework
{
    // Where the profile stands with a title to continue, in one short line: "S01 E05 · 22 min left", "Chapter 183",
    // "Track 4 · 3 min left" or "42%". Whatever is unknown is left out; nothing is invented.
    public static class ContinueDetail
    {
        public static string? Format(Func<string, string> text, ContinueItemV1 item)
        {
            var parts = new List<string>();
            var unit = Unit(text, item.WorkUnitTypeId, item.UnitNumber, item.ParentUnitNumber);
            if (unit is not null) parts.Add(unit);

            // Played media say how long is left; read media their unit, else the share read.
            if (item.DurationMs is > 0 and { } duration && item.PositionMs > 0 && duration > item.PositionMs)
            {
                parts.Add(Left(text, duration - item.PositionMs));
            }
            else if (unit is null && item.Progression is { } share and > 0)
            {
                parts.Add(Math.Clamp(share, 0, 1).ToString("P0", CultureInfo.CurrentCulture));
            }

            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }

        // The unit as a short code or word; an episode with its season reads as S01 E05.
        public static string? Unit(Func<string, string> text, WorkUnitType? type, decimal? number, decimal? parent)
        {
            if (type is not { } unit || number is not { } value)
            {
                return null;
            }

            return unit switch
            {
                WorkUnitType.Episode when parent is { } season => string.Format(CultureInfo.CurrentCulture, text("media.episodeCode"), Padded(season), Padded(value)),
                WorkUnitType.Episode => string.Format(CultureInfo.CurrentCulture, text("media.episodeOnly"), Padded(value)),
                _ => text("library.unit." + unit) + " " + value.ToString("0.###", CultureInfo.InvariantCulture)
            };
        }

        // Whole minutes round up, so a title is never "0 min left" while something remains.
        public static string Left(Func<string, string> text, long remainingMs)
        {
            var minutes = (long)Math.Ceiling(remainingMs / 60000d);
            if (minutes < 60)
            {
                return string.Format(CultureInfo.CurrentCulture, text("media.timeLeft.minutes"), minutes);
            }

            var hours = minutes / 60;
            var rest = minutes % 60;
            return rest == 0
                ? string.Format(CultureInfo.CurrentCulture, text("media.timeLeft.hours"), hours)
                : string.Format(CultureInfo.CurrentCulture, text("media.timeLeft.hoursMinutes"), hours, rest);
        }

        private static string Padded(decimal number) => number == decimal.Truncate(number)
            ? ((long)number).ToString("00", CultureInfo.InvariantCulture)
            : number.ToString("00.###", CultureInfo.InvariantCulture);
    }
}
