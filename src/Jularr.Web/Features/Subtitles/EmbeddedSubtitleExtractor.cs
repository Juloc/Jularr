using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Jularr.Web.Features.Library;
using Jularr.Web.Infrastructure;

namespace Jularr.Web.Features.Subtitles;

public sealed record EmbeddedSubtitleContent(
    string SourceKey,
    string Format,
    string Content);

public enum AudioTranscriptionStatus
{
    None,
    Queued,
    Processing,
    Ready,
    Failed
}

public sealed record AudioTranscriptionState(
    AudioTranscriptionStatus Status,
    string? Message = null);

// Stream metadata comes from the canonical media inventory; this class only runs ffmpeg extraction
// and Whisper transcription for a stream the inventory selected.
public sealed class EmbeddedSubtitleExtractor(
    MediaProcessRunner processRunner,
    MediaInventoryService mediaInventory,
    ILogger<EmbeddedSubtitleExtractor> logger)
{
    public const string SourcePrefix = "embedded:";
    public const string TranscriptionSourcePrefix = "transcribed:";

    private const string JapaneseLanguageTag = "ja";

    private const string TranscriptionRoot = "/data/transcription-cache";
    private const string WhisperModelPath = "/data/whisper/ggml-small-q5_1.bin";
    private const string WhisperModelUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin";
    private const string WhisperModelSha256 =
        "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb";

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan LongProcessTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, AudioTranscriptionState> transcriptionStates =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim transcriptionGate = new(1, 1);

    /// <summary>
    /// Extracts the preferred embedded text-subtitle stream whose language tag
    /// (or, absent one, title) matches <paramref name="targetLanguageTag"/> via
    /// <see cref="SubtitleLanguageAliases"/>, instead of assuming Japanese.
    /// </summary>
    public async Task<EmbeddedSubtitleContent?> ExtractPreferredTextAsync(
        string mediaPath,
        string targetLanguageTag,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(mediaPath);
        var technical = await ReadTechnicalInfoAsync(fullPath, cancellationToken);
        var stream = SelectPreferredTextStream(technical?.SubtitleStreams ?? [], targetLanguageTag);

        return stream is null
            ? null
            : await ExtractTextStreamAsync(fullPath, stream, cancellationToken);
    }

    public async Task<EmbeddedSubtitleContent?> ExtractTextStreamAsync(
        string mediaPath,
        int streamIndex,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(mediaPath);
        var technical = await ReadTechnicalInfoAsync(fullPath, cancellationToken);
        var stream = technical?.SubtitleStreams
            .SingleOrDefault(x => x.Index == streamIndex && x.IsText);

        return stream is null
            ? null
            : await ExtractTextStreamAsync(fullPath, stream, cancellationToken);
    }

    private async Task<MediaTechnicalInfo?> ReadTechnicalInfoAsync(
        string fullPath,
        CancellationToken cancellationToken) =>
        (await mediaInventory.EnsureAnalyzedAsync(fullPath, cancellationToken))?.Technical;

    private async Task<EmbeddedSubtitleContent?> ExtractTextStreamAsync(
        string fullPath,
        MediaStreamInfo stream,
        CancellationToken cancellationToken)
    {
        var format = stream.Codec is "ass" or "ssa" ? "ass" : "srt";
        var extraction = await processRunner.RunAsync(
            "ffmpeg",
            [
                "-v", "error",
                "-nostdin",
                "-i", fullPath,
                "-map", $"0:{stream.Index}",
                "-c:s", stream.Codec == "ass" ? "copy" : format,
                "-f", format,
                "pipe:1"
            ],
            ProcessTimeout,
            cancellationToken);

        if (extraction is null || extraction.ExitCode != 0)
        {
            if (extraction is not null)
            {
                logger.LogWarning(
                    "ffmpeg could not extract subtitle stream {StreamIndex} from {MediaPath}: {Error}",
                    stream.Index,
                    fullPath,
                    extraction.ErrorSummary);
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(extraction.Output))
        {
            return null;
        }

        return new EmbeddedSubtitleContent(
            BuildSourceKey(fullPath, stream.Index),
            format,
            extraction.Output);
    }

    public AudioTranscriptionState GetAudioTranscriptionState(string mediaPath)
    {
        var fullPath = Path.GetFullPath(mediaPath);
        return transcriptionStates.TryGetValue(fullPath, out var state)
            ? state
            : new AudioTranscriptionState(AudioTranscriptionStatus.None);
    }

    public bool TryQueueAudioTranscription(string mediaPath)
    {
        var fullPath = Path.GetFullPath(mediaPath);
        var queued = new AudioTranscriptionState(AudioTranscriptionStatus.Queued);

        while (true)
        {
            var current = GetAudioTranscriptionState(fullPath);
            if (current.Status is AudioTranscriptionStatus.Queued
                or AudioTranscriptionStatus.Processing
                or AudioTranscriptionStatus.Ready)
            {
                return false;
            }

            if (current.Status == AudioTranscriptionStatus.None)
            {
                if (transcriptionStates.TryAdd(fullPath, queued))
                {
                    return true;
                }

                continue;
            }

            if (transcriptionStates.TryUpdate(fullPath, queued, current))
            {
                return true;
            }
        }
    }

    public void MarkAudioTranscriptionProcessing(string mediaPath) =>
        transcriptionStates[Path.GetFullPath(mediaPath)] =
            new AudioTranscriptionState(AudioTranscriptionStatus.Processing);

    public void MarkAudioTranscriptionReady(string mediaPath) =>
        transcriptionStates[Path.GetFullPath(mediaPath)] =
            new AudioTranscriptionState(AudioTranscriptionStatus.Ready);

    public void MarkAudioTranscriptionFailed(string mediaPath, string message) =>
        transcriptionStates[Path.GetFullPath(mediaPath)] =
            new AudioTranscriptionState(AudioTranscriptionStatus.Failed, message);

    public async Task<EmbeddedSubtitleContent?> TranscribeJapaneseAudioAsync(
        string mediaPath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(mediaPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            MarkAudioTranscriptionFailed(fullPath, "Media file no longer exists.");
            return null;
        }

        await transcriptionGate.WaitAsync(cancellationToken);
        try
        {
            MarkAudioTranscriptionProcessing(fullPath);

            Directory.CreateDirectory(TranscriptionRoot);
            var cacheKey = BuildTranscriptionCacheKey(
                fullPath,
                info.Length,
                info.LastWriteTimeUtc);
            var outputPrefix = Path.Combine(TranscriptionRoot, cacheKey);
            var srtPath = outputPrefix + ".srt";

            if (File.Exists(srtPath) && new FileInfo(srtPath).Length > 0)
            {
                var cached = await File.ReadAllTextAsync(srtPath, cancellationToken);
                MarkAudioTranscriptionReady(fullPath);
                return new EmbeddedSubtitleContent(
                    BuildTranscriptionSourceKey(fullPath),
                    "srt",
                    cached);
            }

            if (!await EnsureWhisperModelAsync(cancellationToken))
            {
                MarkAudioTranscriptionFailed(
                    fullPath,
                    "Could not download or verify the local Whisper model.");
                return null;
            }

            var technical = await ReadTechnicalInfoAsync(fullPath, cancellationToken);
            var audioStreamIndex = SelectPreferredAudioStreamIndex(
                technical?.AudioStreams ?? [],
                JapaneseLanguageTag);
            if (audioStreamIndex is null)
            {
                MarkAudioTranscriptionFailed(fullPath, "No audio stream was found.");
                return null;
            }

            var wavPath = outputPrefix + ".input.wav";
            TryDelete(wavPath);
            TryDelete(srtPath);

            try
            {
                var extraction = await processRunner.RunAsync(
                    "ffmpeg",
                    [
                        "-v", "error",
                        "-nostdin",
                        "-y",
                        "-i", fullPath,
                        "-map", $"0:{audioStreamIndex.Value}",
                        "-vn",
                        "-ac", "1",
                        "-ar", "16000",
                        "-c:a", "pcm_s16le",
                        wavPath
                    ],
                    LongProcessTimeout,
                    cancellationToken);

                if (extraction is null ||
                    extraction.ExitCode != 0 ||
                    !File.Exists(wavPath) ||
                    new FileInfo(wavPath).Length == 0)
                {
                    MarkAudioTranscriptionFailed(
                        fullPath,
                        "Could not extract audio for transcription.");
                    return null;
                }

                var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 2);
                var transcription = await processRunner.RunAsync(
                    "whisper-cli",
                    [
                        "-m", WhisperModelPath,
                        "-f", wavPath,
                        "-l", "ja",
                        "-t", threads.ToString(),
                        "-osrt",
                        "-of", outputPrefix,
                        "-np"
                    ],
                    LongProcessTimeout,
                    cancellationToken);

                if (transcription is null ||
                    transcription.ExitCode != 0 ||
                    !File.Exists(srtPath) ||
                    new FileInfo(srtPath).Length == 0)
                {
                    if (transcription is not null)
                    {
                        logger.LogWarning(
                            "whisper.cpp failed for {MediaPath}: {Error}",
                            fullPath,
                            transcription.ErrorSummary);
                    }

                    TryDelete(srtPath);
                    MarkAudioTranscriptionFailed(
                        fullPath,
                        "Japanese audio transcription failed.");
                    return null;
                }

                var content = await File.ReadAllTextAsync(srtPath, cancellationToken);
                if (string.IsNullOrWhiteSpace(content))
                {
                    TryDelete(srtPath);
                    MarkAudioTranscriptionFailed(
                        fullPath,
                        "Japanese audio transcription returned no text.");
                    return null;
                }

                MarkAudioTranscriptionReady(fullPath);
                return new EmbeddedSubtitleContent(
                    BuildTranscriptionSourceKey(fullPath),
                    "srt",
                    content);
            }
            finally
            {
                TryDelete(wavPath);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(
                exception,
                "Could not transcribe Japanese audio for {MediaPath}.",
                fullPath);
            MarkAudioTranscriptionFailed(
                fullPath,
                "Could not write the transcription cache.");
            return null;
        }
        finally
        {
            transcriptionGate.Release();
        }
    }

    public static string BuildTranscriptionSourceKey(string mediaPath) =>
        $"{TranscriptionSourcePrefix}{Path.GetFullPath(mediaPath)}#audio=ja";

    public static string BuildTranscriptionCacheKey(
        string mediaPath,
        long sizeBytes,
        DateTime sourceUpdatedAt)
    {
        var fingerprint =
            $"{Path.GetFullPath(mediaPath)}\n{sizeBytes}\n{sourceUpdatedAt.ToUniversalTime().Ticks}";
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))
            .ToLowerInvariant();
    }

    // Prefers a stream tagged for the target language and otherwise uses the first audio stream.
    public static int? SelectPreferredAudioStreamIndex(
        IEnumerable<MediaStreamInfo> audioStreams,
        string targetLanguageTag)
    {
        var ordered = audioStreams.OrderBy(x => x.Index).ToArray();
        return (ordered.FirstOrDefault(x => MatchesLanguage(x.Language, x.Title, targetLanguageTag))
                ?? ordered.FirstOrDefault())
            ?.Index;
    }

    /// <summary>
    /// True when a stream's language tag (or, absent one, its free-form title)
    /// names <paramref name="targetLanguageTag"/>, via <see cref="SubtitleLanguageAliases"/>
    /// instead of a hardcoded Japanese check.
    /// </summary>
    public static bool MatchesLanguage(string? language, string? title, string targetLanguageTag)
    {
        var normalizedLanguage = language?.Trim();

        if (!string.IsNullOrWhiteSpace(normalizedLanguage) &&
            !normalizedLanguage.Equals("und", StringComparison.OrdinalIgnoreCase))
        {
            return SubtitleLanguageAliases.HasToken([normalizedLanguage], targetLanguageTag);
        }

        return title is not null &&
            SubtitleLanguageAliases.NameTokensFor(targetLanguageTag)
                .Any(token => title.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> EnsureWhisperModelAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(WhisperModelPath) &&
            await HasExpectedWhisperModelHashAsync(WhisperModelPath, cancellationToken))
        {
            return true;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(WhisperModelPath)!);
        var temporaryPath = WhisperModelPath + ".download";
        TryDelete(temporaryPath);

        var download = await processRunner.RunAsync(
            "curl",
            [
                "-fL",
                "--retry", "5",
                "--retry-delay", "2",
                "--retry-all-errors",
                "-o", temporaryPath,
                WhisperModelUrl
            ],
            DownloadTimeout,
            cancellationToken);

        if (download is null ||
            download.ExitCode != 0 ||
            !File.Exists(temporaryPath) ||
            !await HasExpectedWhisperModelHashAsync(temporaryPath, cancellationToken))
        {
            TryDelete(temporaryPath);
            return false;
        }

        File.Move(temporaryPath, WhisperModelPath, overwrite: true);
        return true;
    }

    private static async Task<bool> HasExpectedWhisperModelHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return string.Equals(
            Convert.ToHexString(hash),
            WhisperModelSha256,
            StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Prefers the embedded text-subtitle stream tagged (or titled) for
    /// <paramref name="targetLanguageTag"/> over untagged/other-language ones,
    /// then a full dialogue track over forced/signs-only, the default track,
    /// and finally stream order - the same preference Japanese always used,
    /// generalized to any target language.
    /// </summary>
    public static MediaStreamInfo? SelectPreferredTextStream(
        IEnumerable<MediaStreamInfo> subtitleStreams,
        string targetLanguageTag) =>
        subtitleStreams
            .Where(x => x.IsText && MatchesLanguage(x.Language, x.Title, targetLanguageTag))
            .OrderBy(x => x.IsForced)
            .ThenBy(x => LooksLikeSignsOrSongs(x.Title))
            .ThenByDescending(x => x.IsDefault)
            .ThenBy(x => x.Index)
            .FirstOrDefault();

    public static string BuildSourceKey(string mediaPath, int streamIndex) =>
        $"{BuildSourcePrefix(mediaPath)}{streamIndex}";

    public static string BuildSourcePrefix(string mediaPath) =>
        $"{SourcePrefix}{Path.GetFullPath(mediaPath)}#stream=";

    private static bool LooksLikeSignsOrSongs(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        return title.Contains("sign", StringComparison.OrdinalIgnoreCase)
            || title.Contains("song", StringComparison.OrdinalIgnoreCase)
            || title.Contains("karaoke", StringComparison.OrdinalIgnoreCase)
            || title.Contains("forced", StringComparison.OrdinalIgnoreCase);
    }
}
