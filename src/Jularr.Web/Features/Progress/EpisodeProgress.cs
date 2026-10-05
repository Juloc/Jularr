using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Playback;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Progress;

/// <summary>
/// Legacy per-profile episode progress row. Canonical progress lives in <c>MediaProgress</c> (see
/// <see cref="VideoProgressService"/>); this table is only the source of the one-time
/// <see cref="CanonicalVideoProgressBackfillService"/> and has no runtime writer or reader.
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
/// Legacy playback history row; like <see cref="EpisodeProgress"/> it only feeds the one-time canonical backfill.
/// Canonical history is <c>MediaPlaybackHistory</c>.
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
/// Legacy-identity adapter for pages and clients that still route by <c>Episode.Id</c>. It owns no progress state:
/// every read and write goes through the canonical <see cref="VideoProgressService"/> (<c>MediaProgress</c>,
/// <c>MediaPlaybackHistory</c>) for the current profile, so there is exactly one runtime writer. It also owns the
/// profile playback preferences and the previous/next local episode flow.
/// </summary>
public sealed class EpisodeProgressService(
    AppDbContext db,
    CurrentAccountContext currentAccount,
    VideoProgressService videoProgress,
    CanonicalVideoTargetResolver canonicalTargets)
{
    /// <summary>Positions below this are accidental starts: never resumed and never create state.</summary>
    public const long MinimumResumeMs = VideoProgressService.MinimumResumeMs;

    /// <summary>Maximum number of personal history entries kept per profile.</summary>
    public const int HistoryLimit = VideoProgressService.HistoryLimit;

    public const int ContinueWatchingLimit = VideoProgressService.ContinueWatchingLimit;

    public static string FormatPosition(long positionMs)
    {
        var position = TimeSpan.FromMilliseconds(Math.Max(0, positionMs));
        return position.TotalHours >= 1
            ? position.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : position.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    public async Task<EpisodeProgressSnapshot?> GetAsync(
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

        var rows = await videoProgress.GetLegacyEpisodeProgressAsync(currentAccount.ProfileId, [animeId.Value], cancellationToken);
        var row = rows.SingleOrDefault(x => x.EpisodeId == episodeId);
        return row is null
            ? new EpisodeProgressSnapshot(episodeId, 0, null, false, null)
            : ToSnapshot(row);
    }

    public async Task<IReadOnlyDictionary<Guid, EpisodeProgressSnapshot>> GetForAnimesAsync(
        IReadOnlyCollection<Guid> animeIds,
        CancellationToken cancellationToken = default)
    {
        var rows = await videoProgress.GetLegacyEpisodeProgressAsync(currentAccount.ProfileId, animeIds, cancellationToken);
        return rows
            .GroupBy(x => x.EpisodeId)
            .ToDictionary(group => group.Key, group => ToSnapshot(group.OrderByDescending(x => x.UpdatedAt).First()));
    }

    /// <summary>
    /// Stores a playback checkpoint through <see cref="VideoProgressService.UpdateAsync"/>; see
    /// <see cref="MediaProgressUpdate"/> for the completion contract.
    /// </summary>
    public async Task<EpisodeProgressSnapshot?> UpdateAsync(
        Guid episodeId,
        EpisodeProgressUpdate update,
        CancellationToken cancellationToken = default)
    {
        var target = await canonicalTargets.ResolveLegacyEpisodeAsync(episodeId, cancellationToken);
        if (target is null)
        {
            return null;
        }

        var snapshot = await videoProgress.UpdateAsync(
            currentAccount.ProfileId,
            target,
            new MediaProgressUpdate(update.PositionMs, update.DurationMs, update.Completed),
            cancellationToken);
        return snapshot is null
            ? null
            : ToSnapshot(episodeId, snapshot);
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
        var target = await canonicalTargets.ResolveLegacyEpisodeAsync(episodeId, cancellationToken);
        if (target is null)
        {
            return null;
        }

        var snapshot = await videoProgress.SetCompletedAsync(currentAccount.ProfileId, target, watched, cancellationToken);
        return snapshot is null
            ? null
            : ToSnapshot(episodeId, snapshot);
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
    /// Anime view of the shared <see cref="VideoProgressService.GetContinueWatchingAsync"/> projection, addressed by
    /// legacy episode identity and decorated with the preferred title and cover.
    /// </summary>
    public async Task<IReadOnlyList<ContinueWatchingItem>> GetContinueWatchingAsync(
        int limit = ContinueWatchingLimit,
        CancellationToken cancellationToken = default)
    {
        var items = await videoProgress.GetContinueWatchingAsync(currentAccount.ProfileId, limit, WorkMediaType.Anime, cancellationToken);
        var identities = await videoProgress.GetLegacyEpisodeIdentitiesAsync([.. items.Where(x => x.WorkEpisodeId.HasValue).Select(x => x.WorkEpisodeId!.Value)], cancellationToken);
        var details = await LoadEpisodeDetailsAsync([.. identities.Values.Select(x => x.EpisodeId)], cancellationToken);

        var result = new List<ContinueWatchingItem>(items.Count);
        foreach (var item in items)
        {
            if (item.WorkEpisodeId is not { } workEpisodeId ||
                !identities.TryGetValue(workEpisodeId, out var identity) ||
                !details.TryGetValue(identity.EpisodeId, out var detail))
            {
                continue;
            }

            var kind = item.Kind == VideoContinueWatchingKind.Resume
                ? ContinueWatchingKind.Resume
                : ContinueWatchingKind.UpNext;
            var resumeMs = item.ResumePositionMs >= MinimumResumeMs ? item.ResumePositionMs : 0;
            result.Add(new ContinueWatchingItem(
                kind,
                identity.EpisodeId,
                identity.AnimeId,
                detail.AnimeTitle,
                detail.SeasonNumber,
                detail.Number,
                detail.Title,
                resumeMs,
                item.DurationMs,
                item.UpdatedAt,
                AnimeArtworkStore.ResolveSeasonPosterUrl(identity.AnimeId, detail.SeasonNumber, detail.CoverImageUrl)));
        }

        return result;
    }

    /// <summary>Anime view of the canonical <c>MediaPlaybackHistory</c>, addressed by legacy episode identity.</summary>
    public async Task<IReadOnlyList<PlaybackHistoryItem>> GetHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = (await videoProgress.GetHistoryAsync(currentAccount.ProfileId, cancellationToken))
            .Where(x => x.WorkEpisodeId.HasValue && x.MediaType == WorkMediaType.Anime)
            .ToArray();
        var identities = await videoProgress.GetLegacyEpisodeIdentitiesAsync([.. entries.Select(x => x.WorkEpisodeId!.Value)], cancellationToken);
        var details = await LoadEpisodeDetailsAsync([.. identities.Values.Select(x => x.EpisodeId)], cancellationToken);

        var result = new List<PlaybackHistoryItem>(entries.Length);
        foreach (var entry in entries)
        {
            if (!identities.TryGetValue(entry.WorkEpisodeId!.Value, out var identity) ||
                !details.TryGetValue(identity.EpisodeId, out var detail))
            {
                continue;
            }

            result.Add(new PlaybackHistoryItem(
                entry.Id,
                identity.EpisodeId,
                identity.AnimeId,
                detail.AnimeTitle,
                detail.SeasonNumber,
                detail.Number,
                detail.Title,
                entry.StartedAt,
                entry.LastPlayedAt,
                entry.PositionMs,
                entry.DurationMs,
                entry.ReachedEnd));
        }

        return result;
    }

    /// <summary>Clears only the current profile's history; watched state and resume positions stay.</summary>
    public Task<int> ClearHistoryAsync(CancellationToken cancellationToken = default) =>
        videoProgress.ClearHistoryAsync(currentAccount.ProfileId, cancellationToken);

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

    private async Task<IReadOnlyDictionary<Guid, EpisodeDetail>> LoadEpisodeDetailsAsync(
        IReadOnlyCollection<Guid> episodeIds,
        CancellationToken cancellationToken)
    {
        if (episodeIds.Count == 0)
        {
            return new Dictionary<Guid, EpisodeDetail>();
        }

        return await (
            from episode in db.Episodes.AsNoTracking()
            join anime in db.Anime.AsNoTracking() on episode.AnimeId equals anime.Id
            join metadataValue in db.AnimeMetadata.AsNoTracking()
                on anime.Id equals metadataValue.AnimeId into metadataRows
            from metadata in metadataRows.DefaultIfEmpty()
            where episodeIds.Contains(episode.Id)
            select new EpisodeDetail(
                episode.Id,
                metadata == null ? anime.Title : metadata.PreferredTitle,
                episode.SeasonNumber,
                episode.Number,
                episode.Title,
                metadata == null ? null : metadata.CoverImageUrl))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
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

    private static EpisodeProgressSnapshot ToSnapshot(LegacyEpisodeProgress row) =>
        new(row.EpisodeId, row.PositionMs, row.DurationMs, row.IsCompleted, row.UpdatedAt);

    private static EpisodeProgressSnapshot ToSnapshot(Guid episodeId, MediaProgressSnapshot snapshot) =>
        new(episodeId, snapshot.PositionMs, snapshot.DurationMs, snapshot.IsCompleted, snapshot.UpdatedAt);

    private sealed record EpisodeDetail(
        Guid Id,
        string AnimeTitle,
        int SeasonNumber,
        int Number,
        string Title,
        string? CoverImageUrl);

    private sealed record LocalEpisodeRow(
        Guid Id,
        Guid AnimeId,
        int SeasonNumber,
        int Number)
    {
        public EpisodeOrderKey Key => new(Id, SeasonNumber, Number);
    }
}
