using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jularr.Web.Features.Naming;

/// <summary>One <c>{prefix Token:format suffix}</c> of a template; the key is lower case with the token's own separator read as a space.</summary>
public readonly record struct NamingToken(string Prefix, string RawToken, string Separator, string Format, string Suffix)
{
    public string Key => (Separator.Length == 0 ? RawToken : RawToken.Replace(Separator, " ", StringComparison.Ordinal)).ToLowerInvariant();

    internal static NamingToken From(Match match) =>
        new(match.Groups["prefix"].Value, match.Groups["token"].Value, match.Groups["separator"].Value, match.Groups["format"].Value, match.Groups["suffix"].Value);
}

/// <summary>
/// The one <c>{token}</c> template grammar and the filename-safety rules around it: word-separator substitution, case-by-token-casing, zero-padding for numeric
/// tokens, optional prefix/suffix that vanish with an empty value, repeated-separator collapsing and reserved device-name suffixing. Token values are resolved by
/// the caller. The Anime formatter (Sonarr-compatible tokens, per-token illegal-character table and colon strategies) owns its values and value cleaning and renders
/// through <see cref="Render(string, Func{NamingToken, string}, string)"/>; the other media types use the resolver overload, which replaces illegal characters
/// with "_".
/// </summary>
public static partial class NamingTemplateEngine
{
    public const int MaxNameBytes = 255;

    public const string IllegalLiteralCharacters = "\\/:*?\"<>|";

    private static readonly HashSet<char> InvalidNameCharacters = Path.GetInvalidFileNameChars()
        .Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|'])
        .ToHashSet();

    [GeneratedRegex(
        @"\{(?<prefix>[- ._\[(]*)(?<token>[a-z0-9]+(?:(?<separator>[- ._]+)[a-z0-9]+)?)(?::(?<format>[ ,a-z0-9+-]+(?<![- ])))?(?<suffix>[- ._)\]]*)\}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"([- ._])\1+", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedSeparatorRegex();

    [GeneratedRegex(@"[- ._]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingSeparatorRegex();

    [GeneratedRegex(@"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedDeviceNameRegex();

    /// <summary>Resolves one token to its rendered value; null means the token is unknown or not
    /// available in the current scope (the caller should record an error), empty string means the
    /// token is known but has no value here (renders as nothing, like anime naming).</summary>
    public delegate string? TokenResolver(string key, int padZeros, List<string>? errors);

    public static bool ExceedsNameLimit(string name) =>
        Encoding.UTF8.GetByteCount(name) > MaxNameBytes;

    public static string Render(
        string template,
        TokenResolver resolveToken,
        List<string>? errors = null)
    {
        ArgumentNullException.ThrowIfNull(resolveToken);
        return Render(template, token => RenderToken(token, resolveToken, errors));
    }

    /// <summary>
    /// Renders every <c>{token}</c> with <paramref name="renderToken"/>, then collapses repeated and trailing separators, trims <paramref name="leadingTrim"/>
    /// from the start and suffixes a reserved device name.
    /// </summary>
    public static string Render(string template, Func<NamingToken, string> renderToken, string leadingTrim = " ._")
    {
        ArgumentNullException.ThrowIfNull(renderToken);

        var result = TokenRegex().Replace(template ?? "", match => renderToken(NamingToken.From(match)));
        result = RepeatedSeparatorRegex().Replace(result, "$1");
        result = TrailingSeparatorRegex().Replace(result, "");
        result = result.TrimStart(leadingTrim.ToCharArray());
        return result.Length == 0
            ? result
            : ReservedDeviceNameRegex().Replace(result, match => $"{match.Groups[1].Value}_");
    }

    /// <summary>The token's own separator replaces the spaces of a multi-word value, and its letter case (all lower or all upper) decides the value's case.</summary>
    public static string ApplyCasing(NamingToken token, string value)
    {
        if (token.Separator.Length > 0 && token.Separator != " ")
        {
            value = value.Replace(" ", token.Separator, StringComparison.Ordinal);
        }

        if (token.RawToken.Any(char.IsLetter))
        {
            if (token.RawToken.Where(char.IsLetter).All(char.IsLower))
            {
                value = value.ToLowerInvariant();
            }
            else if (token.RawToken.Where(char.IsLetter).All(char.IsUpper))
            {
                value = value.ToUpperInvariant();
            }
        }

        return value;
    }

    // Blanket-sanitizes characters that are illegal (or merely risky on some filesystems/SMB
    // shares) in file names to "_", matching MangaLibraryPlacement.SafeName/ReadingLibraryPlacement
    // so the default naming profile renders exactly what Jularr already wrote to disk before
    // naming profiles existed.
    public static string CleanFileName(string value)
    {
        var cleaned = new string((value ?? "")
                .Select(character => InvalidNameCharacters.Contains(character) || char.IsControl(character) ? '_' : character)
                .ToArray())
            .Trim(' ', '.', '_');
        return cleaned;
    }

    /// <summary>Validates literal (non-token) template text for illegal characters and malformed braces.</summary>
    public static IReadOnlyList<string> ValidateLiteral(string label, string? template)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(template))
        {
            errors.Add($"{label} is required.");
            return errors;
        }

        if (template.Length > 500)
        {
            errors.Add($"{label} must be at most 500 characters.");
            return errors;
        }

        var literal = TokenRegex().Replace(template, "");
        if (literal.Contains('{') || literal.Contains('}'))
        {
            errors.Add($"{label} contains an unclosed or malformed {{token}}.");
        }

        var illegal = literal
            .Where(character => IllegalLiteralCharacters.Contains(character) || char.IsControl(character))
            .Distinct()
            .ToArray();
        if (illegal.Length > 0)
        {
            errors.Add($"{label} contains characters that are illegal in file names: {string.Join(" ", illegal)}");
        }

        return errors;
    }

    public static string Pad(long value, int zeros) =>
        value.ToString(zeros > 0 ? $"D{zeros}" : "D", CultureInfo.InvariantCulture);

    // Chapter numbers can be fractional (e.g. "Chapter 10.5"); only the whole part is padded.
    public static string PadDecimal(double value, int zeros)
    {
        var whole = Math.Floor(value);
        var wholeText = Pad((long)whole, zeros);
        var fraction = value - whole;
        return fraction <= 0.0001
            ? wholeText
            : wholeText + fraction.ToString("0.#", CultureInfo.InvariantCulture)[1..];
    }

    private static string RenderToken(NamingToken token, TokenResolver resolveToken, List<string>? errors)
    {
        if (token.Format.Any(character => character != '0'))
        {
            errors?.Add($"{{{token.RawToken}}} only accepts zero padding such as :00.");
            return "";
        }

        var value = resolveToken(token.Key, token.Format.Length, errors);
        return string.IsNullOrEmpty(value) ? "" : token.Prefix + CleanFileName(ApplyCasing(token, value)) + token.Suffix;
    }
}
