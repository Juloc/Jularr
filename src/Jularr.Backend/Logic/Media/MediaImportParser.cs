using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Logic.Media
{
    // A provider identity as the identity tables hold it.
    internal sealed record MediaImportId(string Namespace, string Value);

    // One entry of a list after validation; a problem makes it unusable, never silently different.
    internal sealed record MediaImportItem(int Index, string TypeKey, MediaType? Type, MediaSubtype? Subtype, string? Title, int? Year, string? Creator, IReadOnlyList<MediaImportId> Ids,
        string? Problem, string? ProblemDetail, IReadOnlyList<string>? Authors = null);

    // The one-time media list, version 1: a closed set of keys, bounded sizes, plain text only.
    internal static partial class MediaImportParser
    {
        private static readonly string[] ItemKeys = ["type", "ids", "title", "year", "author", "artist"];
        private static readonly string[] IdKeys = ["tmdb", "tvdb", "imdb", "isbn", "openlibrary", "anilist", "musicbrainz", "igdb"];
        private static readonly UTF8Encoding Strict = new(false, true);

        private static readonly Dictionary<string, (MediaType Type, MediaSubtype? Subtype)> Types = new(StringComparer.Ordinal)
        {
            ["movie"] = (MediaType.Movie, null),
            ["series"] = (MediaType.Series, null),
            ["anime"] = (MediaType.Series, MediaSubtype.Anime),
            ["book"] = (MediaType.Book, null),
            ["manga"] = (MediaType.Book, MediaSubtype.Manga),
            ["lightnovel"] = (MediaType.Book, MediaSubtype.LightNovel),
            ["music"] = (MediaType.Music, null),
            ["game"] = (MediaType.Game, null)
        };

        [GeneratedRegex("^[0-9]{1,9}$", RegexOptions.CultureInvariant)]
        private static partial Regex Digits();

        [GeneratedRegex("^tt[0-9]{7,10}$", RegexOptions.CultureInvariant)]
        private static partial Regex Imdb();

        [GeneratedRegex("^OL[0-9]{1,12}W$", RegexOptions.CultureInvariant)]
        private static partial Regex OpenLibrary();

        public static string TypeKey(MediaType type, MediaSubtype? subtype) =>
            Types.First(entry => entry.Value.Type == type && entry.Value.Subtype == subtype).Key;

        // The kinds of title a list may name, in the order the page offers them.
        public static IReadOnlyList<string> TypeKeys { get; } = [.. Types.Keys];

        // Document-level faults reject the whole list; an entry's fault only marks that entry.
        public static IReadOnlyList<MediaImportItem> Parse(string json)
        {
            var field = nameof(MediaImportDataV1.Json);
            json = json.TrimStart('﻿');
            if (Encoding.UTF8.GetByteCount(json) > MediaImportFields.MaximumBytes) throw new DataValidationException(field, "import_too_large");

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            }
            catch (JsonException)
            {
                throw new DataValidationException(field, "import_invalid_json");
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new DataValidationException(field, "import_invalid_structure");

                JsonElement? items = null;
                var version = false;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in root.EnumerateObject())
                {
                    if (!seen.Add(property.Name)) throw new DataValidationException(field, "import_unknown_key");
                    switch (property.Name)
                    {
                        case "version":
                            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var number) || number != 1) throw new DataValidationException(field, "import_unsupported_version");
                            version = true;
                            break;
                        case "items":
                            if (property.Value.ValueKind != JsonValueKind.Array) throw new DataValidationException(field, "import_invalid_structure");
                            items = property.Value;
                            break;
                        default:
                            throw new DataValidationException(field, "import_unknown_key");
                    }
                }

                if (!version) throw new DataValidationException(field, "import_unsupported_version");
                if (items is not { } list) throw new DataValidationException(field, "import_invalid_structure");
                var length = list.GetArrayLength();
                if (length == 0) throw new DataValidationException(field, "import_empty");
                if (length > MediaImportFields.MaximumItems) throw new DataValidationException(field, "import_too_many_items");

                var result = new List<MediaImportItem>(length);
                foreach (var element in list.EnumerateArray()) result.Add(ReadItem(result.Count, element));
                return result;
            }
        }

        // A broken entry still shows its title, so a person can find it in the file.
        private static MediaImportItem ReadItem(int index, JsonElement element)
        {
            MediaImportItem item;
            try
            {
                item = ReadEntry(index, element);
            }
            catch (InvalidOperationException)
            {
                item = Invalid(index, "", "invalid_text");
            }

            if (item.Problem is null || item.Title is not null || element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("title", out var name) || name.ValueKind != JsonValueKind.String) return item;
            try
            {
                return item with { Title = Text(name.GetString()!, out var problem) is { } text && problem is null ? (text.Length > 120 ? text[..120] : text) : null };
            }
            catch (InvalidOperationException)
            {
                return item;
            }
        }

        private static MediaImportItem ReadEntry(int index, JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return Invalid(index, "", "not_object");

            string? typeKey = null, title = null, creator = null;
            IReadOnlyList<string>? authors = null;
            int? year = null;
            JsonElement? ids = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!ItemKeys.Contains(property.Name, StringComparer.Ordinal)) return Invalid(index, typeKey, "unknown_key", property.Name);
                if (!seen.Add(property.Name)) return Invalid(index, typeKey, "duplicate_key", property.Name);
                var value = property.Value;
                switch (property.Name)
                {
                    case "type":
                        typeKey = value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim().ToLowerInvariant() : "";
                        break;
                    case "ids":
                        if (value.ValueKind != JsonValueKind.Object) return Invalid(index, typeKey, "invalid_ids");
                        ids = value;
                        break;
                    case "year":
                        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number is < 1800 or > 2200) return Invalid(index, typeKey, "invalid_year");
                        year = number;
                        break;
                    case "author" when value.ValueKind == JsonValueKind.Array:
                        var names = new List<string>();
                        foreach (var author in value.EnumerateArray())
                        {
                            if (author.ValueKind != JsonValueKind.String) return Invalid(index, typeKey, "invalid_text", "author");
                            var name = Text(author.GetString()!, out var authorProblem);
                            if (authorProblem is not null || name is null) return Invalid(index, typeKey, "invalid_text", "author");
                            names.Add(name);
                        }
                        var joined = string.Join(", ", names);
                        if (names.Count is 0 or > 50 || joined.Length > 500) return Invalid(index, typeKey, "invalid_text", "author");
                        if (creator is not null) return Invalid(index, typeKey, "duplicate_key", "author");
                        authors = names;
                        creator = joined;
                        break;
                    default:
                        if (value.ValueKind != JsonValueKind.String) return Invalid(index, typeKey, "invalid_text", property.Name);
                        var text = Text(value.GetString()!, out var problem);
                        if (problem is not null) return Invalid(index, typeKey, problem, property.Name);
                        if (property.Name == "title") title = text;
                        else if (creator is not null && creator != text) return Invalid(index, typeKey, "duplicate_key", "author");
                        else creator = text;
                        break;
                }
            }

            if (typeKey is null) return Invalid(index, "", "missing_type");
            if (!Types.TryGetValue(typeKey, out var kind)) return Invalid(index, "", "unknown_type", typeKey);

            var identities = new List<MediaImportId>();
            if (ids is { } idObject)
            {
                var kinds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in idObject.EnumerateObject())
                {
                    if (!IdKeys.Contains(property.Name, StringComparer.Ordinal)) return Invalid(index, typeKey, "unknown_id_key", property.Name);
                    if (!kinds.Add(property.Name)) return Invalid(index, typeKey, "duplicate_key", property.Name);
                    var raw = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString()!,
                        JsonValueKind.Number => property.Value.GetRawText(),
                        _ => null
                    };
                    if (raw is null || raw.Length > 64) return Invalid(index, typeKey, "invalid_id", property.Name);
                    var identity = Identity(property.Name, raw.Trim(), kind.Type, kind.Subtype, out var problem);
                    if (identity is null) return Invalid(index, typeKey, problem!, property.Name);
                    identities.Add(identity);
                }
            }

            if (identities.Count == 0 && title is null) return Invalid(index, typeKey, "no_identity");
            return new MediaImportItem(index, typeKey, kind.Type, kind.Subtype, title, year, creator, identities, null, null, authors);
        }

        private static MediaImportItem Invalid(int index, string? typeKey, string problem, string? detail = null) =>
            new(index, typeKey ?? "", null, null, null, null, null, [], problem, detail is null ? null : Detail(detail));

        // A key or value quoted in a message cannot carry markup or break a line.
        private static string Detail(string value)
        {
            var clean = new string([.. value.Where(character => !char.IsControl(character) && character != '<' && character != '>')]);
            return clean.Length > 40 ? clean[..40] : clean;
        }

        // Plain text: trimmed, one space between words, no control characters, at most 500 characters.
        private static string? Text(string value, out string? problem)
        {
            problem = null;
            try
            {
                Strict.GetBytes(value);
            }
            catch (EncoderFallbackException)
            {
                problem = "invalid_text";
                return null;
            }

            var builder = new StringBuilder(value.Length);
            var space = false;
            foreach (var character in value.Trim())
            {
                if (char.IsWhiteSpace(character))
                {
                    space = true;
                    continue;
                }

                if (char.IsControl(character))
                {
                    problem = "invalid_text";
                    return null;
                }

                if (space && builder.Length > 0) builder.Append(' ');
                space = false;
                builder.Append(character);
            }

            if (builder.Length == 0)
            {
                problem = "invalid_text";
                return null;
            }

            if (builder.Length > 500)
            {
                problem = "too_long";
                return null;
            }

            return builder.ToString();
        }

        public static MediaImportId? Identity(string kind, string raw, MediaType type, MediaSubtype? subtype, out string? problem)
        {
            problem = null;
            var books = type == MediaType.Book;
            var video = type is MediaType.Movie or MediaType.Series;
            string? @namespace = kind switch
            {
                "tmdb" when video => type == MediaType.Movie ? "tmdb_movie" : "tmdb_tv",
                "tvdb" when type == MediaType.Series => "tvdb",
                "imdb" when video => "imdb",
                "isbn" when books => "isbn",
                "openlibrary" when books => "openlibrary",
                "anilist" when type == MediaType.Series && subtype == MediaSubtype.Anime || books && subtype is MediaSubtype.Manga or MediaSubtype.LightNovel => "anilist",
                "musicbrainz" when type == MediaType.Music => "musicbrainz",
                "igdb" when type == MediaType.Game => "igdb",
                _ => null
            };
            if (@namespace is null)
            {
                problem = "id_not_for_type";
                return null;
            }

            var value = kind switch
            {
                "tmdb" or "tvdb" or "anilist" or "igdb" => Digits().IsMatch(raw) && int.Parse(raw, CultureInfo.InvariantCulture) > 0 ? int.Parse(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : null,
                "imdb" => Imdb().IsMatch(raw.ToLowerInvariant()) ? raw.ToLowerInvariant() : null,
                "isbn" => Isbn(raw),
                "openlibrary" => OpenLibrary().IsMatch(raw.ToUpperInvariant()) ? raw.ToUpperInvariant() : null,
                _ => Guid.TryParseExact(raw, "D", out var guid) ? guid.ToString("D") : null
            };
            if (value is null) problem = "invalid_id";
            return value is null ? null : new MediaImportId(@namespace, value);
        }

        // An ISBN-10 or ISBN-13 with a correct check digit, always as ISBN-13 without hyphens.
        public static string? Isbn(string raw)
        {
            var digits = new string([.. raw.Where(character => character is not ('-' or ' '))]).ToUpperInvariant();
            if (digits.Length == 10 && digits[..9].All(char.IsAsciiDigit) && (char.IsAsciiDigit(digits[9]) || digits[9] == 'X'))
            {
                var sum = 0;
                for (var position = 0; position < 10; position++) sum += (10 - position) * (digits[position] == 'X' ? 10 : digits[position] - '0');
                return sum % 11 == 0 ? Isbn13("978" + digits[..9]) : null;
            }

            if (digits.Length == 13 && digits.All(char.IsAsciiDigit) && Isbn13(digits[..12]) == digits) return digits;
            return null;
        }

        private static string Isbn13(string twelve)
        {
            var sum = 0;
            for (var position = 0; position < 12; position++) sum += (twelve[position] - '0') * (position % 2 == 0 ? 1 : 3);
            return twelve + ((10 - sum % 10) % 10).ToString(CultureInfo.InvariantCulture);
        }

        // The ISBN-10 of a 978 number, for a library that stored it that way.
        public static string? Isbn10(string isbn13)
        {
            if (isbn13.Length != 13 || !isbn13.StartsWith("978", StringComparison.Ordinal)) return null;
            var nine = isbn13.Substring(3, 9);
            var sum = 0;
            for (var position = 0; position < 9; position++) sum += (10 - position) * (nine[position] - '0');
            var check = (11 - sum % 11) % 11;
            return nine + (check == 10 ? "X" : check.ToString(CultureInfo.InvariantCulture));
        }

        // The name of an identity in the file.
        public static string Kind(string @namespace) => @namespace switch
        {
            "tmdb_movie" or "tmdb_tv" => "tmdb",
            var other => other
        };

        // A list of its own for rows that wait for the next run, with the provider entries already chosen.
        public static string Write(IEnumerable<MediaImportItem> items)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", 1);
                writer.WriteStartArray("items");
                foreach (var item in items)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", item.TypeKey);
                    if (item.Ids.Count > 0)
                    {
                        writer.WriteStartObject("ids");
                        foreach (var identity in item.Ids) writer.WriteString(Kind(identity.Namespace), identity.Value);
                        writer.WriteEndObject();
                    }

                    if (item.Title is not null) writer.WriteString("title", item.Title);
                    if (item.Year is { } year) writer.WriteNumber("year", year);
                    if (item.Authors is { } authors)
                    {
                        writer.WriteStartArray("author");
                        foreach (var author in authors) writer.WriteStringValue(author);
                        writer.WriteEndArray();
                    }
                    else if (item.Creator is not null) writer.WriteString("author", item.Creator);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
