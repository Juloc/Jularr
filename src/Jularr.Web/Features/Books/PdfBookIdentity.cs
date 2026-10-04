using System.Globalization;
using System.Text.RegularExpressions;

namespace Jularr.Web.Features.Books;

/// <summary>The title and author a PDF book is shown with; the author is null when neither metadata nor the file name names one.</summary>
public sealed record PdfBookIdentityResult(string Title, string? Author);

/// <summary>
/// The one owner of what a PDF's Info metadata may say about its book. Browser-printed and converted PDFs carry
/// junk there (the source URL or file path as title, the printing site as author), so every value is validated and
/// the cleaned file name ("Author - Title") is the fallback for both.
/// </summary>
public static partial class PdfBookIdentity
{
    private const int MaxAuthorLength = 120;

    /// <summary>
    /// Title and author of an imported PDF. A usable Info title also vouches for the Info author; an unusable title
    /// marks the whole Info block as untrustworthy, so the author then comes from the file name only.
    /// </summary>
    public static PdfBookIdentityResult Resolve(string? infoTitle, string? infoAuthor, string fileName)
    {
        var fromFileName = FromFileName(fileName);
        var title = UsableTitle(infoTitle);
        if (title is null || (fromFileName.Author is not null && IsFileNameStem(title, fileName)))
        {
            return fromFileName;
        }

        return new PdfBookIdentityResult(title, UsableAuthor(infoAuthor) ?? fromFileName.Author);
    }

    /// <summary>
    /// Whether a stored work title could not have come from valid metadata (a path, URL or encoded path). Re-analysis
    /// repairs only such titles, so a title the owner edited is never overwritten.
    /// </summary>
    public static bool IsGarbageTitle(string? title) => string.IsNullOrWhiteSpace(title) || LooksLikeLocation(title);

    /// <summary>
    /// The book title in a PDF's Info title, or null. Producer junk ("Microsoft Word - draft.docx"), locations (file://, http://,
    /// drive paths, percent-encoded paths) are ignored; a library label around the title
    /// ("The Project Gutenberg eBook #33283: …") is removed.
    /// </summary>
    public static string? UsableTitle(string? title)
    {
        var clean = title?.Trim();
        if (clean is not null && GutenbergPdfTitle().Match(clean) is { Success: true } gutenberg)
        {
            clean = gutenberg.Groups["title"].Value.Trim();
        }

        if (string.IsNullOrWhiteSpace(clean)
            || clean.Length < 2
            || LooksLikeLocation(clean)
            || clean.StartsWith("Microsoft Word", StringComparison.OrdinalIgnoreCase)
            || clean.Equals("untitled", StringComparison.OrdinalIgnoreCase)
            || FileExtension().IsMatch(clean))
        {
            return null;
        }

        return clean;
    }

    /// <summary>The Info author when it reads like a person or organisation name, otherwise null.</summary>
    public static string? UsableAuthor(string? author)
    {
        var clean = author?.Trim();
        if (string.IsNullOrWhiteSpace(clean)
            || clean.Length > MaxAuthorLength
            || LooksLikeLocation(clean)
            || clean.Contains('@', StringComparison.Ordinal)
            || clean.All(character => !char.IsLetter(character))
            || PlaceholderAuthor().IsMatch(clean))
        {
            return null;
        }

        return clean;
    }

    /// <summary>
    /// "Stephen King - Pet Sematary.pdf" gives title "Pet Sematary" and author "Stephen King"; a name without that pattern is
    /// the title alone.
    /// </summary>
    public static PdfBookIdentityResult FromFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var spaced = Regex.Replace(Uri.UnescapeDataString(stem).Replace('_', ' '), @"\s+", " ").Trim();
        if (AuthorTitlePattern().Match(spaced) is { Success: true } match && match.Groups["author"].Value.Any(char.IsLetter))
        {
            return new PdfBookIdentityResult(ToTitle(match.Groups["title"].Value), match.Groups["author"].Value.Trim());
        }

        var title = ToTitle(spaced.Replace('.', ' '));
        return new PdfBookIdentityResult(title.Length == 0 ? "PDF" : title, null);
    }

    private static string ToTitle(string text) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Regex.Replace(text, @"\s+", " ").Trim());

    private static bool LooksLikeLocation(string text) =>
        text.Contains("://", StringComparison.Ordinal)
        || text.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
        || text.Contains('\\', StringComparison.Ordinal)
        || DrivePath().IsMatch(text)
        || PercentEncoding().IsMatch(text)
        || (text.Length > 1 && text[0] == '/' && text.Contains('/', StringComparison.Ordinal) && !text.Contains(' ', StringComparison.Ordinal));

    private static bool IsFileNameStem(string title, string fileName)
    {
        static string Normalize(string text) =>
            Regex.Replace(Uri.UnescapeDataString(text).Replace('_', ' ').Replace('.', ' '), @"\s+", " ").Trim();

        return Normalize(title).Equals(Normalize(Path.GetFileNameWithoutExtension(fileName)), StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(
        @"^(?:the\s+)?project\s+gutenberg'?s?\s+e-?book(?:\s*#\s*\d+)?\s*(?::|,|\s+of\b)\s*(?<title>.+?)(?:,\s+by\s+.+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GutenbergPdfTitle();

    [GeneratedRegex(@"\.(docx?|pdf|indd|tex|rtf|odt)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileExtension();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePath();

    [GeneratedRegex(@"%[0-9A-Fa-f]{2}", RegexOptions.CultureInvariant)]
    private static partial Regex PercentEncoding();

    [GeneratedRegex(@"^(?:unknown|anonymous|admin|administrator|user|author|none|n/?a|null)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderAuthor();

    [GeneratedRegex(@"^(?<author>[^\-–—]{2,60}?)\s+[-–—]\s+(?<title>\S.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorTitlePattern();
}
