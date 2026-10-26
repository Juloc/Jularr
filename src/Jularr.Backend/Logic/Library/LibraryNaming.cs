using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Library;

namespace Jularr.Backend.Logic.Library
{
    // Path safety and collision alternatives shared by imports, uploads and renames.
    internal static class LibraryNaming
    {
        private static readonly HashSet<char> InvalidNameCharacters = [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '/', '\\', '|', '?', '*'];


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

        // A value inside a name: characters no file system takes become spaces, or vanish when the Admin does not want them replaced; its ends keep dots, as in "Title...".
        public static string CleanPart(string value, bool replaces = true)
        {
            static bool IsInvalid(char character) => InvalidNameCharacters.Contains(character) || char.IsControl(character);
            var kept = value.Where(character => replaces || !IsInvalid(character)).Select(character => IsInvalid(character) ? ' ' : character);
            return string.Join(' ', new string(kept.ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        public static string SafeName(string value)
        {
            var cleaned = new string(value.Select(character => InvalidNameCharacters.Contains(character) || char.IsControl(character) ? ' ' : character).ToArray());
            return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ') is { Length: > 0 } name ? name : "Untitled";
        }
    }
}
