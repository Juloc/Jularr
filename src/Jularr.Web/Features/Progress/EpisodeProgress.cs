using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Playback;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Progress;

/// <summary>
/// Canonical per-profile playback state of one episode: the resume position
/// (<see cref="PositionMs"/>) and the watched flag (<see cref="IsCompleted"/>).
/// </summary>
public sealed class EpisodeProgress
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProfileId { get; set; } = "";
    public Guid EpisodeId { get; set; }
    public long PositionMs { get; set; }
    public long? DurationMs { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One bounded, user-owned playback session entry. History is personal recall
/// only; it never drives watched state, resume or Continue Watching.
/// </summary>
public sealed class EpisodePlaybackHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProfileId { get; set; } = "";
    public Guid EpisodeId { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastPlayedAt { get; set; } = DateTime.UtcNow;
    public long PositionMs { get; set; }
    public long? DurationMs { get; set; }
    public bool ReachedEnd { get; set; }
}

/// <summary>
/// Canonical profile-scoped playback preferences shared by every client:
/// user intent that follows the profile across devices. Device capability
/// facts (playback mode, quality cap) are deliberately not stored here.
/// </summary>
public sealed class ProfilePlaybackPreferences
{
    public string ProfileId { get; set; } = "";
    public bool AutoplayNext { get; set; }

    /// <summary>Normalized language tag (see <see cref="PlaybackLanguages"/>); null means file default.</summary>
    public string? PreferredAudioLanguage { get; set; }

    /// <summary>Normalized language tag, <see cref="PlaybackLanguages.SubtitlesOff"/>, or null for the default.</summary>
    public string? PreferredSubtitleLanguage { get; set; }

    public double DefaultPlaybackSpeed { get; set; } = PlaybackPreferenceRules.DefaultSpeed;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Product rules for playback preference values, shared by every client contract.</summary>
public static class PlaybackPreferenceRules
{
    public const double DefaultSpeed = 1.0;

    /// <summary>Practical playback speeds (0.5x–2.0x) owned by design/player/player-tokens.json.</summary>
    public static IReadOnlyList<double> Speeds => PlayerDesign.PlaybackSpeeds;

    public static string SpeedList =>
        string.Join(", ", Speeds.Select(x => x.ToString(CultureInfo.InvariantCulture)));

    public static bool IsAllowedSpeed(double speed) =>
        double.IsFinite(speed) && Speeds.Any(allowed => Math.Abs(allowed - speed) < 0.0001);

    public static double NormalizeSpeed(double speed)
    {
        if (!IsAllowedSpeed(speed))
        {
            throw new ArgumentOutOfRangeException(
                nameof(speed),
                $"Playback speed must be one of {SpeedList}.");
        }

        return Speeds.First(allowed => Math.Abs(allowed - speed) < 0.0001);
    }

    /// <summary>Empty clears the preference (file default); otherwise a valid language tag.</summary>
    public static string? NormalizeAudioLanguage(string value)
    {
        var normalized = NormalizeLanguage(value, "audioLanguage");
        return normalized == PlaybackLanguages.SubtitlesOff
            ? throw new ArgumentOutOfRangeException("audioLanguage", "Audio cannot be turned off.")
            : normalized;
    }

    /// <summary>Empty clears the preference; <c>off</c> or a valid language tag otherwise.</summary>
    public static string? NormalizeSubtitleLanguage(string value) =>
        NormalizeLanguage(value, "subtitleLanguage");

    private static string? NormalizeLanguage(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return PlaybackLanguages.Normalize(value) ?? throw new ArgumentOutOfRangeException(
            parameter,
            "Language must be an ISO 639 language tag such as ja, en or de.");
    }
}

public sealed record EpisodeProgressSnapshot(
    Guid EpisodeId,
    long PositionMs,
    long? DurationMs,
    bool IsCompleted,
    DateTime? UpdatedAt)
{
    public int Percent =>
        IsCompleted
            ? 100
            : DurationMs is > 0
                ? Math.Clamp((int)Math.Round(PositionMs * 100d / DurationMs.Value), 0, 100)
                : 0;

    public long ResumePositionMs =>
        PositionMs >= EpisodeProgressService.MinimumResumeMs
            ? PositionMs
            : 0;
}

public sealed record EpisodeProgressUpdate(
    long PositionMs,
    long? DurationMs,
    bool Completed);

public sealed record EpisodeReference(
    Guid Id,
    int SeasonNumber,
    int Number,
    string Title);

public sealed record EpisodeFlowSnapshot(
    Guid EpisodeId,
    Guid AnimeId,
    EpisodeReference? Previous,
    EpisodeReference? Next,
    PlaybackPreferencesSnapshot Preferences)
{
    public bool AutoplayNext => Preferences.AutoplayNext;
}

public enum ContinueWatchingKind
{
    Resume,
    UpNext
}

public sealed record ContinueWatchingItem(
    ContinueWatchingKind Kind,
    Guid EpisodeId,
    Guid AnimeId,
    string AnimeTitle,
    int SeasonNumber,
    int EpisodeNumber,
    string EpisodeTitle,
    long ResumePositionMs,
    long? DurationMs,
    DateTime UpdatedAt,
    string? CoverImageUrl)
{
    public int Percent =>
        DurationMs is > 0
            ? Math.Clamp((int)Math.Round(ResumePositionMs * 100d / DurationMs.Value), 0, 100)
            : 0;

    public long? RemainingMs =>
        DurationMs is > 0
            ? Math.Max(0, DurationMs.Value - ResumePositionMs)
            : null;
}

public sealed record PlaybackHistoryItem(
    Guid Id,
    Guid EpisodeId,
    Guid AnimeId,
    string AnimeTitle,
    int SeasonNumber,
    int EpisodeNumber,
    string EpisodeTitle,
    DateTime StartedAt,
    DateTime LastPlayedAt,
    long PositionMs,
    long? DurationMs,
    bool ReachedEnd);

public sealed record PlaybackPreferencesSnapshot(
    bool AutoplayNext,
    string? PreferredAudioLanguage = null,
    string? PreferredSubtitleLanguage = null,
    double DefaultPlaybackSpeed = PlaybackPreferenceRules.DefaultSpeed)
{
    public static PlaybackPreferencesSnapshot Default { get; } = new(false);
}

/// <summary>
/// Partial preference update: null keeps the stored value. An empty language
/// string clears that preference back to the file default.
/// </summary>
public sealed record PlaybackPreferencesUpdate(
    bool? AutoplayNext = null,
    string? PreferredAudioLanguage = null,
    string? PreferredSubtitleLanguage = null,
    double? DefaultPlaybackSpeed = null);

/// <summary>
/// Owner of all canonical episode playback facts for the current profile:
/// resume position, watched state, previous/next resolution, Continue
/// Watching, bounded personal history and playback preferences.
/// </summary>
public sealed class EpisodeProgressService(
    AppDbContext db,
    CurrentAccountContext currentAccount,
    VideoProgressService? videoProgress = null,
    CanonicalVideoTargetResolver? canonicalTargets = null)
{
    /// <summary>Playback at or beyond this share of the duration marks the episode watched.</summary>
    public const double CompletionThreshold = VideoProgressService.CompletionThreshold;

    /// <summary>Positions below this are accidental starts: never resumed and never create state.</summary>
    public const long MinimumResumeMs = VideoProgressService.MinimumResumeMs;

    /// <summary>Maximum number of personal history entries kept per profile.</summary>
    public const int HistoryLimit = VideoProgressService.HistoryLimit;

    public const int ContinueWatchingLimit = 12;

    /// <summary>Checkpoints of the same episode within this gap extend one history entry.</summary>
    public static readonly TimeSpan HistorySessionGap = VideoProgressService.HistorySessionGap;

    private const int ContinueWatchingCandidateLimit = 500;

    public static string FormatPosition(long positionMs)
    {
        var position = TimeSpan.FromMilliseconds(Math.Max(0, positionMs));
        return position.TotalHours >= 1
            ? position.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : position.ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<EpisodeProgressSnapshot?> GetAsync(
        Guid episodeId,
        CancellationToken cancellationToken = default)
    {
        if (videoProgress is not null && canonicalTargets is not null)
        {
            var target = await canonicalTargets.ResolveLegacyEpisodeAsync(episodeId, cancellationToken);
            if (target is null)
            {
                return null;
            }

            var canonical = await videoProgress.GetAsync(
                currentAccount.ProfileId,
                target,
                cancellationToken);
            return canonical is null
                ? null
                : new EpisodeProgressSnapshot(
                    episodeId,
                    canonical.PositionMs,
                    canonical.DurationMs,
                    canonical.IsCompleted,
                    canonical.UpdatedAt);
        }

        var row = await (
            from episode in db.Episodes.AsNoTracking()
            join progressValue in db.EpisodeProgress
                    .AsNoTracking()
                    .Where(x => x.ProfileId == currentAccount.ProfileId)
                on episode.Id equals progressValue.EpisodeId into progressRows
            from progress in progressRows.DefaultIfEmpty()
            where episode.Id == episodeId
            select new
            {
                episode.Id,
                PositionMs = progress == null ? 0L : progress.PositionMs,
                DurationMs = progress == null ? null : progress.DurationMs,
                IsCompleted = progress != null && progress.IsCompleted,
                UpdatedAt = progress == null ? (DateTime?)null : progress.UpdatedAt
            })
            .SingleOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new EpisodeProgressSnapshot(
                row.Id,
                row.PositionMs,
                row.DurationMs,
                row.IsCompleted,
                row.UpdatedAt);
    }

    public async Task<IReadOnlyDictionary<Guid, EpisodeProgressSnapshot>> GetForAnimeAsync(
        Guid animeId,
        CancellationToken cancellationToken = default)
    {
        var rows = await (
            from progress in db.EpisodeProgress.AsNoTracking()
            join episode in db.Episodes.AsNoTracking()
                on progress.EpisodeId equals episode.Id
            where progress.ProfileId == currentAccount.ProfileId &&
                  episode.AnimeId == animeId
            select progress)
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(x => x.EpisodeId, ToSnapshot);
    }

    /// <summary>
    /// Stores a playback checkpoint. Reaching the completion threshold marks the
    /// episode watched and clears the resume position. Watched state is sticky:
    /// later partial checkpoints (for example a rewatch) update the resume
    /// position but never flip the episode back to unwatched; only
    /// <see cref="SetWatchedAsync"/> can do that. A first checkpoint below
    /// <see cref="MinimumResumeMs"/> is an accidental start and is not persisted.
    /// </summary>
    public async Task<EpisodeProgressSnapshot?> UpdateAsync(
        Guid episodeId,
        EpisodeProgressUpdate update,
        CancellationToken cancellationToken = default)
    {
        MediaProgressSnapshot? canonicalSnapshot = null;
        if (videoProgress is not null && canonicalTargets is not null)
        {
            var target = await canonicalTargets.ResolveLegacyEpisodeAsync(episodeId, cancellationToken);
            if (target is null)
            {
                return null;
            }

            canonicalSnapshot = await videoProgress.UpdateAsync(
                currentAccount.ProfileId,
                target,
                new MediaProgressUpdate(update.PositionMs, update.DurationMs, update.Completed),
                cancellationToken);
            if (canonicalSnapshot is null)
            {
                return null;
            }
        }

        var episodeExists = await db.Episodes
            .AsNoTracking()
            .AnyAsync(x => x.Id == episodeId, cancellationToken);
        if (!episodeExists)
        {
            return null;
        }

        var positionMs = Math.Max(0, update.PositionMs);
        long? durationMs = update.DurationMs is > 0
            ? update.DurationMs
            : null;

        if (durationMs is { } duration)
        {
            positionMs = Math.Min(positionMs, duration);
        }

        var progress = await db.EpisodeProgress
            .SingleOrDefaultAsync(
                x => x.ProfileId == currentAccount.ProfileId &&
                     x.EpisodeId == episodeId,
                cancellationToken);

        var finalDurationMs = durationMs ?? progress?.DurationMs;
        var reachedEnd = update.Completed ||
            (finalDurationMs is { } knownDuration &&
             positionMs >= knownDuration * CompletionThreshold);

        if (progress is null && !reachedEnd && positionMs < MinimumResumeMs)
        {
            return new EpisodeProgressSnapshot(
                episodeId,
                positionMs,
                durationMs,
                false,
                null);
        }

        var now = DateTime.UtcNow;

        if (progress is null)
        {
            progress = new EpisodeProgress
            {
                ProfileId = currentAccount.ProfileId,
                EpisodeId = episodeId
            };
            db.EpisodeProgress.Add(progress);
        }

        progress.DurationMs = finalDurationMs;
        progress.PositionMs = reachedEnd ? 0 : positionMs;
        progress.IsCompleted = progress.IsCompleted || reachedEnd;
        progress.UpdatedAt = now;

        var trimHistory = await RecordHistoryAsync(
            episodeId,
            reachedEnd ? finalDurationMs ?? positionMs : positionMs,
            finalDurationMs,
            reachedEnd,
            now,
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        if (trimHistory)
        {
            await TrimHistoryAsync(cancellationToken);
        }

        return canonicalSnapshot is null
            ? ToSnapshot(progress)
            : new EpisodeProgressSnapshot(
                episodeId,
                canonicalSnapshot.PositionMs,
                canonicalSnapshot.DurationMs,
                canonicalSnapshot.IsCompleted,
                canonicalSnapshot.UpdatedAt);
    }

    /// <summary>
    /// Explicit Mark watched / Mark unwatched. Both clear the resume position so
    /// the next playback starts from the beginning. Manual actions are state,
    /// not playback, and therefore do not add history entries.
    /// </summary>
    public async Task<EpisodeProgressSnapshot?> SetWatchedAsync(
        Guid episodeId,
        bool watched,
        CancellationToken cancellationToken = default)
    {
        var episodeExists = await db.Episodes
            .AsNoTracking()
            .AnyAsync(x => x.Id == episodeId, cancellationToken);
        if (!episodeExists)
        {
            return null;
        }

        MediaProgressSnapshot? canonicalSnapshot = null;
        if (videoProgress is not null && canonicalTargets is not null)
        {
            var target = await canonicalTargets.ResolveLegacyEpisodeAsync(episodeId, cancellationToken);
            if (target is null)
            {
                return null;
            }

            canonicalSnapshot = await videoProgress.SetCompletedAsync(
                currentAccount.ProfileId,
                target,
                watched,
                cancellationToken);
            if (canonicalSnapshot is null)
            {
                return null;
            }
        }

        var progress = await db.EpisodeProgress
            .SingleOrDefaultAsync(
                x => x.ProfileId == currentAccount.ProfileId &&
                     x.EpisodeId == episodeId,
                cancellationToken);

        if (progress is null)
        {
            if (!watched)
            {
                return new EpisodeProgressSnapshot(episodeId, 0, null, false, null);
            }

            progress = new EpisodeProgress
            {
                ProfileId = currentAccount.ProfileId,
                EpisodeId = episodeId
            };
            db.EpisodeProgress.Add(progress);
        }

        progress.IsCompleted = watched;
        progress.PositionMs = 0;
        progress.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return canonicalSnapshot is null
            ? ToSnapshot(progress)
            : new EpisodeProgressSnapshot(
                episodeId,
                canonicalSnapshot.PositionMs,
                canonicalSnapshot.DurationMs,
                canonicalSnapshot.IsCompleted,
                canonicalSnapshot.UpdatedAt);
    }

    /// <summary>
    /// Canonical previous/next local episode flow for the Episode page, the web
    /// player and the native client API. See <see cref="EpisodeSequence"/>.
    /// </summary>
    public async Task<EpisodeFlowSnapshot?> GetFlowAsync(
        Guid episodeId,
        CancellationToken cancellationToken = default)
    {
        var animeId = await db.Episodes
            .AsNoTracking()
            .Where(x => x.Id == episodeId)
            .Select(x => (Guid?)x.AnimeId)
            .SingleOrDefaultAsync(cancellationToken);

        if (animeId is null)
        {
            return null;
        }

        var episodes = await LoadLocalEpisodesAsync(
            [animeId.Value],
            episodeId,
            cancellationToken);
        var neighbors = EpisodeSequence.Resolve(
            [.. episodes.Select(x => x.Key)],
            episodeId);

        Guid[] neighborIds =
        [
            .. new[] { neighbors.PreviousEpisodeId, neighbors.NextEpisodeId }
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
        ];

        List<EpisodeReference> references = neighborIds.Length == 0
            ? []
            : await db.Episodes
                .AsNoTracking()
                .Where(x => neighborIds.Contains(x.Id))
                .Select(x => new EpisodeReference(
                    x.Id,
                    x.SeasonNumber,
                    x.Number,
                    x.Title))
                .ToListAsync(cancellationToken);

        var preferences = await GetPreferencesAsync(cancellationToken);

        return new EpisodeFlowSnapshot(
            episodeId,
            animeId.Value,
            references.SingleOrDefault(x => x.Id == neighbors.PreviousEpisodeId),
            references.SingleOrDefault(x => x.Id == neighbors.NextEpisodeId),
            preferences);
    }

    /// <summary>
    /// Continue Watching: at most one item per anime, anchored on that anime's
    /// most recently updated meaningful progress row (resumable or watched).
    /// An unfinished anchor resumes that episode; a watched anchor surfaces the
    /// canonical next local episode when it is not watched yet. Items are
    /// ordered by anchor update time (newest first) with the episode id as a
    /// stable tie-break.
    /// </summary>
    public async Task<IReadOnlyList<ContinueWatchingItem>> GetContinueWatchingAsync(
        int limit = ContinueWatchingLimit,
        CancellationToken cancellationToken = default)
    {
        var candidates = await (
            from progress in db.EpisodeProgress.AsNoTracking()
            join episode in db.Episodes.AsNoTracking()
                on progress.EpisodeId equals episode.Id
            where progress.ProfileId == currentAccount.ProfileId &&
                  (progress.IsCompleted || progress.PositionMs >= MinimumResumeMs) &&
                  db.MediaFiles.Any(media => media.EpisodeId == episode.Id)
            orderby progress.UpdatedAt descending
            select new
            {
                progress.EpisodeId,
                episode.AnimeId,
                progress.PositionMs,
                progress.DurationMs,
                progress.IsCompleted,
                progress.UpdatedAt
            })
            .Take(ContinueWatchingCandidateLimit)
            .ToListAsync(cancellationToken);

        var anchors = candidates
            .OrderByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.EpisodeId.ToString("D"), StringComparer.Ordinal)
            .GroupBy(x => x.AnimeId)
            .Select(group => group.First())
            .ToArray();

        var completedAnimeIds = anchors
            .Where(x => x.IsCompleted)
            .Select(x => x.AnimeId)
            .ToArray();

        IReadOnlyList<LocalEpisodeRow> localEpisodes = completedAnimeIds.Length == 0
            ? []
            : await LoadLocalEpisodesAsync(completedAnimeIds, null, cancellationToken);
        var localEpisodesByAnime = localEpisodes
            .GroupBy(x => x.AnimeId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyCollection<EpisodeOrderKey>)group
                    .Select(x => x.Key)
                    .ToArray());

        var selections = new List<(ContinueWatchingKind Kind, Guid EpisodeId, DateTime AnchorUpdatedAt)>();
        var nextEpisodeIds = new List<Guid>();

        foreach (var anchor in anchors)
        {
            if (!anchor.IsCompleted)
            {
                selections.Add((ContinueWatchingKind.Resume, anchor.EpisodeId, anchor.UpdatedAt));
                continue;
            }

            if (!localEpisodesByAnime.TryGetValue(anchor.AnimeId, out var animeEpisodes))
            {
                continue;
            }

            var next = EpisodeSequence.Resolve(animeEpisodes, anchor.EpisodeId).NextEpisodeId;
            if (next is { } nextId)
            {
                selections.Add((ContinueWatchingKind.UpNext, nextId, anchor.UpdatedAt));
                nextEpisodeIds.Add(nextId);
            }
        }

        var nextProgress = nextEpisodeIds.Count == 0
            ? new Dictionary<Guid, EpisodeProgress>()
            : await db.EpisodeProgress
                .AsNoTracking()
                .Where(x =>
                    x.ProfileId == currentAccount.ProfileId &&
                    nextEpisodeIds.Contains(x.EpisodeId))
                .ToDictionaryAsync(x => x.EpisodeId, cancellationToken);

        var progressByEpisode = candidates.ToDictionary(x => x.EpisodeId);
        var selected = selections
            .Where(x =>
                x.Kind == ContinueWatchingKind.Resume ||
                !nextProgress.TryGetValue(x.EpisodeId, out var progress) ||
                !progress.IsCompleted)
            .Take(limit)
            .ToArray();

        if (selected.Length == 0)
        {
            return [];
        }

        var selectedIds = selected.Select(x => x.EpisodeId).ToArray();
        var details = await (
            from episode in db.Episodes.AsNoTracking()
            join anime in db.Anime.AsNoTracking() on episode.AnimeId equals anime.Id
            join metadataValue in db.AnimeMetadata.AsNoTracking()
                on anime.Id equals metadataValue.AnimeId into metadataRows
            from metadata in metadataRows.DefaultIfEmpty()
            where selectedIds.Contains(episode.Id)
            select new
            {
                episode.Id,
                episode.AnimeId,
                AnimeTitle = metadata == null ? anime.Title : metadata.PreferredTitle,
                episode.SeasonNumber,
                episode.Number,
                episode.Title,
                CoverImageUrl = metadata == null ? null : metadata.CoverImageUrl
            })
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        return selected
            .Where(x => details.ContainsKey(x.EpisodeId))
            .Select(x =>
            {
                var detail = details[x.EpisodeId];
                long resumeMs = 0;
                long? durationMs = null;

                if (x.Kind == ContinueWatchingKind.Resume)
                {
                    var progress = progressByEpisode[x.EpisodeId];
                    resumeMs = progress.PositionMs;
                    durationMs = progress.DurationMs;
                }
                else if (nextProgress.TryGetValue(x.EpisodeId, out var next))
                {
                    resumeMs = next.PositionMs >= MinimumResumeMs ? next.PositionMs : 0;
                    durationMs = next.DurationMs;
                }

                return new ContinueWatchingItem(
                    x.Kind,
                    detail.Id,
                    detail.AnimeId,
                    detail.AnimeTitle,
                    detail.SeasonNumber,
                    detail.Number,
                    detail.Title,
                    resumeMs,
                    durationMs,
                    x.AnchorUpdatedAt,
                    AnimeArtworkStore.ResolveSeasonPosterUrl(
                        detail.AnimeId,
                        detail.SeasonNumber,
                        detail.CoverImageUrl));
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<PlaybackHistoryItem>> GetHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await (
            from entry in db.EpisodePlaybackHistory.AsNoTracking()
            join episode in db.Episodes.AsNoTracking() on entry.EpisodeId equals episode.Id
            join anime in db.Anime.AsNoTracking() on episode.AnimeId equals anime.Id
            join metadataValue in db.AnimeMetadata.AsNoTracking()
                on anime.Id equals metadataValue.AnimeId into metadataRows
            from metadata in metadataRows.DefaultIfEmpty()
            where entry.ProfileId == currentAccount.ProfileId
            orderby entry.LastPlayedAt descending
            select new PlaybackHistoryItem(
                entry.Id,
                episode.Id,
                anime.Id,
                metadata == null ? anime.Title : metadata.PreferredTitle,
                episode.SeasonNumber,
                episode.Number,
                episode.Title,
                entry.StartedAt,
                entry.LastPlayedAt,
                entry.PositionMs,
                entry.DurationMs,
                entry.ReachedEnd))
            .Take(HistoryLimit)
            .ToListAsync(cancellationToken);

        return rows
            .OrderByDescending(x => x.LastPlayedAt)
            .ThenBy(x => x.Id.ToString("D"), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Clears only the current profile's history; watched state and resume positions stay.</summary>
    public Task<int> ClearHistoryAsync(CancellationToken cancellationToken = default) =>
        db.EpisodePlaybackHistory
            .Where(x => x.ProfileId == currentAccount.ProfileId)
            .ExecuteDeleteAsync(cancellationToken);

    public async Task<PlaybackPreferencesSnapshot> GetPreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        var preferences = await db.ProfilePlaybackPreferences
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.ProfileId == currentAccount.ProfileId,
                cancellationToken);

        return preferences is null
            ? PlaybackPreferencesSnapshot.Default
            : ToSnapshot(preferences);
    }

    /// <summary>
    /// Applies a partial update to the profile's playback preferences. Values
    /// are normalized through <see cref="PlaybackLanguages"/> and
    /// <see cref="PlaybackPreferenceRules"/>; an unsupported speed or language
    /// throws <see cref="ArgumentOutOfRangeException"/> before anything is stored.
    /// </summary>
    public async Task<PlaybackPreferencesSnapshot> UpdatePreferencesAsync(
        PlaybackPreferencesUpdate update,
        CancellationToken cancellationToken = default)
    {
        var speed = update.DefaultPlaybackSpeed is { } requestedSpeed
            ? PlaybackPreferenceRules.NormalizeSpeed(requestedSpeed)
            : (double?)null;
        var audioLanguage = update.PreferredAudioLanguage is { } requestedAudio
            ? PlaybackPreferenceRules.NormalizeAudioLanguage(requestedAudio)
            : null;
        var subtitleLanguage = update.PreferredSubtitleLanguage is { } requestedSubtitle
            ? PlaybackPreferenceRules.NormalizeSubtitleLanguage(requestedSubtitle)
            : null;

        var preferences = await db.ProfilePlaybackPreferences
            .SingleOrDefaultAsync(
                x => x.ProfileId == currentAccount.ProfileId,
                cancellationToken);

        if (preferences is null)
        {
            preferences = new ProfilePlaybackPreferences
            {
                ProfileId = currentAccount.ProfileId
            };
            db.ProfilePlaybackPreferences.Add(preferences);
        }

        if (update.AutoplayNext is { } autoplayNext)
        {
            preferences.AutoplayNext = autoplayNext;
        }

        if (update.PreferredAudioLanguage is not null)
        {
            preferences.PreferredAudioLanguage = audioLanguage;
        }

        if (update.PreferredSubtitleLanguage is not null)
        {
            preferences.PreferredSubtitleLanguage = subtitleLanguage;
        }

        if (speed is { } normalizedSpeed)
        {
            preferences.DefaultPlaybackSpeed = normalizedSpeed;
        }

        preferences.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return ToSnapshot(preferences);
    }

    private static PlaybackPreferencesSnapshot ToSnapshot(ProfilePlaybackPreferences preferences) =>
        new(
            preferences.AutoplayNext,
            preferences.PreferredAudioLanguage,
            preferences.PreferredSubtitleLanguage,
            preferences.DefaultPlaybackSpeed);

    private async Task<bool> RecordHistoryAsync(
        Guid episodeId,
        long positionMs,
        long? durationMs,
        bool reachedEnd,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var latest = await db.EpisodePlaybackHistory
            .Where(x => x.ProfileId == currentAccount.ProfileId)
            .OrderByDescending(x => x.LastPlayedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is not null &&
            latest.EpisodeId == episodeId &&
            now - latest.LastPlayedAt <= HistorySessionGap)
        {
            latest.LastPlayedAt = now;
            latest.PositionMs = positionMs;
            latest.DurationMs = durationMs ?? latest.DurationMs;
            latest.ReachedEnd = latest.ReachedEnd || reachedEnd;
            return false;
        }

        db.EpisodePlaybackHistory.Add(new EpisodePlaybackHistoryEntry
        {
            ProfileId = currentAccount.ProfileId,
            EpisodeId = episodeId,
            StartedAt = now,
            LastPlayedAt = now,
            PositionMs = positionMs,
            DurationMs = durationMs,
            ReachedEnd = reachedEnd
        });
        return true;
    }

    private async Task TrimHistoryAsync(CancellationToken cancellationToken)
    {
        var staleIds = await db.EpisodePlaybackHistory
            .AsNoTracking()
            .Where(x => x.ProfileId == currentAccount.ProfileId)
            .OrderByDescending(x => x.LastPlayedAt)
            .ThenByDescending(x => x.StartedAt)
            .Skip(HistoryLimit)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        if (staleIds.Count == 0)
        {
            return;
        }

        await db.EpisodePlaybackHistory
            .Where(x => staleIds.Contains(x.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<LocalEpisodeRow>> LoadLocalEpisodesAsync(
        IReadOnlyCollection<Guid> animeIds,
        Guid? includeEpisodeId,
        CancellationToken cancellationToken) =>
        await db.Episodes
            .AsNoTracking()
            .Where(x =>
                animeIds.Contains(x.AnimeId) &&
                (x.Id == includeEpisodeId ||
                 db.MediaFiles.Any(media => media.EpisodeId == x.Id)))
            .Select(x => new LocalEpisodeRow(
                x.Id,
                x.AnimeId,
                x.SeasonNumber,
                x.Number))
            .ToListAsync(cancellationToken);

    private static EpisodeProgressSnapshot ToSnapshot(EpisodeProgress progress) =>
        new(
            progress.EpisodeId,
            progress.PositionMs,
            progress.DurationMs,
            progress.IsCompleted,
            progress.UpdatedAt);

    private sealed record LocalEpisodeRow(
        Guid Id,
        Guid AnimeId,
        int SeasonNumber,
        int Number)
    {
        public EpisodeOrderKey Key => new(Id, SeasonNumber, Number);
    }
}
