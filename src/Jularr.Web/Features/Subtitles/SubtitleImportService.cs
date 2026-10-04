using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jularr.Web.Data;
using Jularr.Web.Features.Learning.Courses;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Vocabulary;
using Jularr.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Subtitles;

public sealed record JimakuConnectionStatus(bool Configured, DateTimeOffset? UpdatedAt = null);

public sealed record JimakuKeyTestResult(bool Success, string Message);

public enum SubtitleSidecarImportStatus
{
    Imported,
    NotFound,
    Unavailable
}

public sealed record SubtitleSidecarImportResult(
    SubtitleSidecarImportStatus Status,
    string? Path = null);

public sealed class SubtitleImportService
{
    public const string JimakuSourcePrefix = "jimaku:";

    /// <summary>Source-key prefix for tracks imported via <see cref="ImportManualSearchResultAsync"/> (#526).</summary>
    public const string ProviderSourcePrefix = "provider:";

    // The canonical default when nothing else configures a content language, and the
    // only language Jimaku (a Japanese fansub site) or the local Whisper fallback
    // (a Japanese-only model invocation) ever serve.
    private const string JapaneseLanguageTag = "ja";

    // Namespaces the advisory-lock key of a track import (see LockSourceKeyAsync) away from any
    // other advisory lock the database might be asked to take.
    private const string SourceLockNamespace = "jularr:subtitle-track:";

    private const string JimakuStorePath = "/data/integrations/jimaku.json";
    private const string JimakuApiBase = "https://jimaku.cc/api/";
    private const int MaxJimakuDownloadBytes = 64 * 1024 * 1024;
    private const int MaxSubtitleBytes = SubtitleDownloadContent.MaxSubtitleBytes;

    private static readonly ConcurrentDictionary<Guid, LearningTextPreparationState>
        PreparationStates = new();

    private static readonly object PreparationStateLock = new();
    private static readonly SemaphoreSlim JimakuStoreGate = new(1, 1);
    private static int batchQueued;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly AppDbContext db;
    private readonly VocabularyService vocabularyService;
    private readonly EmbeddedSubtitleExtractor? embeddedSubtitleExtractor;
    private readonly AnimeMetadataService? animeMetadataService;
    private readonly BackgroundJobQueue? jobs;
    private readonly IHttpClientFactory? httpClientFactory;
    private readonly IDataProtector? jimakuProtector;
    private readonly LearningContentLanguageResolver contentLanguageResolver;
    private readonly ILogger<SubtitleImportService>? logger;
    private readonly IInstanceModuleService? instanceModules;

    public SubtitleImportService(
        AppDbContext db,
        VocabularyService vocabularyService,
        EmbeddedSubtitleExtractor? embeddedSubtitleExtractor = null,
        AnimeMetadataService? animeMetadataService = null,
        BackgroundJobQueue? jobs = null,
        IHttpClientFactory? httpClientFactory = null,
        IDataProtectionProvider? dataProtectionProvider = null,
        ILogger<SubtitleImportService>? logger = null,
        IInstanceModuleService? instanceModules = null)
    {
        this.db = db;
        this.vocabularyService = vocabularyService;
        this.embeddedSubtitleExtractor = embeddedSubtitleExtractor;
        this.animeMetadataService = animeMetadataService;
        this.jobs = jobs;
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;
        this.instanceModules = instanceModules;
        contentLanguageResolver = new LearningContentLanguageResolver(db);
        jimakuProtector = dataProtectionProvider?.CreateProtector(
            "AniLingo.Subtitles.Jimaku.ApiKey.v1");
    }

    // The first usable sidecar in preference order becomes the learning track. Sidecar tracks are
    // removed when no usable sidecar remains; unreadable locations leave the state untouched.
    public async Task<SubtitleSidecarImportResult> ImportPreferredSidecarAsync(
        Guid episodeId,
        IReadOnlyList<string> mediaPaths,
        SubtitleSidecarDirectoryCache listings,
        CancellationToken cancellationToken)
    {
        if (!await IsLearningModuleEnabledAsync(cancellationToken))
        {
            // Unavailable means "leave current state untouched"; a disabled Learning module must
            // not make a library scan delete previously prepared subtitle/vocabulary state.
            return new SubtitleSidecarImportResult(SubtitleSidecarImportStatus.Unavailable);
        }

        var targetLanguage = await contentLanguageResolver.ResolveTargetLanguageAsync(cancellationToken);

        try
        {
            foreach (var candidate in SubtitleSidecarLocator.FindCandidates(mediaPaths, listings, targetLanguage))
            {
                if (await TryImportSidecarAsync(episodeId, candidate, targetLanguage, cancellationToken))
                {
                    return new SubtitleSidecarImportResult(
                        SubtitleSidecarImportStatus.Imported,
                        candidate.Path);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(
                exception,
                "Subtitle sidecars for episode {EpisodeId} could not be read; keeping the current learning text.",
                episodeId);
            return new SubtitleSidecarImportResult(SubtitleSidecarImportStatus.Unavailable);
        }

        await RemoveSidecarTracksAsync(episodeId, targetLanguage, cancellationToken);
        return new SubtitleSidecarImportResult(SubtitleSidecarImportStatus.NotFound);
    }

    public async Task ImportPreferredContentAsync(
        Guid episodeId,
        string sourceKey,
        string format,
        DateTime sourceUpdatedAt,
        string content,
        CancellationToken cancellationToken)
    {
        if (!await IsLearningModuleEnabledAsync(cancellationToken))
        {
            return;
        }

        var normalizedFormat = format.Trim().TrimStart('.').ToLowerInvariant();
        var cues = SubtitleParser.ParseFormat(normalizedFormat, content);
        if (cues.Count == 0)
        {
            return;
        }

        var targetLanguage = await contentLanguageResolver.ResolveTargetLanguageAsync(cancellationToken);
        await ImportCuesAsync(
            episodeId,
            sourceKey,
            normalizedFormat,
            sourceUpdatedAt,
            cues,
            targetLanguage,
            cancellationToken);
    }

    /// <summary>
    /// Imports a subtitle chosen from a manual provider search (#526) as its own
    /// (language, forced, SDH) track, independent of the single "current learning source" track
    /// <see cref="ImportPreferredContentAsync"/> manages. Replaces any earlier track already
    /// imported for that exact (episode, language, forced, SDH) combination.
    /// </summary>
    public async Task<Guid> ImportManualSearchResultAsync(
        Guid episodeId,
        string providerId,
        string resultToken,
        string languageTag,
        bool forced,
        bool sdh,
        string format,
        DateTime sourceUpdatedAt,
        string content,
        CancellationToken cancellationToken)
    {
        var normalizedLanguage = languageTag.Trim().ToLowerInvariant();
        var normalizedFormat = format.Trim().TrimStart('.').ToLowerInvariant();
        var cues = SubtitleParser.ParseFormat(normalizedFormat, content);
        if (cues.Count == 0)
        {
            throw new InvalidDataException("The selected subtitle contained no usable cues.");
        }

        var sourceKey = BuildProviderSourceKey(providerId, resultToken);
        var targetLanguage = await contentLanguageResolver.ResolveTargetLanguageAsync(cancellationToken);

        // A normal (non-forced, non-SDH) track in the resolved learning language is also the
        // learning-text source: importing it rebuilds vocabulary the same way the automatic pipeline
        // would. Every other combination is an independent track.
        var slot = new TrackSlot(
            normalizedLanguage,
            forced,
            sdh,
            !forced && !sdh && normalizedLanguage.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase),
            "Using a manually searched subtitle.");

        // The same idempotent, per-Path serialized upsert the automatic sources use: importing a
        // provider result again (or for another episode) updates the one row that owns its Path.
        return await ImportCuesAsync(
            episodeId,
            sourceKey,
            normalizedFormat,
            sourceUpdatedAt,
            cues,
            targetLanguage,
            cancellationToken,
            slot);
    }

    private static string BuildProviderSourceKey(string providerId, string resultToken)
    {
        var fingerprint = $"{providerId}\n{resultToken}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint))).ToLowerInvariant();
        return $"{ProviderSourcePrefix}{providerId}:{hash}";
    }

    // The one path that creates or replaces a track by its source key (SubtitleTracks.Path is
    // unique). Looking the Path up, creating or updating the row, replacing its cues and rebuilding
    // the episode vocabulary run as one transaction that is serialized per source key (#601): a
    // scan, the background learning-text batch and a repair refresh can all reach the same sidecar
    // at once, and unserialized each would see no track and insert one (IX_SubtitleTracks_Path,
    // 23505) or replace the cues of an existing track twice and leave it with two copies. The
    // importer that waits on the lock re-reads the winner's committed row, so importing a source
    // that is already current degrades to a no-op instead of failing.
    private async Task<Guid> ImportCuesAsync(
        Guid episodeId,
        string sourceKey,
        string normalizedFormat,
        DateTime sourceUpdatedAt,
        IReadOnlyList<SubtitleCueData> cues,
        string targetLanguage,
        CancellationToken cancellationToken,
        TrackSlot? requestedSlot = null)
    {
        var slot = requestedSlot ?? new TrackSlot(targetLanguage, false, false, true);
        if (slot.IsLearningSource && !await IsLearningModuleEnabledAsync(cancellationToken))
        {
            // Manual subtitle management remains usable with Learning off, but must not rebuild
            // vocabulary, mark preparation ready or apply learning-source pruning semantics.
            slot = slot with { IsLearningSource = false };
        }

        sourceUpdatedAt = ToStoredPrecision(sourceUpdatedAt);
        string readyMessage;
        Guid trackId;
        Guid? previousEpisodeId = null;

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await LockSourceKeyAsync(sourceKey, cancellationToken);
            var current = await FindTrackStampAsync(sourceKey, cancellationToken);

            if (current is not null && IsCurrent(current, episodeId, sourceUpdatedAt))
            {
                trackId = current.Id;
                if (slot.IsLearningSource)
                {
                    await PruneOtherTracksAsync(episodeId, current.Id, targetLanguage, cancellationToken);
                }

                readyMessage = slot.ReadyMessage ?? "Learning text is ready.";
            }
            else
            {
                (trackId, previousEpisodeId) = await ReplaceTrackAsync(
                    current,
                    episodeId,
                    sourceKey,
                    normalizedFormat,
                    sourceUpdatedAt,
                    cues,
                    slot,
                    targetLanguage,
                    cancellationToken);
                readyMessage = slot.ReadyMessage ?? $"Learning text is ready ({cues.Count} cues).";
            }

            await transaction.CommitAsync(cancellationToken);
        }

        if (previousEpisodeId is { } previous)
        {
            ForgetReadyState(previous);
        }

        if (slot.IsLearningSource)
        {
            MarkPreparationReady(episodeId, DetectSourceKind(sourceKey), readyMessage);
        }

        return trackId;
    }

    // Upserts the track for the source key and swaps in the new cues. An existing row is matched by
    // Path and updated in place (episode, language, format, timestamps), so a source that moved to
    // another episode keeps its single row. Returns the track id and the episode the track left, if
    // it moved.
    private async Task<(Guid TrackId, Guid? PreviousEpisodeId)> ReplaceTrackAsync(
        TrackStamp? current,
        Guid episodeId,
        string sourceKey,
        string normalizedFormat,
        DateTime sourceUpdatedAt,
        IReadOnlyList<SubtitleCueData> cues,
        TrackSlot slot,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        Guid trackId;
        if (current is null)
        {
            var track = new SubtitleTrack
            {
                EpisodeId = episodeId,
                Path = sourceKey,
                Language = slot.Language,
                Forced = slot.Forced,
                Sdh = slot.Sdh,
                Format = normalizedFormat,
                SourceUpdatedAt = sourceUpdatedAt
            };
            db.SubtitleTracks.Add(track);
            trackId = track.Id;
        }
        else
        {
            trackId = current.Id;
            var importedAt = DateTime.UtcNow;
            await db.SubtitleTracks
                .Where(x => x.Id == trackId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.EpisodeId, episodeId)
                        .SetProperty(x => x.Language, slot.Language)
                        .SetProperty(x => x.Forced, slot.Forced)
                        .SetProperty(x => x.Sdh, slot.Sdh)
                        .SetProperty(x => x.Format, normalizedFormat)
                        .SetProperty(x => x.SourceUpdatedAt, sourceUpdatedAt)
                        .SetProperty(x => x.ImportedAt, importedAt),
                    cancellationToken);

            // ExecuteUpdate bypasses the change tracker; drop any copy this context still holds.
            foreach (var stale in db.ChangeTracker
                         .Entries<SubtitleTrack>()
                         .Where(x => x.Entity.Id == trackId)
                         .ToArray())
            {
                stale.State = EntityState.Detached;
            }

            await db.SubtitleCues
                .Where(x => x.SubtitleTrackId == trackId)
                .ExecuteDeleteAsync(cancellationToken);
        }

        // A newer result for the same (episode, language, forced, SDH) slot supersedes the older one;
        // the learning-text source additionally keeps just one normal track in the learning language.
        await db.SubtitleTracks
            .Where(x =>
                x.EpisodeId == episodeId &&
                x.Language == slot.Language &&
                x.Forced == slot.Forced &&
                x.Sdh == slot.Sdh &&
                x.Id != trackId)
            .ExecuteDeleteAsync(cancellationToken);
        if (slot.IsLearningSource)
        {
            await RemoveOtherTracksAsync(episodeId, trackId, targetLanguage, cancellationToken);
        }

        db.SubtitleCues.AddRange(cues.Select(cue => new SubtitleCue
        {
            SubtitleTrackId = trackId,
            StartMs = cue.StartMs,
            EndMs = cue.EndMs,
            Text = cue.Text
        }));

        await db.SaveChangesAsync(cancellationToken);

        // The episode the track left no longer owns its cues, so its vocabulary is rebuilt too.
        Guid? previousEpisodeId = current is not null && current.EpisodeId != episodeId
            ? current.EpisodeId
            : null;
        if (previousEpisodeId is { } previous)
        {
            await vocabularyService.RebuildEpisodeAsync(previous, cancellationToken);
        }

        if (slot.IsLearningSource)
        {
            await vocabularyService.RebuildEpisodeAsync(episodeId, cancellationToken);
        }

        return (trackId, previousEpisodeId);
    }

    // A transaction-scoped advisory lock on the source key: it needs no row (so it also orders two
    // importers of a Path that has no track yet), is shared by every connection to the database and
    // is released with the transaction. Distinct keys only ever serialize on a hash collision.
    private Task LockSourceKeyAsync(string sourceKey, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({SourceLockNamespace + sourceKey}, 0))",
            cancellationToken);

    private Task<TrackStamp?> FindTrackStampAsync(string path, CancellationToken cancellationToken) =>
        db.SubtitleTracks
            .AsNoTracking()
            .Where(x => x.Path == path)
            .Select(x => new TrackStamp(x.Id, x.EpisodeId, x.SourceUpdatedAt))
            .SingleOrDefaultAsync(cancellationToken);

    // The single definition of "this source was already imported for this episode": the same
    // timestamp on the same episode. A track whose episode differs is re-associated, not kept.
    private static bool IsCurrent(TrackStamp track, Guid episodeId, DateTime sourceUpdatedAt) =>
        track.EpisodeId == episodeId && track.SourceUpdatedAt == ToStoredPrecision(sourceUpdatedAt);

    // timestamptz keeps microseconds while file timestamps carry 100 ns ticks, so a stored stamp
    // only ever equals its file's after truncation; without it an unchanged source never compares
    // equal again and is re-imported (cues replaced, vocabulary rebuilt) on every scan and repair.
    private static DateTime ToStoredPrecision(DateTime value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));

    private async Task PruneOtherTracksAsync(
        Guid episodeId,
        Guid trackId,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var removed = await RemoveOtherTracksAsync(
            episodeId,
            trackId,
            targetLanguage,
            cancellationToken);

        if (removed > 0)
        {
            await vocabularyService.RebuildEpisodeAsync(episodeId, cancellationToken);
        }
    }

    private async Task KeepCurrentTrackAsync(
        Guid episodeId,
        Guid trackId,
        string sourceKey,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        await PruneOtherTracksAsync(episodeId, trackId, targetLanguage, cancellationToken);

        MarkPreparationReady(
            episodeId,
            DetectSourceKind(sourceKey),
            "Learning text is ready.");
    }

    private async Task<bool> TryImportSidecarAsync(
        Guid episodeId,
        SubtitleSidecarCandidate candidate,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(candidate.Path);
        if (!info.Exists || info.Length > MaxSubtitleBytes)
        {
            return false;
        }

        var sourceUpdatedAt = info.LastWriteTimeUtc;
        // Unlocked fast path so an unchanged sidecar is not even read; ImportCuesAsync re-checks
        // under the source-key lock, so a stale answer here can only cost a redundant parse.
        var current = await FindTrackStampAsync(candidate.Path, cancellationToken);

        // An unchanged sidecar was already validated when it was imported.
        if (current is not null && IsCurrent(current, episodeId, sourceUpdatedAt))
        {
            await KeepCurrentTrackAsync(episodeId, current.Id, candidate.Path, targetLanguage, cancellationToken);
            return true;
        }

        var content = await File.ReadAllTextAsync(candidate.Path, cancellationToken);
        var cues = SubtitleParser.ParseFormat(candidate.Format, content);
        if (cues.Count == 0)
        {
            return false;
        }

        // Japanese keeps its untagged-content kana heuristic; FindCandidates already
        // restricts every other language's untagged candidates to "no tagged file
        // exists", so an untagged candidate reaching here needs no further content gate.
        var isJapaneseTarget = targetLanguage.Equals(JapaneseLanguageTag, StringComparison.OrdinalIgnoreCase);
        if (!candidate.IsTargetLanguageTagged && isJapaneseTarget && !ContainsJapaneseDialogue(cues))
        {
            return false;
        }

        await ImportCuesAsync(
            episodeId,
            candidate.Path,
            candidate.Format,
            sourceUpdatedAt,
            cues,
            targetLanguage,
            cancellationToken);
        return true;
    }

    private async Task RemoveSidecarTracksAsync(
        Guid episodeId,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var removed = await db.SubtitleTracks
            .Where(x =>
                x.EpisodeId == episodeId &&
                x.Language == targetLanguage &&
                !x.Forced &&
                !x.Sdh &&
                !x.Path.StartsWith(EmbeddedSubtitleExtractor.SourcePrefix) &&
                !x.Path.StartsWith(EmbeddedSubtitleExtractor.TranscriptionSourcePrefix) &&
                !x.Path.StartsWith(JimakuSourcePrefix))
            .ExecuteDeleteAsync(cancellationToken);

        if (removed == 0)
        {
            return;
        }

        await vocabularyService.RebuildEpisodeAsync(episodeId, cancellationToken);
        ForgetReadyState(episodeId);
    }

    private static void ForgetReadyState(Guid episodeId)
    {
        if (PreparationStates.TryGetValue(episodeId, out var state) &&
            state.Status == LearningTextPreparationStatus.Ready)
        {
            PreparationStates.TryRemove(KeyValuePair.Create(episodeId, state));
        }
    }

    // Untagged sidecars are only trusted when at least a third of their cues contain kana,
    // which rejects English or Chinese files that happen to share the episode base name.
    private static bool ContainsJapaneseDialogue(IReadOnlyList<SubtitleCueData> cues)
    {
        var kanaCues = cues.Count(cue => cue.Text.Any(ch => ch is >= '぀' and <= 'ヿ'));
        return kanaCues * 3 >= cues.Count;
    }

    private async Task<bool> IsLearningModuleEnabledAsync(
        CancellationToken cancellationToken) =>
        instanceModules is null
        || await instanceModules.IsEnabledAsync(
            InstanceModule.Learning,
            cancellationToken);

    public LearningTextPreparationState GetPreparationState(Guid episodeId) =>
        PreparationStates.TryGetValue(episodeId, out var state)
            ? state
            : LearningTextPreparationState.Empty;

    public async Task<LearningTextCoverageSnapshot> GetCoverageAsync(
        CancellationToken cancellationToken)
    {
        var episodeIds = await db.MediaFiles
            .AsNoTracking()
            .Where(x => x.EpisodeId.HasValue)
            .Select(x => x.EpisodeId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (episodeIds.Count == 0)
        {
            return new LearningTextCoverageSnapshot(0, 0, 0, 0, 0, 0);
        }

        var targetLanguage = await contentLanguageResolver.ResolveTargetLanguageAsync(cancellationToken);
        var readyIds = await db.SubtitleTracks
            .AsNoTracking()
            .Where(x =>
                episodeIds.Contains(x.EpisodeId) &&
                x.Language == targetLanguage &&
                db.SubtitleCues.Any(cue => cue.SubtitleTrackId == x.Id))
            .Select(x => x.EpisodeId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var readySet = readyIds.ToHashSet();
        var queued = 0;
        var processing = 0;
        var failed = 0;

        foreach (var episodeId in episodeIds)
        {
            if (readySet.Contains(episodeId))
            {
                continue;
            }

            switch (GetPreparationState(episodeId).Status)
            {
                case LearningTextPreparationStatus.Queued:
                    queued++;
                    break;
                case LearningTextPreparationStatus.Processing:
                    processing++;
                    break;
                case LearningTextPreparationStatus.Failed:
                    failed++;
                    break;
            }
        }

        var missing = Math.Max(
            0,
            episodeIds.Count - readySet.Count - queued - processing - failed);

        return new LearningTextCoverageSnapshot(
            episodeIds.Count,
            readySet.Count,
            queued,
            processing,
            failed,
            missing);
    }

    public async Task<IReadOnlyList<LearningTextEpisodeStatus>> GetMissingEpisodesAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var boundedLimit = Math.Clamp(limit, 1, 500);
        var targetLanguage = await contentLanguageResolver.ResolveTargetLanguageAsync(cancellationToken);

        var rows = await (
            from episode in db.Episodes.AsNoTracking()
            join anime in db.Anime.AsNoTracking() on episode.AnimeId equals anime.Id
            where db.MediaFiles.Any(media => media.EpisodeId == episode.Id)
                && !db.SubtitleTracks.Any(track =>
                    track.EpisodeId == episode.Id &&
                    track.Language == targetLanguage &&
                    db.SubtitleCues.Any(cue => cue.SubtitleTrackId == track.Id))
            orderby anime.Title, episode.SeasonNumber, episode.Number
            select new
            {
                episode.Id,
                AnimeTitle = anime.Title,
                episode.SeasonNumber,
                episode.Number,
                episode.Title
            })
            .Take(boundedLimit)
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new LearningTextEpisodeStatus(
                row.Id,
                row.AnimeTitle,
                row.SeasonNumber,
                row.Number,
                row.Title,
                GetPreparationState(row.Id)))
            .ToArray();
    }

    public async Task<bool> QueueLearningTextAsync(
        Guid episodeId,
        CancellationToken cancellationToken)
    {
        if (!await IsLearningModuleEnabledAsync(cancellationToken))
        {
            PreparationStates.TryRemove(episodeId, out _);
            return false;
        }

        var targetLanguage = await contentLanguageResolver.ResolveTargetLanguageAsync(cancellationToken);
        if (await HasUsableTextAsync(episodeId, targetLanguage, cancellationToken))
        {
            MarkPreparationReady(
                episodeId,
                LearningTextSourceKind.Existing,
                "Learning text is already available.");
            return false;
        }

        lock (PreparationStateLock)
        {
            var state = GetPreparationState(episodeId);
            if (state.Status is LearningTextPreparationStatus.Queued
                or LearningTextPreparationStatus.Processing)
            {
                return false;
            }

            PreparationStates[episodeId] = new LearningTextPreparationState(
                LearningTextPreparationStatus.Queued,
                UpdatedAt: DateTimeOffset.UtcNow);
        }

        try
        {
            await RequireJobs().QueueAsync(
                new OperationDescriptor(
                    "learning-text-preparation",
                    "Subtitles",
                    "Prepare episode learning text",
                    $"Episode {episodeId:N}",
                    Lane: OperationLane.Normal,
                    IsDownload: true,
                    Retryable: true),
                async (operation, services, jobCancellationToken) =>
                {
                    await operation.ReportAsync(
                        5,
                        "Checking subtitle and transcript sources.",
                        cancellationToken: jobCancellationToken);

                    var importer = services.GetRequiredService<SubtitleImportService>();
                    var moduleService = services.GetRequiredService<IInstanceModuleService>();
                    if (!await moduleService.IsEnabledAsync(
                            InstanceModule.Learning,
                            jobCancellationToken))
                    {
                        await importer.PrepareLearningTextAsync(
                            episodeId,
                            jobCancellationToken);
                        await operation.ReportAsync(
                            100,
                            "Learning module is disabled; preparation skipped.",
                            cancellationToken: jobCancellationToken);
                        return;
                    }

                    await importer.PrepareLearningTextAsync(
                        episodeId,
                        jobCancellationToken);

                    await operation.ReportAsync(
                        100,
                        "Learning text is ready.",
                        cancellationToken: jobCancellationToken);
                },
                cancellationToken);
            return true;
        }
        catch
        {
            MarkPreparationFailed(
                episodeId,
                "Could not queue learning-text preparation.");
            throw;
        }
    }

    public async Task<int> QueueAllMissingAsync(CancellationToken cancellationToken)
    {
        if (!await IsLearningModuleEnabledAsync(cancellationToken))
        {
            return 0;
        }

        lock (PreparationStateLock)
        {
            if (batchQueued != 0)
            {
                return 0;
            }

            batchQueued = 1;
        }

        List<Guid> episodeIds;
        try
        {
            episodeIds = await GetMissingEpisodeIdsAsync(cancellationToken);
        }
        catch
        {
            Interlocked.Exchange(ref batchQueued, 0);
            throw;
        }

        if (episodeIds.Count == 0)
        {
            Interlocked.Exchange(ref batchQueued, 0);
            return 0;
        }

        foreach (var episodeId in episodeIds)
        {
            lock (PreparationStateLock)
            {
                var state = GetPreparationState(episodeId);
                if (state.Status is not LearningTextPreparationStatus.Processing)
                {
                    PreparationStates[episodeId] = new LearningTextPreparationState(
                        LearningTextPreparationStatus.Queued,
                        UpdatedAt: DateTimeOffset.UtcNow);
                }
            }
        }

        try
        {
            await RequireJobs().QueueAsync(
                new OperationDescriptor(
                    "learning-text-batch",
                    "Subtitles",
                    "Prepare missing learning text",
                    $"{episodeIds.Count} episode(s)",
                    Lane: OperationLane.Maintenance,
                    IsDownload: true,
                    Retryable: true),
                async (operation, services, jobCancellationToken) =>
                {
                    try
                    {
                        var importer =
                            services.GetRequiredService<SubtitleImportService>();
                        var moduleService =
                            services.GetRequiredService<IInstanceModuleService>();

                        if (!await moduleService.IsEnabledAsync(
                                InstanceModule.Learning,
                                jobCancellationToken))
                        {
                            await operation.ReportAsync(
                                100,
                                "Learning module is disabled; batch skipped.",
                                cancellationToken: jobCancellationToken);
                            return;
                        }

                        var pendingIds =
                            await importer.GetMissingEpisodeIdsAsync(jobCancellationToken);

                        if (pendingIds.Count == 0)
                        {
                            await operation.ReportAsync(
                                100,
                                "No missing learning text remains.",
                                cancellationToken: jobCancellationToken);
                            return;
                        }

                        for (var index = 0; index < pendingIds.Count; index++)
                        {
                            jobCancellationToken.ThrowIfCancellationRequested();

                            if (!await moduleService.IsEnabledAsync(
                                    InstanceModule.Learning,
                                    jobCancellationToken))
                            {
                                await operation.ReportAsync(
                                    100,
                                    "Learning module was disabled; remaining preparation skipped.",
                                    cancellationToken: jobCancellationToken);
                                return;
                            }

                            var episodeId = pendingIds[index];
                            var percent = Math.Clamp(
                                (int)Math.Round(index * 100d / pendingIds.Count),
                                0,
                                99);

                            await operation.ReportAsync(
                                percent,
                                $"Preparing episode {index + 1} of {pendingIds.Count}.",
                                cancellationToken: jobCancellationToken);

                            await importer.PrepareLearningTextAsync(
                                episodeId,
                                jobCancellationToken);
                        }

                        await operation.ReportAsync(
                            100,
                            $"Prepared {pendingIds.Count} episode(s).",
                            cancellationToken: jobCancellationToken);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref batchQueued, 0);
                    }
                },
                cancellationToken);
        }
        catch
        {
            Interlocked.Exchange(ref batchQueued, 0);
            throw;
        }

        return episodeIds.Count;
    }

    public async Task PrepareLearningTextAsync(
        Guid episodeId,
        CancellationToken cancellationToken)
    {
        if (!await IsLearningModuleEnabledAsync(cancellationToken))
        {
            PreparationStates.TryRemove(episodeId, out _);
            return;
        }

        PreparationStates[episodeId] = new LearningTextPreparationState(
            LearningTextPreparationStatus.Processing,
            Message: "Checking learning-text sources.",
            UpdatedAt: DateTimeOffset.UtcNow);

        try
        {
            var targetLanguage = await contentLanguageResolver.ResolveTargetLanguageAsync(cancellationToken);
            if (await HasUsableTextAsync(episodeId, targetLanguage, cancellationToken))
            {
                MarkPreparationReady(
                    episodeId,
                    LearningTextSourceKind.Existing,
                    "Learning text is already available.");
                return;
            }

            var media = await GetEpisodeMediaAsync(episodeId, cancellationToken);
            if (media is null || !File.Exists(media.MediaPath))
            {
                MarkPreparationFailed(episodeId, "Episode media file was not found.");
                return;
            }

            // Jimaku only indexes Japanese fansub releases and the local Whisper fallback
            // only ever decodes as Japanese, so both stay Japanese-only: neither is a
            // usable source once the resolved content language is something else.
            var isJapaneseTarget = targetLanguage.Equals(
                JapaneseLanguageTag,
                StringComparison.OrdinalIgnoreCase);
            var jimakuApiKey = await LoadJimakuApiKeyAsync(cancellationToken);
            if (!isJapaneseTarget && jimakuApiKey is not null)
            {
                logger?.LogInformation(
                    "Episode {EpisodeId} content language is {Language}; Jimaku (Japanese-only) and local Whisper transcription (Japanese-only model) are skipped for this stage.",
                    episodeId,
                    targetLanguage);
            }

            var stages = LearningTextFallbackPolicy.Build(
                !string.IsNullOrWhiteSpace(jimakuApiKey),
                isJapaneseTarget);

            foreach (var stage in stages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var imported = stage switch
                {
                    LearningTextFallbackStage.LocalSubtitle =>
                        await TryImportLocalSubtitleAsync(media, targetLanguage, cancellationToken),
                    LearningTextFallbackStage.EmbeddedSubtitle =>
                        await TryImportEmbeddedSubtitleAsync(media, targetLanguage, cancellationToken),
                    LearningTextFallbackStage.Jimaku when jimakuApiKey is not null && isJapaneseTarget =>
                        await TryImportJimakuAsync(
                            media,
                            jimakuApiKey,
                            cancellationToken),
                    LearningTextFallbackStage.Whisper when isJapaneseTarget =>
                        await TryImportWhisperAsync(media, cancellationToken),
                    _ => false
                };

                if (imported &&
                    await HasUsableTextAsync(episodeId, targetLanguage, cancellationToken))
                {
                    return;
                }
            }

            var audioState =
                RequireEmbeddedSubtitleExtractor().GetAudioTranscriptionState(media.MediaPath);
            MarkPreparationFailed(
                episodeId,
                audioState.Message ??
                "No usable subtitle or audio transcript could be produced.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger?.LogError(
                exception,
                "Learning-text preparation failed for episode {EpisodeId}.",
                episodeId);
            MarkPreparationFailed(
                episodeId,
                "Learning-text preparation failed unexpectedly.");
        }
    }

    public async Task<JimakuConnectionStatus> GetJimakuConnectionStatusAsync(
        CancellationToken cancellationToken)
    {
        var persisted = await LoadPersistedJimakuSettingsAsync(cancellationToken);
        if (persisted is null)
        {
            return new JimakuConnectionStatus(false);
        }

        try
        {
            var key = RequireJimakuProtector().Unprotect(persisted.ProtectedApiKey);
            return new JimakuConnectionStatus(
                !string.IsNullOrWhiteSpace(key),
                persisted.UpdatedAt);
        }
        catch (CryptographicException exception)
        {
            logger?.LogWarning(exception, "Could not decrypt the Jimaku API key.");
            return new JimakuConnectionStatus(false);
        }
    }

    public async Task<JimakuKeyTestResult> SaveJimakuApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        var normalized = apiKey.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new JimakuKeyTestResult(false, "Jimaku API key is required.");
        }

        var test = await TestJimakuApiKeyAsync(normalized, cancellationToken);
        if (!test.Success)
        {
            return test;
        }

        var directory = Path.GetDirectoryName(JimakuStorePath)
            ?? throw new InvalidOperationException("Jimaku settings path has no directory.");
        Directory.CreateDirectory(directory);

        var persisted = new PersistedJimakuSettings(
            RequireJimakuProtector().Protect(normalized),
            DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(persisted, JsonOptions);
        var temporaryPath = $"{JimakuStorePath}.tmp-{Guid.NewGuid():N}";

        await JimakuStoreGate.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            SetPrivateFileMode(temporaryPath);
            File.Move(temporaryPath, JimakuStorePath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
            JimakuStoreGate.Release();
        }

        return new JimakuKeyTestResult(true, "Jimaku connection saved.");
    }

    public async Task DisconnectJimakuAsync(CancellationToken cancellationToken)
    {
        await JimakuStoreGate.WaitAsync(cancellationToken);
        try
        {
            TryDelete(JimakuStorePath);
        }
        finally
        {
            JimakuStoreGate.Release();
        }
    }

    public async Task<JimakuKeyTestResult> TestJimakuApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendJimakuAsync(
                apiKey,
                "entries/search?query=Sousou%20no%20Frieren",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return new JimakuKeyTestResult(true, "Jimaku API key is valid.");
            }

            if ((int)response.StatusCode is 401 or 403)
            {
                return new JimakuKeyTestResult(false, "Jimaku rejected the API key.");
            }

            return new JimakuKeyTestResult(
                false,
                $"Jimaku returned HTTP {(int)response.StatusCode}.");
        }
        catch (HttpRequestException)
        {
            return new JimakuKeyTestResult(
                false,
                "Jimaku could not be reached.");
        }
    }

    private async Task<bool> TryImportLocalSubtitleAsync(
        EpisodeMediaSnapshot media,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var sidecar = await ImportPreferredSidecarAsync(
            media.EpisodeId,
            [media.MediaPath],
            new SubtitleSidecarDirectoryCache(),
            cancellationToken);

        if (sidecar.Status != SubtitleSidecarImportStatus.Imported ||
            !await HasUsableTextAsync(media.EpisodeId, targetLanguage, cancellationToken))
        {
            return false;
        }

        MarkPreparationReady(
            media.EpisodeId,
            LearningTextSourceKind.LocalSubtitle,
            $"Using local subtitle {Path.GetFileName(sidecar.Path)}.");
        return true;
    }

    private async Task<bool> TryImportEmbeddedSubtitleAsync(
        EpisodeMediaSnapshot media,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var embedded = await RequireEmbeddedSubtitleExtractor().ExtractPreferredTextAsync(
            media.MediaPath,
            targetLanguage,
            cancellationToken);

        if (embedded is null)
        {
            return false;
        }

        await ImportPreferredContentAsync(
            media.EpisodeId,
            embedded.SourceKey,
            embedded.Format,
            media.SourceUpdatedAt,
            embedded.Content,
            cancellationToken);

        if (!await HasUsableTextAsync(media.EpisodeId, targetLanguage, cancellationToken))
        {
            return false;
        }

        MarkPreparationReady(
            media.EpisodeId,
            LearningTextSourceKind.EmbeddedSubtitle,
            "Using embedded text subtitles.");
        return true;
    }

    private async Task<bool> TryImportJimakuAsync(
        EpisodeMediaSnapshot media,
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await RequireAnimeMetadataService().ResolveEpisodeAsync(
                media.EpisodeId,
                cancellationToken);

            var remoteEpisode = resolved?.RemoteEpisodeNumber ?? media.EpisodeNumber;
            var entries = await SearchJimakuEntriesAsync(
                apiKey,
                resolved,
                media.AnimeTitle,
                cancellationToken);

            foreach (var entry in entries.Take(5))
            {
                var filteredFiles = await GetJimakuFilesAsync(
                    apiKey,
                    entry.Id,
                    remoteEpisode,
                    cancellationToken);

                var candidate = JimakuSubtitleMatcher.SelectBest(
                    filteredFiles,
                    remoteEpisode,
                    episodeFiltered: true);

                if (candidate is null)
                {
                    var allFiles = await GetJimakuFilesAsync(
                        apiKey,
                        entry.Id,
                        episodeNumber: null,
                        cancellationToken);

                    candidate = JimakuSubtitleMatcher.SelectBest(
                        allFiles,
                        remoteEpisode,
                        episodeFiltered: false);
                }

                if (candidate is null)
                {
                    continue;
                }

                var downloaded = await DownloadJimakuSubtitleAsync(
                    candidate,
                    remoteEpisode,
                    cancellationToken);

                if (downloaded is null)
                {
                    continue;
                }

                var sourceKey = BuildJimakuSourceKey(
                    entry.Id,
                    candidate.Url,
                    downloaded.Value.FileName);

                await ImportPreferredContentAsync(
                    media.EpisodeId,
                    sourceKey,
                    downloaded.Value.Format,
                    candidate.LastModified?.UtcDateTime ?? DateTime.UnixEpoch,
                    downloaded.Value.Content,
                    cancellationToken);

                if (!await HasUsableTextAsync(
                        media.EpisodeId,
                        JapaneseLanguageTag,
                        cancellationToken))
                {
                    continue;
                }

                MarkPreparationReady(
                    media.EpisodeId,
                    LearningTextSourceKind.Jimaku,
                    $"Using Jimaku subtitle {downloaded.Value.FileName}.");
                return true;
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            JsonException or
            InvalidDataException or
            IOException)
        {
            logger?.LogWarning(
                exception,
                "Jimaku lookup failed for episode {EpisodeId}; falling back to Whisper.",
                media.EpisodeId);
        }

        return false;
    }

    private async Task<bool> TryImportWhisperAsync(
        EpisodeMediaSnapshot media,
        CancellationToken cancellationToken)
    {
        var transcript = await RequireEmbeddedSubtitleExtractor().TranscribeJapaneseAudioAsync(
            media.MediaPath,
            cancellationToken);

        if (transcript is null)
        {
            return false;
        }

        await ImportPreferredContentAsync(
            media.EpisodeId,
            transcript.SourceKey,
            transcript.Format,
            media.SourceUpdatedAt,
            transcript.Content,
            cancellationToken);

        if (!await HasUsableTextAsync(media.EpisodeId, JapaneseLanguageTag, cancellationToken))
        {
            return false;
        }

        RequireEmbeddedSubtitleExtractor().MarkAudioTranscriptionReady(media.MediaPath);
        MarkPreparationReady(
            media.EpisodeId,
            LearningTextSourceKind.Whisper,
            "Using local Whisper Japanese audio transcription.");
        return true;
    }

    private async Task<IReadOnlyList<JimakuEntry>> SearchJimakuEntriesAsync(
        string apiKey,
        ResolvedAnimeEpisodeMetadata? resolved,
        string animeTitle,
        CancellationToken cancellationToken)
    {
        string relativeUrl;

        if (resolved is not null &&
            resolved.Provider.Equals("anilist", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(resolved.ExternalId, out var anilistId))
        {
            relativeUrl = $"entries/search?anilist_id={anilistId}";
        }
        else
        {
            var query = Uri.EscapeDataString(
                resolved?.PreferredTitle ?? animeTitle);
            relativeUrl = $"entries/search?query={query}";
        }

        using var response = await SendJimakuAsync(
            apiKey,
            relativeUrl,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger?.LogWarning(
                "Jimaku entry search returned HTTP {StatusCode}.",
                (int)response.StatusCode);
            return [];
        }

        return await response.Content.ReadFromJsonAsync<List<JimakuEntry>>(
                   JsonOptions,
                   cancellationToken)
               ?? [];
    }

    private async Task<IReadOnlyList<JimakuSubtitleFileCandidate>> GetJimakuFilesAsync(
        string apiKey,
        long entryId,
        int? episodeNumber,
        CancellationToken cancellationToken)
    {
        var relativeUrl = episodeNumber is > 0
            ? $"entries/{entryId}/files?episode={episodeNumber.Value}"
            : $"entries/{entryId}/files";

        using var response = await SendJimakuAsync(
            apiKey,
            relativeUrl,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var files = await response.Content.ReadFromJsonAsync<List<JimakuFile>>(
                        JsonOptions,
                        cancellationToken)
                    ?? [];

        return files
            .Where(file =>
                !string.IsNullOrWhiteSpace(file.Name) &&
                !string.IsNullOrWhiteSpace(file.Url))
            .Select(file => new JimakuSubtitleFileCandidate(
                file.Name,
                file.Url,
                file.LastModified))
            .ToArray();
    }

    private async Task<(string FileName, string Format, string Content)?>
        DownloadJimakuSubtitleAsync(
            JimakuSubtitleFileCandidate candidate,
            int episodeNumber,
            CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !(uri.Host.Equals("jimaku.cc", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".jimaku.cc", StringComparison.OrdinalIgnoreCase)))
        {
            logger?.LogWarning(
                "Rejected unexpected Jimaku download host for {FileName}.",
                candidate.Name);
            return null;
        }

        using var client = RequireHttpClientFactory().CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/octet-stream");

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        if (response.Content.Headers.ContentLength is > MaxJimakuDownloadBytes)
        {
            return null;
        }

        var bytes = await SubtitleDownloadContent.ReadLimitedBytesAsync(
            response.Content,
            MaxJimakuDownloadBytes,
            cancellationToken);

        if (bytes is null)
        {
            return null;
        }

        var extension = Path.GetExtension(candidate.Name).ToLowerInvariant();
        if (extension == ".zip")
        {
            return ExtractSubtitleFromZip(bytes, episodeNumber);
        }

        var format = NormalizeDownloadedFormat(extension);
        if (format is null || bytes.Length > MaxSubtitleBytes)
        {
            return null;
        }

        return (
            candidate.Name,
            format,
            SubtitleDownloadContent.Decode(bytes));
    }

    private static (string FileName, string Format, string Content)?
        ExtractSubtitleFromZip(byte[] bytes, int episodeNumber)
    {
        using var memory = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false);

        var candidates = archive.Entries
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.Name) &&
                NormalizeDownloadedFormat(Path.GetExtension(entry.Name)) is not null &&
                entry.Length is > 0 and <= MaxSubtitleBytes)
            .Select(entry => new
            {
                Entry = entry,
                Candidate = new JimakuSubtitleFileCandidate(entry.FullName, "")
            })
            .Select(x => new
            {
                x.Entry,
                Score = JimakuSubtitleMatcher.Score(
                    x.Candidate.Name,
                    episodeNumber,
                    episodeFiltered: false)
            })
            .Where(x => x.Score > int.MinValue)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (candidates is null)
        {
            return null;
        }

        using var stream = candidates.Entry.Open();
        using var output = new MemoryStream((int)candidates.Entry.Length);
        stream.CopyTo(output);

        var format = NormalizeDownloadedFormat(
            Path.GetExtension(candidates.Entry.Name));

        return format is null
            ? null
            : (
                candidates.Entry.FullName,
                format,
                SubtitleDownloadContent.Decode(output.ToArray()));
    }

    private async Task<HttpResponseMessage> SendJimakuAsync(
        string apiKey,
        string relativeUrl,
        CancellationToken cancellationToken)
    {
        var client = RequireHttpClientFactory().CreateClient();
        client.BaseAddress = new Uri(JimakuApiBase);
        client.Timeout = TimeSpan.FromSeconds(20);

        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.TryAddWithoutValidation("Authorization", apiKey);
        request.Headers.UserAgent.ParseAdd(
            "Jularr/0.1 (+https://github.com/Juloc/Jularr)");
        request.Headers.Accept.ParseAdd("application/json");

        return await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    private async Task<string?> LoadJimakuApiKeyAsync(
        CancellationToken cancellationToken)
    {
        var persisted = await LoadPersistedJimakuSettingsAsync(cancellationToken);
        if (persisted is null)
        {
            return null;
        }

        try
        {
            var key = RequireJimakuProtector().Unprotect(persisted.ProtectedApiKey);
            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }
        catch (CryptographicException exception)
        {
            logger?.LogWarning(exception, "Could not decrypt the Jimaku API key.");
            return null;
        }
    }

    private async Task<PersistedJimakuSettings?> LoadPersistedJimakuSettingsAsync(
        CancellationToken cancellationToken)
    {
        await JimakuStoreGate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(JimakuStorePath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(
                JimakuStorePath,
                cancellationToken);
            return JsonSerializer.Deserialize<PersistedJimakuSettings>(
                json,
                JsonOptions);
        }
        catch (Exception exception) when (
            exception is JsonException or
            IOException or
            UnauthorizedAccessException)
        {
            logger?.LogWarning(exception, "Could not load Jimaku settings.");
            return null;
        }
        finally
        {
            JimakuStoreGate.Release();
        }
    }

    private async Task<EpisodeMediaSnapshot?> GetEpisodeMediaAsync(
        Guid episodeId,
        CancellationToken cancellationToken) =>
        await (
            from media in db.MediaFiles.AsNoTracking()
            join episode in db.Episodes.AsNoTracking()
                on media.EpisodeId equals episode.Id
            join anime in db.Anime.AsNoTracking()
                on episode.AnimeId equals anime.Id
            where episode.Id == episodeId
            orderby media.Path
            select new EpisodeMediaSnapshot(
                episode.Id,
                anime.Title,
                episode.Number,
                media.Path,
                media.LastWriteTimeUtc))
            .FirstOrDefaultAsync(cancellationToken);

    private Task<bool> HasUsableTextAsync(
        Guid episodeId,
        string targetLanguage,
        CancellationToken cancellationToken) =>
        db.SubtitleTracks
            .AsNoTracking()
            .AnyAsync(
                track =>
                    track.EpisodeId == episodeId &&
                    track.Language == targetLanguage &&
                    db.SubtitleCues.Any(cue => cue.SubtitleTrackId == track.Id),
                cancellationToken);

    private async Task<List<Guid>> GetMissingEpisodeIdsAsync(
        CancellationToken cancellationToken)
    {
        var targetLanguage = await contentLanguageResolver.ResolveTargetLanguageAsync(cancellationToken);
        return await db.MediaFiles
            .AsNoTracking()
            .Where(x => x.EpisodeId.HasValue)
            .Select(x => x.EpisodeId!.Value)
            .Distinct()
            .Where(episodeId =>
                !db.SubtitleTracks.Any(track =>
                    track.EpisodeId == episodeId &&
                    track.Language == targetLanguage &&
                    db.SubtitleCues.Any(cue => cue.SubtitleTrackId == track.Id)))
            .ToListAsync(cancellationToken);
    }

    // Only prunes other *normal* (non-forced, non-SDH) tracks in the same language: forced/SDH
    // tracks imported through ImportManualSearchResultAsync (#526) are a separate wanted item, not
    // a stale copy of the single "current learning source" this pipeline otherwise manages.
    private Task<int> RemoveOtherTracksAsync(
        Guid episodeId,
        Guid preferredTrackId,
        string targetLanguage,
        CancellationToken cancellationToken) =>
        db.SubtitleTracks
            .Where(x =>
                x.EpisodeId == episodeId &&
                x.Language == targetLanguage &&
                !x.Forced &&
                !x.Sdh &&
                x.Id != preferredTrackId)
            .ExecuteDeleteAsync(cancellationToken);

    private static LearningTextSourceKind DetectSourceKind(string sourceKey)
    {
        if (sourceKey.StartsWith(
                EmbeddedSubtitleExtractor.TranscriptionSourcePrefix,
                StringComparison.Ordinal))
        {
            return LearningTextSourceKind.Whisper;
        }

        if (sourceKey.StartsWith(
                EmbeddedSubtitleExtractor.SourcePrefix,
                StringComparison.Ordinal))
        {
            return LearningTextSourceKind.EmbeddedSubtitle;
        }

        if (sourceKey.StartsWith(JimakuSourcePrefix, StringComparison.Ordinal))
        {
            return LearningTextSourceKind.Jimaku;
        }

        return LearningTextSourceKind.LocalSubtitle;
    }

    private static void MarkPreparationReady(
        Guid episodeId,
        LearningTextSourceKind source,
        string message) =>
        PreparationStates[episodeId] = new LearningTextPreparationState(
            LearningTextPreparationStatus.Ready,
            source,
            message,
            DateTimeOffset.UtcNow);

    private static void MarkPreparationFailed(Guid episodeId, string message) =>
        PreparationStates[episodeId] = new LearningTextPreparationState(
            LearningTextPreparationStatus.Failed,
            Message: message,
            UpdatedAt: DateTimeOffset.UtcNow);

    private static string BuildJimakuSourceKey(
        long entryId,
        string url,
        string fileName)
    {
        var fingerprint = $"{url}\n{fileName}";
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))
            .ToLowerInvariant();
        return $"{JimakuSourcePrefix}{entryId}:{hash}";
    }

    private static string? NormalizeDownloadedFormat(string extension) =>
        extension.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "srt" => "srt",
            "ass" => "ass",
            "ssa" => "ssa",
            _ => null
        };

    private EmbeddedSubtitleExtractor RequireEmbeddedSubtitleExtractor() =>
        embeddedSubtitleExtractor ?? throw new InvalidOperationException(
            "Learning-text preparation requires EmbeddedSubtitleExtractor.");

    private AnimeMetadataService RequireAnimeMetadataService() =>
        animeMetadataService ?? throw new InvalidOperationException(
            "Jimaku lookup requires AnimeMetadataService.");

    private BackgroundJobQueue RequireJobs() =>
        jobs ?? throw new InvalidOperationException(
            "Learning-text preparation requires BackgroundJobQueue.");

    private IHttpClientFactory RequireHttpClientFactory() =>
        httpClientFactory ?? throw new InvalidOperationException(
            "Jimaku lookup requires IHttpClientFactory.");

    private IDataProtector RequireJimakuProtector() =>
        jimakuProtector ?? throw new InvalidOperationException(
            "Jimaku settings require ASP.NET Core Data Protection.");

    private static void SetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    // Which (language, forced, SDH) combination a track fills. IsLearningSource marks the single
    // normal track in the learning language that feeds vocabulary; ReadyMessage overrides the
    // preparation message for imports that are not part of the automatic pipeline.
    private sealed record TrackSlot(
        string Language,
        bool Forced,
        bool Sdh,
        bool IsLearningSource,
        string? ReadyMessage = null);

    private sealed record TrackStamp(Guid Id, Guid EpisodeId, DateTime SourceUpdatedAt);

    private sealed record EpisodeMediaSnapshot(
        Guid EpisodeId,
        string AnimeTitle,
        int EpisodeNumber,
        string MediaPath,
        DateTime SourceUpdatedAt);

    private sealed record PersistedJimakuSettings(
        string ProtectedApiKey,
        DateTimeOffset UpdatedAt);

    private sealed record JimakuEntry(
        long Id,
        string Name,
        [property: JsonPropertyName("anilist_id")] long? AniListId);

    private sealed record JimakuFile(
        string Name,
        string Url,
        [property: JsonPropertyName("last_modified")] DateTimeOffset? LastModified);
}
