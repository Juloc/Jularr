using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;

namespace Jularr.Web.Features.ReadingAcquisition;

/// <summary>
/// Release parsing of Manga and Light Novel releases for the shared acquisition engine: the shared scene parser for the common facts and the
/// reading format (<c>CBZ</c>, <c>EPUB</c>, <c>ZIP</c>, ...) as the quality key, so format preference is an ordinary, editable quality order.
/// Volume, chapter and language stay in <see cref="ReadingReleaseParser"/>, which the identity judgement of the reading selector reads.
/// </summary>
public sealed class ReadingReleaseEvidenceParser : IReleaseParser
{
    public static ReadingReleaseEvidenceParser Instance { get; } = new();

    public ReleaseInfo Parse(string releaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseName);

        return SceneReleaseParser.Instance.Parse(releaseName) with { DocumentFormat = QualityOf(ReadingReleaseParser.Parse(releaseName).Format) };
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

    public static string QualityOf(ReadingReleaseFormat format) =>
        format switch
        {
            ReadingReleaseFormat.Epub => "EPUB",
            ReadingReleaseFormat.Pdf => "PDF",
            ReadingReleaseFormat.Cbz => "CBZ",
            ReadingReleaseFormat.Cbr => "CBR",
            ReadingReleaseFormat.Zip => "ZIP",
            _ => "UNKNOWN-UNKNOWN"
        };
}

/// <summary>
/// Seed profiles of the reading types: the formats their importers read, best first. A release without a declared format is the last tier
/// because the importer validates the actual files; formats the importer cannot read never reach the profile (they are unsupported, not "low").
/// </summary>
public static class ReadingQualityProfiles
{
    public const string DefaultMangaId = "manga-cbz";
    public const string DefaultLightNovelId = "lightnovel-epub";

    public static QualityProfile CreateDefaultManga() => Create(DefaultMangaId, "Manga", "CBZ");

    public static QualityProfile CreateDefaultLightNovel() => Create(DefaultLightNovelId, "Light Novels", "EPUB");

    public static QualityProfile For(MediaAcquisitionKind kind) => kind == MediaAcquisitionKind.LightNovel ? CreateDefaultLightNovel() : CreateDefaultManga();

    private static QualityProfile Create(string id, string name, string preferred) =>
        new(
            id,
            name,
            [preferred, "ZIP", "UNKNOWN-UNKNOWN"],
            [preferred, "ZIP", "UNKNOWN-UNKNOWN"],
            UpgradeAllowed: false,
            UpgradeCutoffQuality: null,
            MinimumScore: 0,
            MinimumSizeBytes: null,
            MaximumSizeBytes: null,
            MustContain: [],
            MustNotContain: [],
            RequiredRegex: [],
            RejectedRegex: [],
            ScoreRules: []);
}

/// <summary>Manga participates in the shared acquisition registry at whole-item granularity: a request is one volume, a chapter range or the series.</summary>
public sealed class MangaAcquisitionRegistration : IMediaAcquisitionRegistration
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Manga;

    public IReleaseParser CreateReleaseParser() => ReadingReleaseEvidenceParser.Instance;

    public QualityProfile CreateDefaultQualityProfile() => ReadingQualityProfiles.CreateDefaultManga();

    public MonitoringGranularity MonitoringGranularity => MonitoringGranularity.Item;
}

public sealed class LightNovelAcquisitionRegistration : IMediaAcquisitionRegistration
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.LightNovel;

    public IReleaseParser CreateReleaseParser() => ReadingReleaseEvidenceParser.Instance;

    public QualityProfile CreateDefaultQualityProfile() => ReadingQualityProfiles.CreateDefaultLightNovel();

    public MonitoringGranularity MonitoringGranularity => MonitoringGranularity.Item;
}
