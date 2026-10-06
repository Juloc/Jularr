using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32.SafeHandles;

namespace Jularr.Web.Features.Library;

// Canonical media inventory: owns the persisted ffprobe analysis of every MediaFile. Library
// reconciliation, playback and embedded-subtitle extraction read it; ffprobe only runs when the
// analysed source identity or CurrentProbeVersion no longer matches.
public sealed class MediaInventoryService(
    IServiceScopeFactory scopeFactory,
    IMediaProbeRunner probeRunner,
    ILogger<MediaInventoryService> logger)
{
    // Bump whenever the ffprobe invocation or MediaProbeParser output changes. Every analysis with an
    // older version is re-probed by the next reconciliation or on first use.
    public const int CurrentProbeVersion = 1;

    // The fingerprint hashes the file length plus the first and last 64 KiB. It is only read when a
    // file needs analysis or its mtime changed without a size change, so unchanged libraries cost a stat.
    public const int FingerprintChunkBytes = 64 * 1024;

    public const int DiagnosticMaxLength = 500;

    // Never equal to CurrentProbeVersion (or any version Jularr has ever shipped), so an
    // invalidated analysis is always evaluated as stale.
    private const int InvalidatedProbeVersion = -1;

    // A deferred (Pending) analysis is not retried sooner, so a missing or hanging ffprobe is not
    // re-run by every consumer of the same file within one scan or page load.
    public static readonly TimeSpan PendingRetryDelay = TimeSpan.FromMinutes(5);

    // Files whose stored (Succeeded) analysis is kept because ffprobe could not run, with the instant its
    // probe may be tried again. Only touched under analysisGate; the stored row has no Pending state to carry this.
    private readonly Dictionary<Guid, DateTime> probeRetryNotBefore = [];

    // One analysis at a time per process: it bounds NAS/ffprobe load and makes the
    // "re-check, probe, persist" sequence race-free for concurrent on-demand callers.
    private readonly SemaphoreSlim analysisGate = new(1, 1);

    public async Task<MediaInventoryEntry?> GetAsync(
        Guid mediaFileId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var analysis = await db.MediaTechnicalAnalyses
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.MediaFileId == mediaFileId, cancellationToken);

        return analysis is null
            ? null
            : await ToEntryAsync(db, analysis, cancellationToken);
    }

    public async Task<IReadOnlyList<MediaInventoryEntry>> ListAsync(
        Guid libraryRootId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var analyses = await (
                from analysis in db.MediaTechnicalAnalyses.AsNoTracking()
                join media in db.StoredFiles.AsNoTracking() on analysis.MediaFileId equals media.Id
                where media.LibraryRootId == libraryRootId
                orderby media.Path
                select analysis)
            .ToListAsync(cancellationToken);

        var streams = (await (
                    from stream in db.MediaTracks.AsNoTracking()
                    join media in db.StoredFiles.AsNoTracking() on stream.MediaFileId equals media.Id
                    where media.LibraryRootId == libraryRootId
                    select stream)
                .ToListAsync(cancellationToken))
            .ToLookup(x => x.MediaFileId);

        return
        [
            .. analyses.Select(analysis => ToEntryFromRows(analysis, streams[analysis.MediaFileId]))
        ];
    }

    public Task<MediaInventoryEntry?> EnsureAnalyzedAsync(
        Guid mediaFileId,
        CancellationToken cancellationToken) =>
        EnsureAnalyzedCoreAsync(mediaFileId, null, cancellationToken);

    public Task<MediaInventoryEntry?> EnsureAnalyzedAsync(
        string mediaPath,
        CancellationToken cancellationToken) =>
        EnsureAnalyzedCoreAsync(null, Path.GetFullPath(mediaPath), cancellationToken);

    // Brings every media file of a root up to date. Uses the scanner-observed size/mtime on the
    // MediaFile rows, so an unchanged library performs no ffprobe and no extra file I/O.
    public Task<MediaInventoryReconciliation> ReconcileAsync(
        Guid libraryRootId,
        CancellationToken cancellationToken) =>
        ReconcileAsync(libraryRootId, null, cancellationToken);

    // Forces the given media files to be re-probed on their next EnsureAnalyzedAsync/ReconcileAsync
    // call: an invalidated ProbeVersion makes Evaluate() see them as stale, so the existing
    // analysis path re-runs ffprobe without a second probe path. Used by the per-anime
    // "re-analyse media" repair action, which never invents its own probing. An explicit re-analysis also ends
    // the wait after an unavailable ffprobe, so a repaired tool is tried at once.
    public async Task<int> InvalidateAsync(
        IReadOnlyCollection<Guid> mediaFileIds,
        CancellationToken cancellationToken)
    {
        if (mediaFileIds.Count == 0)
        {
            return 0;
        }

        await analysisGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var mediaFileId in mediaFileIds)
            {
                probeRetryNotBefore.Remove(mediaFileId);
            }
        }
        finally
        {
            analysisGate.Release();
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.MediaTechnicalAnalyses
            .Where(x => mediaFileIds.Contains(x.MediaFileId))
            .ExecuteUpdateAsync(
                update => update.SetProperty(x => x.ProbeVersion, InvalidatedProbeVersion),
                cancellationToken);
    }

    // A partial library scan passes the full-path prefix of its folder (ending in a directory
    // separator) so only the media it reconciled are brought up to date.
    public async Task<MediaInventoryReconciliation> ReconcileAsync(
        Guid libraryRootId,
        string? pathPrefix,
        CancellationToken cancellationToken)
    {
        List<MediaFileIdentity> files;
        Dictionary<Guid, MediaTechnicalAnalysis> analyses;

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            files = await db.StoredFiles
                .AsNoTracking()
                .Where(x => x.LibraryRootId == libraryRootId)
                .OrderBy(x => x.Path)
                .Select(x => new MediaFileIdentity(x.Id, x.Path, x.SizeBytes, x.LastWriteTimeUtc))
                .ToListAsync(cancellationToken);

            analyses = await (
                    from analysis in db.MediaTechnicalAnalyses.AsNoTracking()
                    join media in db.StoredFiles.AsNoTracking() on analysis.MediaFileId equals media.Id
                    where media.LibraryRootId == libraryRootId
                    select analysis)
                .ToDictionaryAsync(x => x.MediaFileId, cancellationToken);
        }

        if (pathPrefix is not null)
        {
            files = files
                .Where(x => x.Path.StartsWith(pathPrefix, StringComparison.Ordinal))
                .ToList();
        }

        int unchanged = 0, analyzed = 0, failed = 0, deferred = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var observed = new SourceIdentity(file.SizeBytes, file.LastWriteTimeUtc);
            if (Evaluate(analyses.GetValueOrDefault(file.Id), observed) == Freshness.Current)
            {
                unchanged++;
                continue;
            }

            var (_, outcome) = await AnalyzeAsync(
                file.Id,
                Path.GetFullPath(file.Path),
                observed,
                cancellationToken);

            switch (outcome)
            {
                case AnalysisOutcome.Unchanged:
                    unchanged++;
                    break;
                case AnalysisOutcome.Analyzed:
                    analyzed++;
                    break;
                case AnalysisOutcome.Failed:
                    failed++;
                    break;
                case AnalysisOutcome.Deferred:
                    deferred++;
                    break;
            }
        }

        return new MediaInventoryReconciliation(unchanged, analyzed, failed, deferred);
    }

    public static async Task<string?> TryComputeFingerprintAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = RandomAccess.GetLength(handle);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var lengthBytes = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, length);
            hash.AppendData(lengthBytes);

            var buffer = new byte[FingerprintChunkBytes];
            await AppendRangeAsync(handle, hash, buffer, 0, length, cancellationToken);
            if (length > FingerprintChunkBytes)
            {
                var tailOffset = Math.Max(FingerprintChunkBytes, length - FingerprintChunkBytes);
                await AppendRangeAsync(handle, hash, buffer, tailOffset, length, cancellationToken);
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<MediaInventoryEntry?> EnsureAnalyzedCoreAsync(
        Guid? mediaFileId,
        string? fullPath,
        CancellationToken cancellationToken)
    {
        MediaFileIdentity? media;
        SourceIdentity observed;

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var query = db.StoredFiles.AsNoTracking();
            query = mediaFileId is { } id
                ? query.Where(x => x.Id == id)
                : query.Where(x => x.Path == fullPath);

            media = await query
                .Select(x => new MediaFileIdentity(x.Id, x.Path, x.SizeBytes, x.LastWriteTimeUtc))
                .SingleOrDefaultAsync(cancellationToken);
            if (media is null)
            {
                return null;
            }

            var file = new FileInfo(Path.GetFullPath(media.Path));
            if (!file.Exists)
            {
                return null;
            }

            observed = new SourceIdentity(file.Length, file.LastWriteTimeUtc);
            var analysis = await db.MediaTechnicalAnalyses
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.MediaFileId == media.Id, cancellationToken);

            if (Evaluate(analysis, observed) == Freshness.Current)
            {
                return await ToEntryAsync(db, analysis!, cancellationToken);
            }
        }

        var (entry, _) = await AnalyzeAsync(
            media.Id,
            Path.GetFullPath(media.Path),
            observed,
            cancellationToken);
        return entry;
    }

    private async Task<(MediaInventoryEntry Entry, AnalysisOutcome Outcome)> AnalyzeAsync(
        Guid mediaFileId,
        string fullPath,
        SourceIdentity observed,
        CancellationToken cancellationToken)
    {
        await analysisGate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var analysis = await db.MediaTechnicalAnalyses
                .SingleOrDefaultAsync(x => x.MediaFileId == mediaFileId, cancellationToken);

            // Another caller may have analysed this file while we waited for the gate.
            var freshness = Evaluate(analysis, observed);
            if (freshness == Freshness.Current)
            {
                return (await ToEntryAsync(db, analysis!, cancellationToken), AnalysisOutcome.Unchanged);
            }

            // An analysis that already succeeded keeps serving the file while ffprobe cannot run: a missing or hanging
            // tool says nothing about the file, so it must not erase the stored technical data or its tracks. The wait is checked before
            // the fingerprint so a file on slow storage is not read again for every caller.
            var keepStored = analysis is { Status: MediaAnalysisStatus.Succeeded };
            if (keepStored && probeRetryNotBefore.TryGetValue(mediaFileId, out var retryNotBefore) && retryNotBefore > DateTime.UtcNow)
            {
                return (await ToKeptEntryAsync(db, analysis!, cancellationToken), AnalysisOutcome.Deferred);
            }

            var fingerprint = await TryComputeFingerprintAsync(fullPath, cancellationToken);
            if (freshness == Freshness.ModifiedTimeOnly &&
                fingerprint is not null &&
                string.Equals(fingerprint, analysis!.SourceFingerprint, StringComparison.Ordinal))
            {
                // Same length and same head/tail content: a touched or re-copied file keeps its analysis.
                analysis.SourceLastWriteTimeUtc = observed.LastWriteTimeUtc;
                await db.SaveChangesAsync(cancellationToken);
                return (await ToEntryAsync(db, analysis, cancellationToken), AnalysisOutcome.Unchanged);
            }

            var run = await probeRunner.ProbeAsync(fullPath, cancellationToken);
            var (status, diagnostic, technical) = Interpret(run);
            diagnostic = diagnostic?.Replace(fullPath, Path.GetFileName(fullPath), StringComparison.Ordinal);

            if (keepStored && status == MediaAnalysisStatus.Pending)
            {
                analysis!.Diagnostic = Bound(diagnostic, DiagnosticMaxLength);
                await db.SaveChangesAsync(cancellationToken);

                var now = DateTime.UtcNow;
                foreach (var expired in probeRetryNotBefore.Where(x => x.Value <= now).Select(x => x.Key).ToList())
                {
                    probeRetryNotBefore.Remove(expired);
                }

                probeRetryNotBefore[mediaFileId] = now + PendingRetryDelay;
                logger.LogDebug("Media analysis for {MediaPath} was deferred and its stored analysis kept: {Diagnostic}", fullPath, analysis.Diagnostic);
                return (await ToKeptEntryAsync(db, analysis, cancellationToken), AnalysisOutcome.Deferred);
            }

            probeRetryNotBefore.Remove(mediaFileId);
            if (analysis is null)
            {
                analysis = new MediaTechnicalAnalysis { MediaFileId = mediaFileId };
                db.MediaTechnicalAnalyses.Add(analysis);
            }

            Apply(analysis, observed, fingerprint, status, diagnostic, technical);

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.MediaTracks
                .Where(x => x.MediaFileId == mediaFileId)
                .ExecuteDeleteAsync(cancellationToken);
            if (technical is not null)
            {
                var tracks = technical.Streams.Select(stream => ToRow(mediaFileId, stream)).ToList();
                if (technical.Video is { StreamIndex: { } videoIndex } video)
                {
                    tracks.Add(new MediaTrack
                    {
                        MediaFileId = mediaFileId,
                        StreamIndex = videoIndex,
                        Kind = MediaTrackKind.Video,
                        Codec = Bound(video.Codec, 64)
                    });
                }

                db.MediaTracks.AddRange(tracks);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var outcome = status switch
            {
                MediaAnalysisStatus.Succeeded => AnalysisOutcome.Analyzed,
                MediaAnalysisStatus.Failed => AnalysisOutcome.Failed,
                _ => AnalysisOutcome.Deferred
            };

            if (outcome == AnalysisOutcome.Failed)
            {
                logger.LogWarning(
                    "Media analysis failed for {MediaPath}: {Diagnostic} The file is skipped until it changes.",
                    fullPath,
                    analysis.Diagnostic);
            }
            else if (outcome == AnalysisOutcome.Deferred)
            {
                logger.LogDebug(
                    "Media analysis for {MediaPath} was deferred: {Diagnostic}",
                    fullPath,
                    analysis.Diagnostic);
            }

            return (ToEntry(analysis, technical), outcome);
        }
        finally
        {
            analysisGate.Release();
        }
    }

    private static (MediaAnalysisStatus Status, string? Diagnostic, MediaTechnicalInfo? Technical) Interpret(
        MediaProbeRun run)
    {
        switch (run.Status)
        {
            case MediaProbeRunStatus.Unavailable:
                return (MediaAnalysisStatus.Pending, run.Error ?? "ffprobe was unavailable.", null);
            case MediaProbeRunStatus.Failed:
                return (MediaAnalysisStatus.Failed, run.Error ?? "ffprobe rejected the file.", null);
        }

        MediaTechnicalInfo technical;
        try
        {
            technical = MediaProbeParser.Parse(run.Output);
        }
        catch (JsonException)
        {
            return (MediaAnalysisStatus.Failed, "ffprobe returned invalid JSON.", null);
        }

        return technical.Video is null && technical.Streams.Count == 0
            ? (MediaAnalysisStatus.Failed, "ffprobe found no video, audio or subtitle streams.", null)
            : (MediaAnalysisStatus.Succeeded, null, technical);
    }

    private static void Apply(
        MediaTechnicalAnalysis analysis,
        SourceIdentity observed,
        string? fingerprint,
        MediaAnalysisStatus status,
        string? diagnostic,
        MediaTechnicalInfo? technical)
    {
        analysis.Status = status;
        analysis.ProbeVersion = CurrentProbeVersion;
        analysis.SourceSizeBytes = observed.SizeBytes;
        analysis.SourceLastWriteTimeUtc = observed.LastWriteTimeUtc;
        analysis.SourceFingerprint = fingerprint;
        analysis.Diagnostic = Bound(diagnostic, DiagnosticMaxLength);
        analysis.AnalyzedAt = DateTime.UtcNow;
        analysis.Container = Bound(technical?.Container, 120);
        analysis.DurationSeconds = technical?.DurationSeconds;

        var video = technical?.Video;
        analysis.VideoCodec = Bound(video?.Codec, 64);
        analysis.VideoProfile = Bound(video?.Profile, 80);
        analysis.Width = video?.Width;
        analysis.Height = video?.Height;
        analysis.PixelFormat = Bound(video?.PixelFormat, 40);
        analysis.BitDepth = video?.BitDepth;
        analysis.DynamicRange = Bound(video?.DynamicRange, 24);
    }

    private static MediaTrack ToRow(Guid mediaFileId, MediaStreamInfo stream) =>
        new()
        {
            MediaFileId = mediaFileId,
            StreamIndex = stream.Index,
            Kind = stream.Kind,
            Codec = Bound(stream.Codec, 64),
            Language = Bound(stream.Language, 32),
            Title = Bound(stream.Title, 300),
            Channels = stream.Channels,
            ChannelLayout = Bound(stream.ChannelLayout, 64),
            IsDefault = stream.IsDefault,
            IsForced = stream.IsForced
        };

    private static async Task<MediaInventoryEntry> ToEntryAsync(
        AppDbContext db,
        MediaTechnicalAnalysis analysis,
        CancellationToken cancellationToken)
    {
        if (analysis.Status != MediaAnalysisStatus.Succeeded)
        {
            return ToEntry(analysis, technical: null);
        }

        var streams = await db.MediaTracks
            .AsNoTracking()
            .Where(x => x.MediaFileId == analysis.MediaFileId)
            .ToListAsync(cancellationToken);
        return ToEntryFromRows(analysis, streams);
    }

    // The re-check did not happen, so the entry is Pending while Technical still carries the last stored analysis;
    // callers that report a re-analysis therefore never count it as done, and playback keeps its data.
    private static async Task<MediaInventoryEntry> ToKeptEntryAsync(
        AppDbContext db,
        MediaTechnicalAnalysis analysis,
        CancellationToken cancellationToken) =>
        (await ToEntryAsync(db, analysis, cancellationToken)) with { Status = MediaAnalysisStatus.Pending };

    private static MediaInventoryEntry ToEntryFromRows(
        MediaTechnicalAnalysis analysis,
        IEnumerable<MediaTrack> streams) =>
        ToEntry(
            analysis,
            analysis.Status == MediaAnalysisStatus.Succeeded
                ? new MediaTechnicalInfo(
                    analysis.Container,
                    analysis.DurationSeconds,
                    analysis.VideoCodec is null
                        ? null
                        : new MediaVideoInfo(
                            analysis.VideoCodec,
                            analysis.VideoProfile,
                            analysis.Width,
                            analysis.Height,
                            analysis.PixelFormat,
                            analysis.BitDepth,
                            analysis.DynamicRange),
                    [
                        .. streams
                            .Where(x => x.Kind != MediaTrackKind.Video)
                            .OrderBy(x => x.StreamIndex)
                            .Select(x => new MediaStreamInfo(
                                x.StreamIndex,
                                x.Kind,
                                x.Codec,
                                x.Language,
                                x.Title,
                                x.Channels,
                                x.ChannelLayout,
                                x.IsDefault,
                                x.IsForced))
                    ])
                : null);

    private static MediaInventoryEntry ToEntry(
        MediaTechnicalAnalysis analysis,
        MediaTechnicalInfo? technical) =>
        new(
            analysis.MediaFileId,
            analysis.Status,
            analysis.ProbeVersion,
            analysis.AnalyzedAt,
            analysis.Diagnostic,
            technical);

    private static Freshness Evaluate(MediaTechnicalAnalysis? analysis, SourceIdentity observed)
    {
        if (analysis is null ||
            analysis.ProbeVersion != CurrentProbeVersion ||
            analysis.SourceSizeBytes != observed.SizeBytes)
        {
            return Freshness.Stale;
        }

        var sameModifiedTime = analysis.SourceLastWriteTimeUtc == observed.LastWriteTimeUtc;
        if (analysis.Status == MediaAnalysisStatus.Pending)
        {
            return sameModifiedTime && DateTime.UtcNow - analysis.AnalyzedAt < PendingRetryDelay
                ? Freshness.Current
                : Freshness.Stale;
        }

        return sameModifiedTime ? Freshness.Current : Freshness.ModifiedTimeOnly;
    }

    private static async Task AppendRangeAsync(
        SafeFileHandle handle,
        IncrementalHash hash,
        byte[] buffer,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        var remaining = (int)Math.Min(buffer.Length, length - offset);
        var filled = 0;
        while (filled < remaining)
        {
            var read = await RandomAccess.ReadAsync(
                handle,
                buffer.AsMemory(filled, remaining - filled),
                offset + filled,
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        hash.AppendData(buffer, 0, filled);
    }

    private static string? Bound(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    private enum Freshness
    {
        Current,
        ModifiedTimeOnly,
        Stale
    }

    private enum AnalysisOutcome
    {
        Unchanged,
        Analyzed,
        Failed,
        Deferred
    }

    private readonly record struct SourceIdentity(long SizeBytes, DateTime LastWriteTimeUtc);

    private sealed record MediaFileIdentity(
        Guid Id,
        string Path,
        long SizeBytes,
        DateTime LastWriteTimeUtc);
}
