using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;

namespace Jularr.Web.Features.Acquisition.Pipeline;

/// <summary>The wanted episodes of the anime a release covers, and the wanted episode the search was for.</summary>
public sealed record AnimeMatch(IReadOnlyList<AnimeEpisodeKey> Covered, AnimeWantedEpisode? WantedEpisode);

// What is Anime-specific about judging a release: does it carry this anime (title and aliases), which wanted episodes does it cover (season, absolute numbers and
// packs, through the AniList mapping of each slot), and may it be grabbed at all (blocklist, Sonarr ownership). The shared core searches and its one selection
// ranks; this only supplies the facts.
public static class AnimeReleaseJudge
{
    private const int MaxSearchAliases = 3;

    public static MediaSearchPlan<AnimeMatch> Plan(
        AnimeAcquisitionTarget target,
        IReadOnlyList<AnimeAcquisitionEpisode> scope,
        IReadOnlyList<AnimeWantedEpisode> wanted,
        AnimeEpisodeKey? primary,
        ProwlarrAnimeSearchTarget searchTarget,
        AcquisitionOwnershipSnapshot snapshot,
        DateTimeOffset now,
        Func<string, bool>? isBlocked = null)
    {
        var aliases = scope.SelectMany(episode => new[] { episode.SearchTitle }.Concat(episode.SearchAliases)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new MediaSearchPlan<AnimeMatch>(ToSearchIntent(searchTarget), release => Judge(release, aliases, wanted, primary, snapshot, now, isBlocked));
    }

    // The ranked releases as the grab decisions the pipeline and the interactive search show, with the reasons people already read.
    public static IReadOnlyList<AnimeSearchCandidate> ToCandidates(AnimeAcquisitionTarget target, IReadOnlyList<AnimeAcquisitionEpisode> scope, SearchEvaluation<AnimeMatch> search) =>
        [.. search.Releases
            .Select(evaluation =>
            {
                var release = evaluation.Candidate;
                // The score shown is the profile's own, so a release that only waits for a later fallback tier still reads as accepted.
                var score = AnimeReleaseScorer.Score(target.Profile, new AnimeReleaseCandidate(release.ParsedRelease, release.SizeBytes, release.Indexer, release.Identity));
                return new AnimeSearchCandidate(release, score, Decide(target, scope, evaluation, score), evaluation.Match.Covered);
            })
            .OrderByDescending(candidate => candidate.Decision.Grab)];

    private static ReleaseJudgement<AnimeMatch> Judge(
        AcquisitionCandidate release,
        IReadOnlyList<string> aliases,
        IReadOnlyList<AnimeWantedEpisode> wanted,
        AnimeEpisodeKey? primary,
        AcquisitionOwnershipSnapshot snapshot,
        DateTimeOffset now,
        Func<string, bool>? isBlocked)
    {
        var parsed = release.ParsedRelease;
        var covered = wanted.Where(item => Covers(parsed, item.Key)).Select(item => item.Key).ToArray();
        var wantedEpisode = primary is not null
            ? wanted.FirstOrDefault(item => item.Key == primary)
            : covered.Length > 0 ? wanted.First(item => item.Key == covered[0]) : null;

        ReleaseIdentityEvidence identity;
        if (!AnimeImportPlanner.SeriesMatches(aliases, parsed.SeriesTitle))
        {
            identity = ReleaseIdentityEvidence.Conflict("TitleDoesNotMatch", $"Series title '{parsed.SeriesTitle}' does not match this anime.");
        }
        else if (covered.Length == 0 || wantedEpisode is null || (primary is not null && !covered.Contains(primary)))
        {
            identity = ReleaseIdentityEvidence.Conflict("EpisodeNotCovered", "Release does not cover the requested episode.");
        }
        else
        {
            // Season and episode numbers are the release's own statement; an absolute number only agrees through the AniList mapping.
            identity = parsed.SeasonNumber is not null && parsed.EpisodeStart is not null
                ? ReleaseIdentityEvidence.Exact("EpisodeCovered", "The release names the wanted season and episode.")
                : ReleaseIdentityEvidence.Strong("AbsoluteEpisodeCovered", "The release's absolute episode number covers the wanted episode.");
        }

        string? safety = null;
        if (release.InternalDownloadUri is null || !string.Equals(release.Protocol, "usenet", StringComparison.OrdinalIgnoreCase))
        {
            safety = "Not a usenet release with an NZB link; only SABnzbd downloads are supported.";
        }
        else if (isBlocked?.Invoke(release.Identity) == true)
        {
            safety = "The release was blocklisted after a failed download.";
        }
        else if (identity.Confidence != IdentityConfidence.Conflict)
        {
            var key = wantedEpisode!.Key;
            var ownership = SonarrParallelSafety.CanGrab(
                snapshot,
                new AcquisitionGrabRequest(
                    key.AnimeKey,
                    parsed.ReleaseKey,
                    parsed.SeasonNumber ?? key.SeasonNumber,
                    parsed.EpisodeStart ?? key.EpisodeNumber,
                    parsed.EpisodeEnd ?? key.EpisodeNumber,
                    parsed.AbsoluteEpisodeStart ?? key.AbsoluteEpisodeNumber,
                    parsed.AbsoluteEpisodeEnd ?? key.AbsoluteEpisodeNumber),
                now);
            safety = ownership.Allowed ? null : $"Ownership: {ownership.Reason}";
        }

        var span = parsed.EpisodeStart is { } first && parsed.EpisodeEnd is { } last
            ? last - first + 1
            : parsed.AbsoluteEpisodeStart is { } absoluteFirst && parsed.AbsoluteEpisodeEnd is { } absoluteLast ? absoluteLast - absoluteFirst + 1 : 1;
        return new ReleaseJudgement<AnimeMatch>(
            new AnimeMatch(covered, wantedEpisode),
            parsed,
            identity,
            new SelectionCoverage(covered.Length, Math.Max(wanted.Count, covered.Length), Math.Max(0, span - covered.Length)),
            safety);
    }

    private static AnimeAutoGrabDecision Decide(AnimeAcquisitionTarget target, IReadOnlyList<AnimeAcquisitionEpisode> scope, ReleaseEvaluation<AnimeMatch> evaluation, AnimeReleaseScoreResult score)
    {
        var selection = evaluation.Selection;
        if (!selection.IsSelectable)
        {
            if (selection.Reasons.FirstOrDefault(reason => reason.Kind == SelectionReasonKind.Safety) is { } safety)
            {
                return new(false, safety.Detail, score);
            }

            if (selection.Candidate.Identity.Confidence == IdentityConfidence.Conflict)
            {
                return new(false, selection.Candidate.Identity.Detail, score);
            }

            return new(false, "Candidate is rejected by the assigned quality profile.", score);
        }

        var wanted = evaluation.Match.WantedEpisode!;
        if (wanted.Reason == AnimeWantedReason.Missing)
        {
            return new(true, "Accepted candidate satisfies a missing monitored unit.", score);
        }

        var current = scope.FirstOrDefault(item => item.Key == wanted.Key) is { } slot
            ? AnimeAcquisitionInventory.ToInventory(slot, target.Profile).CurrentFile
            : null;
        return current is null
            ? new(false, "Upgrade decision requires the current file score.", score)
            : AnimeReleaseScorer.IsUpgrade(target.Profile, current, score)
                ? new(true, "Accepted candidate is an upgrade over the current file.", score)
                : new(false, "Candidate is not an upgrade over the current file.", score);
    }

    private static bool Covers(AnimeReleaseInfo release, AnimeEpisodeKey key)
    {
        // A season pack names no episode range: it covers every wanted episode of its season.
        if (release.IsSeasonPack && release.EpisodeStart is null && release.SeasonNumber is { } packSeason)
        {
            return packSeason == key.SeasonNumber;
        }

        if (release.SeasonNumber is { } season && release.EpisodeStart is { } start && release.EpisodeEnd is { } end)
        {
            return season == key.SeasonNumber && key.EpisodeNumber >= start && key.EpisodeNumber <= end;
        }

        return key.AbsoluteEpisodeNumber is { } absolute &&
               release.AbsoluteEpisodeStart is { } absoluteStart &&
               release.AbsoluteEpisodeEnd is { } absoluteEnd &&
               absolute >= absoluteStart && absolute <= absoluteEnd;
    }

    /// <summary>
    /// What the shared Search Planner is asked for. A season of which every episode is wanted (and so none is in the library yet) is searched as
    /// the season, so the planner asks for packs deliberately; a season that is partly there or still airing is searched episode by episode.
    /// </summary>
    public static ProwlarrAnimeSearchTarget PlannedTargetFor(AnimeAcquisitionTarget target, AnimeAcquisitionEpisode episode, IReadOnlyList<AnimeWantedEpisode> allWanted)
    {
        var season = episode.Key.SeasonNumber;
        var inSeason = target.Episodes.Count(item => item.Key.SeasonNumber == season);
        var wantedInSeason = allWanted.Count(item => item.Key.SeasonNumber == season);
        return season > 0 && inSeason >= 2 && wantedInSeason == inSeason
            ? new ProwlarrAnimeSearchTarget(episode.SearchTitle, Aliases(episode.SearchAliases, episode.SearchTitle), ProwlarrAnimeSearchMode.Season, season, null, null)
            : SearchTargetFor(episode);
    }

    public static ProwlarrAnimeSearchTarget SearchTargetFor(AnimeAcquisitionEpisode episode) =>
        new(
            episode.SearchTitle,
            Aliases(episode.SearchAliases, episode.SearchTitle),
            ProwlarrAnimeSearchMode.Episode,
            episode.Key.SeasonNumber,
            episode.Key.EpisodeNumber,
            episode.Key.AbsoluteEpisodeNumber);

    public static string[] Aliases(IReadOnlyList<string> aliases, string canonical) =>
        aliases
            .Where(alias => !alias.Equals(canonical, StringComparison.OrdinalIgnoreCase))
            .Take(MaxSearchAliases)
            .ToArray();

    private static SearchIntent ToSearchIntent(ProwlarrAnimeSearchTarget target) =>
        new(MediaAcquisitionKind.Anime, target.CanonicalTitle)
        {
            Aliases = target.Aliases ?? [],
            Season = target.Mode == ProwlarrAnimeSearchMode.Anime ? null : target.SeasonNumber,
            Episode = target.Mode == ProwlarrAnimeSearchMode.Episode ? target.EpisodeNumber : null,
            AbsoluteEpisode = target.Mode == ProwlarrAnimeSearchMode.Episode ? target.AbsoluteEpisodeNumber : null
        };
}
