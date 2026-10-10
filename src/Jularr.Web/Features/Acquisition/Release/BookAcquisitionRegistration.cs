using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Quality;

namespace Jularr.Web.Features.Acquisition.Release;

/// <summary>
/// Book-specific release parsing on top of the shared scene parser. Books still reuse the common
/// title/language/group parsing, but expose document formats as the generic quality key.
/// </summary>
public sealed partial class BookReleaseParser : IReleaseParser
{
    public static BookReleaseParser Instance { get; } = new();

    public ReleaseInfo Parse(string releaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseName);

        var parsed = SceneReleaseParser.Instance.Parse(releaseName);
        return parsed with
        {
            DocumentFormat = DetectFormat(releaseName)
        };
    }

    public bool TryParse(string? releaseName, out ReleaseInfo release)
    {
        release = default!;
        if (string.IsNullOrWhiteSpace(releaseName))
        {
            return false;
        }

        release = Parse(releaseName);
        return true;
    }

    public static string? DetectFormat(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var match = DocumentFormatRegex().Match(value);
        return match.Success
            ? match.Groups["format"].Value.ToUpperInvariant()
            : null;
    }

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?<format>EPUB|PDF|MOBI|AZW3?|DJVU|CBZ|CBR)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DocumentFormatRegex();
}

/// <summary>
/// Seed Book profile for the shared acquisition engine. EPUB is preferred, PDF is accepted, and
/// an unlabelled release remains a last-resort candidate because the completed-download importer
/// validates the actual file before accepting it.
/// </summary>
public static class BookQualityProfiles
{
    public const string DefaultBookId = "book-epub";

    public static QualityProfile CreateDefaultBook() =>
        new(
            DefaultBookId,
            "Books",
            [
                "EPUB",
                "PDF",
                "UNKNOWN-UNKNOWN"
            ],
            [
                "EPUB",
                "PDF",
                "UNKNOWN-UNKNOWN"
            ],
            UpgradeAllowed: true,
            UpgradeCutoffQuality: "EPUB",
            MinimumSizeBytes: null,
            MaximumSizeBytes: null,
            MustContain: [],
            MustNotContain:
            [
                "audiobook",
                "hörbuch"
            ],
            RequiredRegex: [],
            RejectedRegex: [],
            ScoreRules:
            [
                new(
                    "Prefer retail",
                    ReleaseRuleField.RawTitle,
                    ReleaseRuleMatch.Contains,
                    "retail",
                    2)
            ]);
}

/// <summary>
/// Books participate in the shared acquisition registry at whole-item granularity.
/// </summary>
public sealed class BookAcquisitionRegistration : IMediaAcquisitionRegistration
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Book;

    public IReleaseParser CreateReleaseParser() => BookReleaseParser.Instance;

    public QualityProfile CreateDefaultQualityProfile() => BookQualityProfiles.CreateDefaultBook();

    public MonitoringGranularity MonitoringGranularity => MonitoringGranularity.Item;
}
