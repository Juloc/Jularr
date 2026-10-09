using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Library;

namespace Jularr.Web.Features.Acquisition.Selection;

public enum IncomingVideoVerdict
{
    /// <summary>The target has no installed file: the incoming one is placed as usual.</summary>
    NothingInstalled,

    /// <summary>The incoming file is a meaningful upgrade of every installed version of the target.</summary>
    Upgrade,

    /// <summary>A comparable installed version is at least as good; it stays and the incoming file is not placed over it.</summary>
    ExistingPreferred,

    /// <summary>Either quality cannot be compared safely, so nothing is replaced.</summary>
    Undecidable
}

/// <param name="Superseded">The stored files an <see cref="IncomingVideoVerdict.Upgrade"/> replaces.</param>
public sealed record IncomingVideoJudgement(IncomingVideoVerdict Verdict, IReadOnlyList<InstalledVideoFile> Superseded);

/// <summary>
/// The installed side of the upgrade policy for Movie and TV: what quality each target has, read from the canonical Version/Asset/File chain
/// (the quality the importer recorded, else the one the file name still carries). The importers ask it whether a file they are about to place
/// is an upgrade, and the Wanted pass asks which installed targets are not final. It stores nothing.
/// </summary>
public sealed class InstalledVideoVersions(CanonicalMediaStorageService storage, MediaAcquisitionRegistry registry, QualityProfileStore profiles)
{
    /// <summary>The quality key a release or file name states, or null when it states none that can be compared.</summary>
    public string? QualityOfName(MediaAcquisitionKind kind, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !registry.ParserFor(kind).TryParse(name, out var release))
        {
            return null;
        }

        var key = ReleaseQuality.GetKey(release);
        return key.Equals("UNKNOWN-UNKNOWN", StringComparison.OrdinalIgnoreCase) ? null : key;
    }

    /// <summary>The quality of a downloaded video: its file name, else the folder the release came in.</summary>
    public string? QualityOfDownload(MediaAcquisitionKind kind, string videoPath) =>
        QualityOfName(kind, Path.GetFileNameWithoutExtension(videoPath)) ?? QualityOfName(kind, Path.GetFileName(Path.GetDirectoryName(videoPath)) ?? "");

    /// <summary>Every video file of the Work with its installed quality; episodes are told apart by <see cref="InstalledVideoFile.WorkEpisodeId"/>.</summary>
    public async Task<IReadOnlyList<InstalledVideoFile>> FilesAsync(MediaAcquisitionKind kind, Guid workId, CancellationToken cancellationToken) =>
        [.. (await storage.ListVideoFilesAsync(workId, cancellationToken)).Select(file => file with { Quality = file.Quality ?? QualityOfName(kind, Path.GetFileNameWithoutExtension(file.Path)) })];

    /// <summary>The best comparable installed quality of every target of the Work (a Movie is the null episode); a target without an installed file is absent.</summary>
    public async Task<IReadOnlyDictionary<Guid, string?>> BestQualityByEpisodeAsync(MediaAcquisitionKind kind, Guid workId, QualityProfile profile, CancellationToken cancellationToken) =>
        (await FilesAsync(kind, workId, cancellationToken))
            .Where(file => file.WorkEpisodeId is not null)
            .GroupBy(file => file.WorkEpisodeId!.Value)
            .ToDictionary(group => group.Key, group => UpgradePolicy.Best(profile, group.Select(file => file.Quality)));

    public async Task<string?> BestMovieQualityAsync(Guid workId, QualityProfile profile, CancellationToken cancellationToken) =>
        UpgradePolicy.Best(profile, (await FilesAsync(MediaAcquisitionKind.Movie, workId, cancellationToken)).Where(file => file.WorkEpisodeId is null).Select(file => file.Quality));

    /// <summary>Decides what an incoming file means for the target it was downloaded for. The profile is the one the Work is acquired with.</summary>
    public async Task<IncomingVideoJudgement> JudgeIncomingAsync(MediaAcquisitionKind kind, Guid workId, Guid? workEpisodeId, string? incomingQuality, CancellationToken cancellationToken)
    {
        var installed = (await FilesAsync(kind, workId, cancellationToken)).Where(file => file.WorkEpisodeId == workEpisodeId).ToArray();
        if (installed.Length == 0)
        {
            return new IncomingVideoJudgement(IncomingVideoVerdict.NothingInstalled, []);
        }

        var profile = await profiles.ResolveAsync(kind, workId, cancellationToken);
        var best = UpgradePolicy.Best(profile, installed.Select(file => file.Quality));
        if (UpgradePolicy.IsUpgrade(profile, best, incomingQuality))
        {
            return new IncomingVideoJudgement(IncomingVideoVerdict.Upgrade, installed);
        }

        return new IncomingVideoJudgement(best is null || UpgradePolicy.RankOf(profile, incomingQuality) == int.MaxValue ? IncomingVideoVerdict.Undecidable : IncomingVideoVerdict.ExistingPreferred, []);
    }
}
