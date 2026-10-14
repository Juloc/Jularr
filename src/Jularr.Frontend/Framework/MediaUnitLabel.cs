using System.Globalization;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Frontend.Framework
{
    // Localized structure label such as "Season 1 · Episode 2".
    public static class MediaUnitLabel
    {
        // The unit's own title follows its number.
        public static string? Format(UiText texts, WorkUnitType? unit, decimal? number, decimal? parent, string? title) =>
            Format(texts, unit, number, parent) is { } label && title is { } name ? label + " · " + name : Format(texts, unit, number, parent);

        public static string? Format(UiText texts, WorkUnitType? unit, decimal? number, decimal? parent)
        {
            if (unit is not { } type || number is not { } value)
            {
                return null;
            }

            var label = texts["library.unit." + type] + " " + value.ToString("0.###", CultureInfo.InvariantCulture);
            return parent is { } group && WorkUnitTypes.ParentOf(type) is { } parentType
                ? texts["library.unit." + parentType] + " " + group.ToString("0.###", CultureInfo.InvariantCulture) + " · " + label
                : label;
        }
    }
}
