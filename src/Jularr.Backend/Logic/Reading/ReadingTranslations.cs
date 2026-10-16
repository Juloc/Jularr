using System.Globalization;
using System.Text.Json;

namespace Jularr.Backend.Logic.Reading
{
    // A translated section lives beside the section: one plain text per block, in block order.
    internal static class ReadingTranslations
    {
        private const int MaximumBlockLength = 20_000;

        public static string Path(int sectionIndex, string language) =>
            ":translation/" + language + "/" + sectionIndex.ToString("D5", CultureInfo.InvariantCulture);

        // Only an array of bounded texts counts; anything else is refused.
        public static IReadOnlyList<string>? Parse(string json)
        {
            try
            {
                var blocks = JsonSerializer.Deserialize<string?[]>(json);
                return blocks is not null && blocks.All(block => block is not null && block.Length <= MaximumBlockLength) ? [.. blocks!] : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
