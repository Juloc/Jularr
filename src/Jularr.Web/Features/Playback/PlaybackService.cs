using System.Text;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Learning;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Media.Compatibility;
using Jularr.Web.Features.MediaSegments;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Subtitles;
using Jularr.Web.Features.Vocabulary;
using Jularr.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Playback;

public enum PlaybackOptionAvailability
{
    Ready,
    CanPrepare,
    Preparing,
    Failed,
    Unsupported
}

public sealed record PlaybackOption(
    PlaybackOptionAvailability Availability,
    string StatusMessage,
    bool UsesLiveStream = false)
{
    public bool IsReady => Availability == PlaybackOptionAvailability.Ready;
    public bool CanPrepare =>
        Availability is PlaybackOptionAvailability.CanPrepare or PlaybackOptionAvailability.Failed;
    public bool IsPreparing => Availability == PlaybackOptionAvailability.Preparing;
}

public sealed record PlaybackMedia(
    Guid EpisodeId,
    Guid MediaFileId,
    string SourcePath,
    string FileName,
    string ContentType,
    string? VideoCodec,
    PlaybackOption Device,
    PlaybackOption Server,
    double? DurationSeconds = null,
    string? PixelFormat = null,
    string? AudioCodec = null,
    long? SizeBytes = null,
    IReadOnlyList<PlaybackMediaTrack>? Tracks = null,
    MediaAvailabilitySnapshot? Storage = null,
    int? VideoHeight = null)
{
    public bool HasReadyOption => Device.IsReady || Server.IsReady;
    public bool IsPreparing => Device.IsPreparing || Server.IsPreparing;
}

public sealed record PlaybackTermInfo(
    Guid TermId,
    string Canonical,
    string? Reading,
    string? Meaning,
    UserTermState? State);

public sealed record PlaybackToken(
    string Surface,
    Guid? TermId,
    string? Canonical,
    string? Reading,
    string? Meaning,
    string? State)
{
    public bool IsVocabulary => TermId.HasValue;
}

public sealed record PlaybackCue(
    int StartMs,
    int EndMs,
    IReadOnlyList<PlaybackToken> Tokens,
    long CueId = 0);

public sealed record PlaybackCueSet(
    Guid? TrackId,
    IReadOnlyList<PlaybackCue> Cues)
{
    public static PlaybackCueSet Empty { get; } = new(null, []);
}

public sealed record EpisodePlaybackSnapshot(
    PlaybackMedia? Media,
    IReadOnlyList<PlaybackCue> Cues,
    EpisodePlayerNavigation? Navigation = null)
{
    public static EpisodePlaybackSnapshot Empty { get; } = new(null, []);
}

public static class PlaybackMediaTypes
{
    public static string GetContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".ogg" or ".ogv" => "video/ogg",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".ts" or ".m2ts" => "video/mp2t",
            _ => "application/octet-stream"
        };

    public static bool IsLikelyBrowserSupportedContainer(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".mp4" or ".m4v" or ".webm" or ".ogg" or ".ogv";

    public static bool IsLikelyBrowserSupported(string path) =>
        IsLikelyBrowserSupportedContainer(path);
}

public sealed class PlaybackCueProjector(IJapaneseMorphology morphology)
{
    public PlaybackCue Project(
        int startMs,
        int endMs,
        string text,
        IReadOnlyDictionary<string, PlaybackTermInfo> terms,
        long cueId = 0)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        // Without a Japanese dictionary the cue is still playable; it just carries no word tokens.
        var analyzed = morphology.Status.IsAvailable ? morphology.Analyze(normalized) : [];

        if (analyzed.Count == 0)
        {
            return Plain(startMs, endMs, normalized, cueId);
        }

        var tokens = new List<PlaybackToken>(analyzed.Count + 2);
        var cursor = 0;

        foreach (var token in analyzed)
        {
            if (string.IsNullOrEmpty(token.Surface))
            {
                continue;
            }

            var index = normalized.IndexOf(token.Surface, cursor, StringComparison.Ordinal);
            if (index < 0)
            {
                return Plain(startMs, endMs, normalized, cueId);
            }

            if (index > cursor)
            {
                tokens.Add(PlainToken(normalized[cursor..index]));
            }

            var canonical = token.Canonical.Normalize(NormalizationForm.FormKC).Trim();
            if (terms.TryGetValue(canonical, out var term))
            {
                tokens.Add(new PlaybackToken(
                    token.Surface,
                    term.TermId,
                    term.Canonical,
                    term.Reading,
                    term.Meaning,
                    term.State?.ToString()));
            }
            else
            {
                tokens.Add(PlainToken(token.Surface));
            }

            cursor = index + token.Surface.Length;
        }

        if (cursor < normalized.Length)
        {
            tokens.Add(PlainToken(normalized[cursor..]));
        }

        return new PlaybackCue(startMs, endMs, tokens, cueId);
    }

    private static PlaybackCue Plain(int startMs, int endMs, string text, long cueId) =>
        new(startMs, endMs, string.IsNullOrEmpty(text) ? [] : [PlainToken(text)], cueId);

    private static PlaybackToken PlainToken(string surface) =>
        new(surface, null, null, null, null, null);
}

public sealed record PlaybackStream(
    string SourcePath,
    string ContentType,
    DateTimeOffset LastModified,
    PlaybackPreparationPlan? LivePlan,
    double? DurationSeconds = null,
    int? AudioStreamIndex = null,
    PlaybackQualityCap QualityCap = PlaybackQualityCap.Auto)
{
    public bool IsLive => LivePlan is not null;
}

/// <summary>
/// One playback stream request. <paramref name="AudioTrackId"/> is a canonical
/// <c>stream:N</c> id; null keeps the file default. The quality cap is the
/// requesting device's bandwidth limit.
/// </summary>
public sealed record PlaybackStreamRequest(
    PlaybackRequestedMode Mode,
    string? AudioTrackId = null,
    PlaybackQualityCap QualityCap = PlaybackQualityCap.Auto);

/// <summary>
/// Outcome of <see cref="PlaybackService.Decide"/>: a null plan means direct
/// play of the source file; otherwise a live remux/encode. The audio stream is
/// the resolved selection (null only when the file has no audio).
/// </summary>
public sealed record PlaybackStreamDecision(
    PlaybackPreparationPlan? Plan,
    int? AudioStreamIndex);

/// <summary>Plain text cues of one embedded subtitle stream for display-only playback subtitles.</summary>
public sealed record PlaybackEmbeddedSubtitleCues(
    string TrackId,
    string? Language,
    IReadOnlyList<SubtitleCueData> Cues);

public sealed class PlaybackService
{
    private readonly AppDbContext db;
    private readonly PlaybackCueProjector projector;
    private readonly MediaInventoryService mediaInventory;
    private readonly EmbeddedSubtitleExtractor? subtitleExtractor;
    private readonly MediaAvailabilityService? mediaAvailability;
    private readonly MediaSegmentService? segments;
    private readonly string profileId;

    public PlaybackService(
        AppDbContext db,
        PlaybackCueProjector projector,
        MediaInventoryService mediaInventory,
        EmbeddedSubtitleExtractor subtitleExtractor,
        MediaAvailabilityService mediaAvailability,
        CurrentAccountContext currentAccount,
        MediaSegmentService? segments = null)
        : this(db, projector, mediaInventory, subtitleExtractor, mediaAvailability, currentAccount.ProfileId, segments)
    {
    }

    /// <summary>Decision-only instance (no storage checks, no subtitle extraction) for focused tests.</summary>
    public PlaybackService(
        AppDbContext db,
        PlaybackCueProjector projector,
        MediaInventoryService mediaInventory,
        MediaSegmentService? segments = null)
        : this(db, projector, mediaInventory, null, null, LearningProfile.DefaultId, segments)
    {
    }

    private PlaybackService(
        AppDbContext db,
        PlaybackCueProjector projector,
        MediaInventoryService mediaInventory,
        EmbeddedSubtitleExtractor? subtitleExtractor,
        MediaAvailabilityService? mediaAvailability,
        string profileId,
        MediaSegmentService? segments)
    {
        this.db = db;
        this.projector = projector;
        this.mediaInventory = mediaInventory;
        this.subtitleExtractor = subtitleExtractor;
        this.mediaAvailability = mediaAvailability;
        this.profileId = profileId;
        this.segments = segments;
    }

    public async Task<PlaybackMedia?> GetMediaAsync(
        Guid episodeId,
        CancellationToken cancellationToken)
    {
        var row = await GetMediaRowAsync(episodeId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var availability = await CheckAvailabilityAsync(
            row.Id,
            cancellationToken);

        if (availability is { IsAvailable: false })
        {
            var unavailable = new PlaybackOption(
                PlaybackOptionAvailability.Unsupported,
                availability.State == StorageAvailabilityState.FileMissing
                    ? "The media file is missing from otherwise available storage."
                    : "Media storage is currently unavailable.");

            return new PlaybackMedia(
                episodeId,
                row.Id,
                row.Path,
                Path.GetFileName(row.Path),
                PlaybackMediaTypes.GetContentType(row.Path),
                null,
                unavailable,
                unavailable,
                SizeBytes: row.SizeBytes,
                Storage: availability);
        }

        var probe = await ReadTechnicalInfoAsync(row.Id, cancellationToken);
        if (probe is null)
        {
            availability = await CheckAvailabilityAsync(
                row.Id,
                cancellationToken,
                force: true);

            if (availability is { IsAvailable: false })
            {
                var unavailable = new PlaybackOption(
                    PlaybackOptionAvailability.Unsupported,
                    availability.State == StorageAvailabilityState.FileMissing
                        ? "The media file is missing from otherwise available storage."
                        : "Media storage is currently unavailable.");

                return new PlaybackMedia(
                    episodeId,
                    row.Id,
                    row.Path,
                    Path.GetFileName(row.Path),
                    PlaybackMediaTypes.GetContentType(row.Path),
                    null,
                    unavailable,
                    unavailable,
                    SizeBytes: row.SizeBytes,
                    Storage: availability);
            }

            var failed = new PlaybackOption(
                PlaybackOptionAvailability.Unsupported,
                "Could not inspect this media file.");
            return new PlaybackMedia(
                episodeId,
                row.Id,
                row.Path,
                Path.GetFileName(row.Path),
                PlaybackMediaTypes.GetContentType(row.Path),
                null,
                failed,
                failed,
                SizeBytes: row.SizeBytes,
                Storage: availability);
        }

        if (IsUniversalDirect(row.Path, probe))
        {
            var direct = new PlaybackOption(
                PlaybackOptionAvailability.Ready,
                "Direct play");
            return new PlaybackMedia(
                episodeId,
                row.Id,
                row.Path,
                Path.GetFileName(row.Path),
                PlaybackMediaTypes.GetContentType(row.Path),
                probe.VideoCodec,
                direct,
                direct,
                probe.DurationSeconds,
                probe.PixelFormat,
                probe.AudioCodec,
                row.SizeBytes,
                probe.Tracks,
                availability,
                probe.VideoHeight);
        }

        var device = BuildOption(row, probe, PlaybackRequestedMode.Device);
        var server = BuildOption(row, probe, PlaybackRequestedMode.Server);

        if (IsHevc(probe.VideoCodec) &&
            PlaybackMediaTypes.IsLikelyBrowserSupportedContainer(row.Path))
        {
            device = new PlaybackOption(
                PlaybackOptionAvailability.Ready,
                "HEVC direct play on a capable device");
        }

        return new PlaybackMedia(
            episodeId,
            row.Id,
            row.Path,
            Path.GetFileName(row.Path),
            PlaybackMediaTypes.GetContentType(row.Path),
            probe.VideoCodec,
            device,
            server,
            probe.DurationSeconds,
            probe.PixelFormat,
            probe.AudioCodec,
            row.SizeBytes,
            probe.Tracks,
            availability,
            probe.VideoHeight);
    }

    /// <summary>
    /// Resolves the stream for one request. Direct play wins whenever the file
    /// is browser-compatible, the requested audio track is the file default and
    /// the quality cap is satisfied; a non-default audio track needs a
    /// video-copy remux, and a cap becomes an encode only through
    /// <see cref="ResolvePlan"/>. Returns null when the media, the requested
    /// audio track or a usable plan does not exist.
    /// </summary>
    public async Task<PlaybackStream?> GetStreamAsync(
        Guid episodeId,
        PlaybackStreamRequest request,
        CancellationToken cancellationToken)
    {
        var row = await GetMediaRowAsync(episodeId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var availability = await CheckAvailabilityAsync(
            row.Id,
            cancellationToken);
        if (availability is { IsAvailable: false })
        {
            return null;
        }

        var probe = await ReadTechnicalInfoAsync(row.Id, cancellationToken);
        if (probe is null || !File.Exists(row.Path))
        {
            _ = await CheckAvailabilityAsync(
                row.Id,
                cancellationToken,
                force: true);
            return null;
        }

        var decision = Decide(row.Path, probe, request);
        if (decision is null)
        {
            return null;
        }

        if (decision.Plan is null)
        {
            // HLS always encodes, so it still needs the resolved audio stream and cap.
            return SourceStream(row.Path, probe.DurationSeconds) with
            {
                AudioStreamIndex = decision.AudioStreamIndex,
                QualityCap = request.QualityCap
            };
        }

        return new PlaybackStream(
            row.Path,
            "video/mp4",
            new DateTimeOffset(File.GetLastWriteTimeUtc(row.Path)),
            decision.Plan,
            probe.DurationSeconds,
            decision.AudioStreamIndex,
            request.QualityCap);
    }

    /// <summary>
    /// Pure stream decision from the canonical media inventory facts. Direct
    /// play (null plan) wins whenever the file is browser-compatible, the
    /// requested audio track is the file default and no Server-mode quality cap
    /// is proven to be exceeded by the inventory's source height. Returns null
    /// when the requested audio track or a usable live plan does not exist.
    /// </summary>
    public static PlaybackStreamDecision? Decide(
        string sourcePath,
        PlaybackProbeResult probe,
        PlaybackStreamRequest request)
    {
        PlaybackMediaTrack? audioTrack = null;
        if (request.AudioTrackId is not null)
        {
            audioTrack = FindAudioTrack(probe, request.AudioTrackId);
            if (audioTrack is null)
            {
                return null;
            }
        }

        var defaultAudio = PlaybackTrackSelection.DefaultAudio(probe.Tracks);
        var usesDefaultAudio = audioTrack is null ||
            audioTrack.StreamIndex == defaultAudio?.StreamIndex;

        var directEligible = IsUniversalDirect(sourcePath, probe) ||
            (request.Mode == PlaybackRequestedMode.Device &&
             IsHevc(probe.VideoCodec) &&
             PlaybackMediaTypes.IsLikelyBrowserSupportedContainer(sourcePath));

        // Encoded paths always map the resolved audio stream explicitly so the
        // canonical default (not merely the first stream) survives restarts.
        var effectiveAudio = audioTrack ?? defaultAudio;
        if (directEligible && usesDefaultAudio && !CapForcesEncode(request, probe.VideoHeight))
        {
            return new PlaybackStreamDecision(null, effectiveAudio?.StreamIndex);
        }

        var plan = ResolvePlan(probe, request, effectiveAudio);
        return plan is null
            ? null
            : new PlaybackStreamDecision(plan, effectiveAudio?.StreamIndex);
    }

    /// <summary>
    /// Builds the live plan for a request. A selected audio track re-derives
    /// the audio mode from that track's codec. A quality cap upgrades a
    /// video-copy plan to an H.264 encode only in Server mode and only when
    /// the inventory proves the source is taller than the cap; Device mode
    /// never video-transcodes.
    /// </summary>
    public static PlaybackPreparationPlan? ResolvePlan(
        PlaybackProbeResult probe,
        PlaybackStreamRequest request,
        PlaybackMediaTrack? audioTrack)
    {
        var plan = PlaybackPreparationPlan.Build(probe, request.Mode);
        if (!plan.CanPrepare || plan.Kind is null)
        {
            return null;
        }

        if (audioTrack is not null)
        {
            plan = plan with
            {
                AudioMode = string.Equals(audioTrack.Codec, "aac", StringComparison.OrdinalIgnoreCase)
                    ? PlaybackAudioMode.Copy
                    : PlaybackAudioMode.Aac
            };
        }

        if (plan.VideoMode == PlaybackVideoMode.Copy &&
            CapForcesEncode(request, probe.VideoHeight))
        {
            plan = plan with
            {
                Kind = PlaybackPreparationKind.ServerH264Transcode,
                VideoMode = PlaybackVideoMode.H264,
                TagHevcAsHvc1 = false,
                Message = $"Video will be transcoded to H.264 at most {PlaybackQuality.MaxHeight(request.QualityCap)}p."
            };
        }

        return plan;
    }

    private static bool CapForcesEncode(PlaybackStreamRequest request, int? sourceHeight) =>
        request.Mode == PlaybackRequestedMode.Server &&
        PlaybackQuality.RequiresTranscode(request.QualityCap, sourceHeight);

    /// <summary>
    /// Extracts one embedded text subtitle stream as plain cues so a client can
    /// keep a selected playback subtitle across stream restarts and fallbacks
    /// that drop container subtitles. Returns null when the episode, media or
    /// text stream does not exist.
    /// </summary>
    public async Task<PlaybackEmbeddedSubtitleCues?> GetEmbeddedSubtitleCuesAsync(
        Guid episodeId,
        string trackId,
        CancellationToken cancellationToken)
    {
        if (!PlaybackTrackIds.TryParse(trackId, out var streamIndex))
        {
            return null;
        }

        var row = await GetMediaRowAsync(episodeId, cancellationToken);
        return row is null
            ? null
            : await GetEmbeddedSubtitleCuesAsync(row.Id, row.Path, streamIndex, cancellationToken);
    }

    /// <summary>
    /// The cues of one embedded text stream of an already resolved file, for the legacy episode route and the canonical
    /// video route alike. Returns null when storage is unavailable, the file is gone or the stream is not a text stream.
    /// </summary>
    public async Task<PlaybackEmbeddedSubtitleCues?> GetEmbeddedSubtitleCuesAsync(
        Guid mediaFileId,
        string path,
        int streamIndex,
        CancellationToken cancellationToken)
    {
        var availability = await CheckAvailabilityAsync(mediaFileId, cancellationToken);
        if (availability is { IsAvailable: false } || !File.Exists(path))
        {
            return null;
        }

        var extractor = subtitleExtractor ?? throw new InvalidOperationException(
            "This PlaybackService instance was created without subtitle extraction.");
        var extracted = await extractor.ExtractTextStreamAsync(
            path,
            streamIndex,
            cancellationToken);
        if (extracted is null)
        {
            return null;
        }

        var probe = await ReadTechnicalInfoAsync(mediaFileId, cancellationToken);
        var language = probe?.Tracks?
            .FirstOrDefault(x => x.Kind == PlaybackTrackKind.Subtitle && x.StreamIndex == streamIndex)?
            .Language;

        return new PlaybackEmbeddedSubtitleCues(
            PlaybackTrackIds.Format(streamIndex),
            language,
            SubtitleParser.ParseFormat(extracted.Format, extracted.Content));
    }

    private static PlaybackMediaTrack? FindAudioTrack(
        PlaybackProbeResult probe,
        string trackId) =>
        PlaybackTrackIds.TryParse(trackId, out var streamIndex)
            ? probe.Tracks?.FirstOrDefault(x =>
                x.Kind == PlaybackTrackKind.Audio && x.StreamIndex == streamIndex)
            : null;

    public async Task<EpisodePlaybackSnapshot> GetSnapshotAsync(
        Guid episodeId,
        CancellationToken cancellationToken)
    {
        var media = await GetMediaAsync(episodeId, cancellationToken);
        var cueSet = await GetCueSetAsync(
            episodeId,
            trackId: null,
            fromMs: null,
            toMs: null,
            cancellationToken);
        var navigation = segments is null
            ? null
            : await segments.GetPlayerNavigationAsync(episodeId, media, cancellationToken);

        return new EpisodePlaybackSnapshot(media, cueSet.Cues, navigation);
    }

    public async Task<PlaybackCueSet> GetCueSetAsync(
        Guid episodeId,
        Guid? trackId,
        int? fromMs,
        int? toMs,
        CancellationToken cancellationToken)
    {
        if (fromMs is < 0 || toMs is < 0)
        {
            throw new ArgumentOutOfRangeException(
                fromMs is < 0 ? nameof(fromMs) : nameof(toMs));
        }

        if (fromMs.HasValue && toMs.HasValue && fromMs.Value > toMs.Value)
        {
            throw new ArgumentException("fromMs must be less than or equal to toMs.");
        }

        var trackQuery = db.SubtitleTracks
            .AsNoTracking()
            .Where(x => x.EpisodeId == episodeId && x.Language == "ja");

        if (trackId.HasValue)
        {
            trackQuery = trackQuery.Where(x => x.Id == trackId.Value);
        }

        var resolvedTrackId = await trackQuery
            .OrderByDescending(x => x.ImportedAt)
            .ThenBy(x => x.Id)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (resolvedTrackId is null)
        {
            return PlaybackCueSet.Empty;
        }

        var cueQuery = db.SubtitleCues
            .AsNoTracking()
            .Where(x => x.SubtitleTrackId == resolvedTrackId.Value);

        if (fromMs.HasValue)
        {
            cueQuery = cueQuery.Where(x => x.EndMs >= fromMs.Value);
        }

        if (toMs.HasValue)
        {
            cueQuery = cueQuery.Where(x => x.StartMs <= toMs.Value);
        }

        var cues = await cueQuery
            .OrderBy(x => x.StartMs)
            .ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.StartMs, x.EndMs, x.Text })
            .ToListAsync(cancellationToken);

        var termRows = await (
            from episodeTerm in db.EpisodeTerms.AsNoTracking()
            join term in db.Terms.AsNoTracking() on episodeTerm.TermId equals term.Id
            join stateValue in LearningQueries.TermStates(db, profileId)
                on term.Id equals stateValue.TermId into states
            from state in states.DefaultIfEmpty()
            where episodeTerm.EpisodeId == episodeId
            select new PlaybackTermInfo(
                term.Id,
                term.Canonical,
                term.Reading,
                term.Meaning,
                state == null ? null : state.State))
            .ToListAsync(cancellationToken);

        var terms = termRows.ToDictionary(x => x.Canonical, StringComparer.Ordinal);
        var projected = cues
            .Select(cue => projector.Project(
                cue.StartMs,
                cue.EndMs,
                cue.Text,
                terms,
                cue.Id))
            .ToArray();

        return new PlaybackCueSet(resolvedTrackId, projected);
    }

    public async Task<PlaybackStream?> GetOriginalContentAsync(
        Guid mediaFileId,
        CancellationToken cancellationToken)
    {
        var row = await db.MediaFiles
            .AsNoTracking()
            .Where(x => x.Id == mediaFileId)
            .Select(x => new MediaRow(
                x.Id,
                x.Path,
                x.SizeBytes,
                x.LastWriteTimeUtc))
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var availability = await CheckAvailabilityAsync(
            row.Id,
            cancellationToken);
        if (availability is { IsAvailable: false })
        {
            return null;
        }

        if (!File.Exists(row.Path))
        {
            _ = await CheckAvailabilityAsync(
                row.Id,
                cancellationToken,
                force: true);
            return null;
        }

        return new PlaybackStream(
            row.Path,
            PlaybackMediaTypes.GetContentType(row.Path),
            new DateTimeOffset(File.GetLastWriteTimeUtc(row.Path)),
            null);
    }

    private async Task<MediaAvailabilitySnapshot?> CheckAvailabilityAsync(
        Guid mediaFileId,
        CancellationToken cancellationToken,
        bool force = false) =>
        mediaAvailability is null
            ? null
            : await mediaAvailability.CheckMediaAsync(
                mediaFileId,
                force,
                cancellationToken);

    private async Task<PlaybackProbeResult?> ReadTechnicalInfoAsync(
        Guid mediaFileId,
        CancellationToken cancellationToken) =>
        (await mediaInventory.EnsureAnalyzedAsync(mediaFileId, cancellationToken))?.Technical is { } technical
            ? PlaybackProbeResult.From(technical)
            : null;

    private static PlaybackOption BuildOption(
        MediaRow row,
        PlaybackProbeResult probe,
        PlaybackRequestedMode mode)
    {
        var plan = PlaybackPreparationPlan.Build(probe, mode);
        if (!plan.CanPrepare || plan.Kind is null)
        {
            return new PlaybackOption(
                PlaybackOptionAvailability.Unsupported,
                plan.Message);
        }

        var message = plan.Kind.Value switch
        {
            PlaybackPreparationKind.CompatibleRemux =>
                "Instant MP4 remux; video is copied without re-encoding.",
            PlaybackPreparationKind.DeviceHevcRemux =>
                "Instant HEVC MP4 stream for device decoding.",
            PlaybackPreparationKind.ServerH264Transcode =>
                "Instant H.264 server transcode.",
            _ => "Instant playback stream."
        };

        return new PlaybackOption(
            PlaybackOptionAvailability.Ready,
            message,
            UsesLiveStream: true);
    }

    private async Task<MediaRow?> GetMediaRowAsync(
        Guid episodeId,
        CancellationToken cancellationToken) =>
        await db.MediaFiles
            .AsNoTracking()
            .Where(x => x.EpisodeId == episodeId)
            .OrderBy(x => x.Path)
            .Select(x => new MediaRow(
                x.Id,
                x.Path,
                x.SizeBytes,
                x.LastWriteTimeUtc))
            .FirstOrDefaultAsync(cancellationToken);

    private static bool IsUniversalDirect(string path, PlaybackProbeResult probe) =>
        MediaPlaybackCompatibility.Evaluate(
                PlaybackClientProfiles.BrowserBaseline,
                new MediaPlaybackCharacteristics(
                    MediaContainers.FromPath(path),
                    probe.VideoCodec,
                    null,
                    probe.PixelFormat,
                    probe.AudioCodec))
            .CanDirectPlay;

    private static bool IsHevc(string? codec) =>
        string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(codec, "h265", StringComparison.OrdinalIgnoreCase);

    private static PlaybackStream SourceStream(
        string path,
        double? durationSeconds) =>
        new(
            path,
            PlaybackMediaTypes.GetContentType(path),
            new DateTimeOffset(File.GetLastWriteTimeUtc(path)),
            null,
            durationSeconds);

    private sealed record MediaRow(
        Guid Id,
        string Path,
        long SizeBytes,
        DateTime LastWriteTimeUtc);
}
