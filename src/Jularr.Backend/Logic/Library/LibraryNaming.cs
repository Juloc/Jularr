using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Logic.Acquisition;

namespace Jularr.Backend.Logic.Library
{
    // The library layout shared by imports and renames; every result is a path stem below the library root.
    internal static class LibraryNaming
    {
        private static readonly HashSet<char> InvalidNameCharacters = [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '/', '\\', '|', '?', '*'];


        public static string Book(string title, int? year, string? author) =>
            (BookReleaseJudge.Authors(author).FirstOrDefault() is { } writer ? SafeName(writer) + "/" : "") + Folder(title, year) + "/" + SafeName(title);

        // A volume-less file is the whole series.
        public static string Volume(string title, decimal? volume)
        {
            var name = SafeName(title);
            return volume is { } value ? $"{name}/{name} v{Number(value)}" : $"{name}/{name}";
        }

        public static string Track(string? artist, string album, int? year, decimal number, string? name)
        {
            var numberText = Number(number);
            return (artist is { Length: > 0 } ? SafeName(artist) + "/" : "") + Folder(album, year) + "/" + numberText + " - " + (name is { Length: > 0 } ? SafeName(name) : "Track " + numberText);
        }

        public static string Game(string? platform, string title, int? year) => SafeName(platform ?? "Unknown") + "/" + Folder(title, year);

        // Taken names are never reused; a counter keeps both files.
        public static string Alternative(string stem, int attempt) => attempt <= 1 ? stem : $"{stem} ({attempt.ToString(CultureInfo.InvariantCulture)})";

        public static bool IsStorable(string relativePath)
        {
            try
            {
                StoredFileFields.RelativePath.Validate(relativePath, nameof(StoredFileFields.RelativePath));
                return true;
            }
            catch (DataValidationException)
            {
                return false;
            }
        }

        public static string SafeName(string value)
        {
            var cleaned = new string(value.Select(character => InvalidNameCharacters.Contains(character) || char.IsControl(character) ? ' ' : character).ToArray());
            return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ') is { Length: > 0 } name ? name : "Untitled";
        }

        private static string Folder(string title, int? year) => SafeName(title) + (year is { } value ? " (" + value.ToString(CultureInfo.InvariantCulture) + ")" : "");

        // Half units such as 1.5 keep their fraction.
        private static string Number(decimal value) => value.ToString("00.###", CultureInfo.InvariantCulture);
    }
}
