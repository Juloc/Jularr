using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jularr.Web.Features.Branding;

/// <summary>
/// The instance's own identity (#876): an optional display name, an optional logo and whether its hue replaces the accent. Everything here is
/// optional; an instance without any of it is plain Jularr. The logo bytes are not part of this value, only whether one exists and its version.
/// </summary>
public sealed record InstanceBrandingSettings(string? Name, bool HueBranding, int? Hue, bool HasLogo, long LogoVersion)
{
    public const string ProductName = "Jularr";

    /// <summary>The small line that stays beside the instance's own brand. Like the product name it is a fixed brand lockup, not translated text.</summary>
    public const string Attribution = "by Jularr";

    public static InstanceBrandingSettings Default { get; } = new(null, false, null, false, 0);

    /// <summary>The name every surface shows: the instance's own, else the product's.</summary>
    public string DisplayName => Name ?? ProductName;

    /// <summary>Whether the instance replaces the primary brand; then "by Jularr" stays beside it, so the product is never hidden.</summary>
    public bool IsCustom => Name is not null || HasLogo;

    public string? LogoUrl => HasLogo ? $"{BrandingEndpoints.LogoPath}?v={LogoVersion.ToString(CultureInfo.InvariantCulture)}" : null;

    /// <summary>The accent seed (#rrggbb) the hue stands for, or null while hue branding is off or has no hue.</summary>
    public string? HueSeed => HueBranding && Hue is { } hue ? BrandingColor.SeedOf(hue) : null;
}

/// <summary>The stored logo with the address-independent facts a response needs.</summary>
public sealed record StoredBrandingLogo(string ContentType, byte[] Bytes, long Version);

public enum BrandingLogoProblem
{
    None = 0,
    Empty = 1,
    TooLarge = 2,
    UnsupportedType = 3,
    UnsafeVector = 4
}

public static partial class BrandingValidation
{
    public const int MaxNameLength = 40;
    public const int MaxLogoBytes = 524_288;

    /// <summary>A trimmed display name, null when empty or when it is the product's own name (no "Jularr by Jularr"); false when it is too long or holds control characters.</summary>
    public static bool TryNormalizeName(string? value, out string? name)
    {
        var text = value?.Trim() ?? string.Empty;
        name = null;
        if (text.Length == 0 || string.Equals(text, InstanceBrandingSettings.ProductName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (text.Length > MaxNameLength || text.Any(char.IsControl))
        {
            return false;
        }

        name = text;
        return true;
    }

    /// <summary>
    /// Decides what an upload is by its bytes, never by its name or the type the client claims: PNG, JPEG, WebP, or an SVG that carries nothing but
    /// shapes (no script, no event handler, no external or embedded reference). The logo is only ever shown as an image, and is served sandboxed on top.
    /// </summary>
    public static BrandingLogoProblem Validate(ReadOnlySpan<byte> bytes, out string? contentType)
    {
        contentType = null;
        if (bytes.IsEmpty)
        {
            return BrandingLogoProblem.Empty;
        }

        if (bytes.Length > MaxLogoBytes)
        {
            return BrandingLogoProblem.TooLarge;
        }

        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            contentType = "image/png";
            return BrandingLogoProblem.None;
        }

        if (bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            contentType = "image/jpeg";
            return BrandingLogoProblem.None;
        }

        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            contentType = "image/webp";
            return BrandingLogoProblem.None;
        }

        var text = Encoding.UTF8.GetString(bytes);
        if (!text.Contains("<svg", StringComparison.OrdinalIgnoreCase))
        {
            return BrandingLogoProblem.UnsupportedType;
        }

        if (UnsafeVector().IsMatch(text) || ExternalReference().IsMatch(text))
        {
            return BrandingLogoProblem.UnsafeVector;
        }

        contentType = "image/svg+xml";
        return BrandingLogoProblem.None;
    }

    [GeneratedRegex(@"<\s*(script|foreignObject|iframe|embed|object)\b|\son[a-z]+\s*=|javascript:|<!ENTITY|<!DOCTYPE[^>]*\[", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeVector();

    [GeneratedRegex(@"(href|src)\s*=\s*[""']\s*(?!#)|url\(\s*[""']?\s*(?!#)", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalReference();
}

/// <summary>The colour a brand hue stands for. The accent palette derives everything else from this seed, so only the seed is needed.</summary>
public static class BrandingColor
{
    public static string SeedOf(int hue)
    {
        const double saturation = 0.62;
        const double lightness = 0.46;
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var section = (hue % 360 + 360) % 360 / 60d;
        var second = chroma * (1 - Math.Abs(section % 2 - 1));
        var (red, green, blue) = (int)section switch
        {
            0 => (chroma, second, 0d),
            1 => (second, chroma, 0d),
            2 => (0d, chroma, second),
            3 => (0d, second, chroma),
            4 => (second, 0d, chroma),
            _ => (chroma, 0d, second)
        };
        var offset = lightness - chroma / 2;
        string Channel(double value) => ((int)Math.Round((value + offset) * 255)).ToString("x2", CultureInfo.InvariantCulture);
        return $"#{Channel(red)}{Channel(green)}{Channel(blue)}";
    }
}
