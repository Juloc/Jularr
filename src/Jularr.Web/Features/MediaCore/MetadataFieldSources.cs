using System.Globalization;
using System.Text;

namespace Jularr.Web.Features.MediaCore;

/// <summary>
/// The deterministic field-level precedence model of #435, absorbed into the media core. Every
/// <see cref="WorkFieldProvenance"/> row records where a field's value came from; when a provider
/// refresh proposes a new value for a field, <see cref="ShouldReplace"/> decides whether it may win,
/// using a stable priority ladder:
///
/// <code>
/// manual override  &gt;  canonical preferred provider  &gt;  secondary provider  &gt;  local / NFO  &gt;  derived filename
/// </code>
///
/// A manual override is never overwritten by any provider. Exact per-field ladders can differ by
/// media type; this is the shared default the per-type adapters (later #556 children) refine.
/// </summary>
public static class MetadataFieldSources
{
    // Well-known non-provider sources (normalized, stable — never localise).
    public const string Owner = "owner";
    public const string Local = "local";
    public const string Nfo = "nfo";
    public const string Filename = "filename";

    // Priority tiers (lower value = higher precedence).
    public const int PriorityManual = 0;
    public const int PriorityPreferredProvider = 10;
    public const int PrioritySecondaryProvider = 20;
    public const int PriorityLocal = 30;
    public const int PriorityNfo = 40;
    public const int PriorityFilename = 50;
    public const int PriorityUnknown = 100;

    /// <summary>Normalizes a source key to its stored lowercase form.</summary>
    public static string Normalize(string? source) => (source ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// Priority of a source for a field. Manual override always wins; otherwise the preferred provider
    /// beats other providers, which beat local/NFO/filename fallbacks in that order.
    /// </summary>
    public static int PriorityFor(string? source, bool isManualOverride, string? preferredProvider = null)
    {
        if (isManualOverride)
        {
            return PriorityManual;
        }

        var normalized = Normalize(source);
        var preferred = Normalize(preferredProvider);

        return normalized switch
        {
            "" => PriorityUnknown,
            Owner => PriorityManual,
            Filename => PriorityFilename,
            Nfo => PriorityNfo,
            Local => PriorityLocal,
            _ when preferred.Length > 0 && normalized == preferred => PriorityPreferredProvider,
            _ => PrioritySecondaryProvider
        };
    }

    /// <summary>
    /// Whether an incoming value from <paramref name="incomingSource"/> should replace the value the
    /// <paramref name="current"/> provenance records. A manual override is only replaced by another
    /// manual override; otherwise a strictly higher-priority source wins, and an equal-priority source
    /// only wins when it is a fresh fetch from the same source (a refresh of the same origin).
    /// </summary>
    public static bool ShouldReplace(
        WorkFieldProvenance? current,
        string incomingSource,
        bool incomingIsManualOverride,
        string? preferredProvider = null) =>
        current is null || ShouldReplace(current.Source, current.IsManualOverride, incomingSource, incomingIsManualOverride, preferredProvider);

    /// <summary>
    /// The same ladder for a value whose provenance is stored beside it (a localized <c>(work, locale, field)</c> value or an
    /// artwork variant) rather than in a <see cref="WorkFieldProvenance"/> row.
    /// </summary>
    public static bool ShouldReplace(string currentSource, bool currentIsManualOverride, string incomingSource, bool incomingIsManualOverride, string? preferredProvider = null)
    {
        if (currentIsManualOverride && !incomingIsManualOverride)
        {
            return false;
        }

        var incomingPriority = PriorityFor(incomingSource, incomingIsManualOverride, preferredProvider);
        var currentPriority = PriorityFor(currentSource, currentIsManualOverride, preferredProvider);

        if (incomingPriority != currentPriority)
        {
            return incomingPriority < currentPriority;
        }

        // Same priority: only refresh when it is the same origin (keeps the winning source stable).
        return Normalize(incomingSource) == Normalize(currentSource);
    }
}

/// <summary>Shared normalization for titles and provider ids used across the media core (search/dedup only, never display).</summary>
public static class MediaCoreNormalization
{
    /// <summary>Case/diacritic/whitespace/punctuation-folded form of a title for de-duplication and matching.</summary>
    public static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var lowered = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(lowered.Length);
        var lastWasSpace = false;

        foreach (var ch in lowered)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue; // strip combining diacritics
            }

            if (ch is '\'' or '’' or 'ʼ' or '`' or '´')
            {
                continue; // drop apostrophes so "journey's" folds to "journeys", not "journey s"
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace && builder.Length > 0)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Normalized provider key: trimmed lowercase.</summary>
    public static string NormalizeProvider(string? provider) => (provider ?? "").Trim().ToLowerInvariant();

    /// <summary>Canonical external id form: trimmed (providers are otherwise case/format sensitive).</summary>
    public static string NormalizeExternalId(string? externalId) => (externalId ?? "").Trim();
}
