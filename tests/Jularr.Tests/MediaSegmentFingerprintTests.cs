using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaSegments;
using Jularr.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jularr.Tests;

// Cross-episode audio fingerprint OP/ED detection (#135 follow-up). AudioFingerprint and
// AudioFingerprintPolicy are pure and tested directly with synthetic PCM/hashes; the detector
// itself is tested through IAudioWindowDecoder fakes so these tests never need a real ffmpeg.
[TestClass]
public sealed class MediaSegmentFingerprintTests
{
    [TestMethod]
    public void PolicyFindsTheSharedWindowAcrossSiblingsAndScoresConfidenceBySupport()
    {
        // A ~25 s "OP" burst, bit-identical across three fake episodes, inserted at a different
        // offset in each one; everything else is per-episode noise so it must not match. Offsets
        // are exact multiples of the frame hop (128 ms) so an integer frame shift aligns them
        // perfectly, the same way a real encoder-accurate cut would; AudioFingerprint's Hamming
        // tolerance (not this test) is what has to absorb any real-world sub-frame drift.
        var own = SyntheticEpisode(seed: 1, insertOffsetSeconds: 40 * AudioFingerprint.FrameHopSeconds, sharedDurationSeconds: 25);
        var siblingA = SyntheticEpisode(seed: 2, insertOffsetSeconds: 94 * AudioFingerprint.FrameHopSeconds, sharedDurationSeconds: 25);
        var siblingB = SyntheticEpisode(seed: 3, insertOffsetSeconds: 8 * AudioFingerprint.FrameHopSeconds, sharedDurationSeconds: 25);

        var ownHashes = AudioFingerprint.ComputeFrameHashes(own);
        var oneSibling = AudioFingerprintPolicy.Evaluate(
            MediaSegmentKind.Intro, ownHashes, windowStartSeconds: 0, [AudioFingerprint.ComputeFrameHashes(siblingA)]);
        var twoSiblings = AudioFingerprintPolicy.Evaluate(
            MediaSegmentKind.Intro,
            ownHashes,
            windowStartSeconds: 0,
            [AudioFingerprint.ComputeFrameHashes(siblingA), AudioFingerprint.ComputeFrameHashes(siblingB)]);

        Assert.IsNotNull(oneSibling, "A single corroborating sibling is enough to report a marker.");
        Assert.IsNotNull(twoSiblings);
        Assert.AreEqual(MediaSegmentKind.Intro, twoSiblings!.Kind);

        // The recovered window should land close to where the shared burst was actually inserted
        // in the "own" episode (frame-hop granularity, ~128 ms, is the expected slack).
        Assert.AreEqual(5_120, twoSiblings.StartMs, 400);
        Assert.AreEqual(30_120, twoSiblings.EndMs, 400);

        Assert.IsTrue(
            twoSiblings.Confidence > oneSibling!.Confidence,
            "A second corroborating episode (three total) should raise confidence over a single sibling.");
        Assert.IsTrue(twoSiblings.Confidence >= 0.8, "An exact repeated segment across three episodes is highly confident.");
    }

    [TestMethod]
    public void PolicyRejectsASharedBurstShorterThanTwentySeconds()
    {
        var own = SyntheticEpisode(seed: 10, insertOffsetSeconds: 24 * AudioFingerprint.FrameHopSeconds, sharedDurationSeconds: 8);
        var sibling = SyntheticEpisode(seed: 11, insertOffsetSeconds: 47 * AudioFingerprint.FrameHopSeconds, sharedDurationSeconds: 8);

        var result = AudioFingerprintPolicy.Evaluate(
            MediaSegmentKind.Outro,
            AudioFingerprint.ComputeFrameHashes(own),
            windowStartSeconds: 0,
            [AudioFingerprint.ComputeFrameHashes(sibling)]);

        Assert.IsNull(result, "An 8 s shared burst is well under the 20 s floor and must not become a marker.");
    }

    [TestMethod]
    public void PolicyRejectsAMatchLongerThanThreeMinutes()
    {
        // Operates directly on hash sequences: an identical run far longer than the 180 s cap,
        // surrounded by mismatching frames so the run boundaries are unambiguous.
        var hopSeconds = AudioFingerprint.FrameHopSeconds;
        var longRunFrames = (int)Math.Ceiling(200 / hopSeconds); // > 180 s
        var padding = 20;

        var a = new uint[padding + longRunFrames + padding];
        var b = new uint[a.Length];
        var random = new Random(42);
        for (var i = 0; i < a.Length; i++)
        {
            var shared = i >= padding && i < padding + longRunFrames;
            // The shared span varies by position (not a constant) so a shifted, partial overlap
            // of it cannot masquerade as a shorter valid match; only the true (unshifted)
            // alignment lines the two sequences up.
            a[i] = shared ? SharedRunValue(i - padding) : (uint)random.Next();
            b[i] = shared ? SharedRunValue(i - padding) : (uint)random.Next();
        }

        var match = AudioFingerprint.FindBestMatch(
            a, b, AudioFingerprintPolicy.MaxHammingDistance, AudioFingerprintPolicy.MinMatchRatio,
            minRunFrames: 1, maxRunFrames: a.Length);
        Assert.IsNotNull(match, "Sanity check: the raw matcher does find the long run.");

        var result = AudioFingerprintPolicy.Evaluate(MediaSegmentKind.Intro, a, 0, [b]);
        Assert.IsNull(result, "A 200 s shared run exceeds the 3-minute cap and must be rejected.");
    }

    [TestMethod]
    public async Task DetectorSkipsUnchangedEpisodesAndNeverOutranksManualMarkers()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var episode1 = await fixture.AddMediaAsync("Show - S01E01.mkv", [1]);
        var episode2 = await fixture.AddMediaAsync("Show - S01E02.mkv", [2]);
        var episode3 = await fixture.AddMediaAsync("Show - S01E03.mkv", [3]);

        const double durationSeconds = 90;
        fixture.Runner.Returns(episode1.Path, ProbeWithDuration(durationSeconds));
        fixture.Runner.Returns(episode2.Path, ProbeWithDuration(durationSeconds));
        fixture.Runner.Returns(episode3.Path, ProbeWithDuration(durationSeconds));
        await fixture.Inventory.ReconcileAsync(fixture.Root.Id, CancellationToken.None);

        // Offsets are exact multiples of the 128 ms frame hop (see the policy test above for why).
        var hop = AudioFingerprint.FrameHopSeconds;
        var decoder = new FakeAudioWindowDecoder();
        decoder.Configure(episode1.Path, introInsertOffsetSeconds: 40 * hop, introSharedSeconds: 25, outroInsertOffsetSeconds: 31 * hop, outroSharedSeconds: 22, seed: 1);
        decoder.Configure(episode2.Path, introInsertOffsetSeconds: 94 * hop, introSharedSeconds: 25, outroInsertOffsetSeconds: 70 * hop, outroSharedSeconds: 22, seed: 2);
        decoder.Configure(episode3.Path, introInsertOffsetSeconds: 8 * hop, introSharedSeconds: 25, outroInsertOffsetSeconds: 16 * hop, outroSharedSeconds: 22, seed: 3);

        var detector = new AudioFingerprintMediaSegmentDetector(
            decoder,
            NullLogger<AudioFingerprintMediaSegmentDetector>.Instance,
            cacheRoot: Path.Combine(fixture.TempRoot, "fingerprints"),
            introWindow: TimeSpan.FromSeconds(45),
            outroWindow: TimeSpan.FromSeconds(45));

        var trickplay = new TrickplayGenerator(
            new MediaProcessRunner(NullLogger<MediaProcessRunner>.Instance),
            new BackgroundJobQueue(BuildScopeFactory(fixture)),
            NullLogger<TrickplayGenerator>.Instance,
            Path.Combine(fixture.TempRoot, "trickplay"),
            ffmpegExecutable: "jularr-missing-ffmpeg");
        var service = new MediaSegmentService(fixture.Db, Options.Create(new MediaSegmentOptions()), detector, trickplay);

        var run = await service.RunDetectorAsync(episode1.EpisodeId!.Value, force: false, CancellationToken.None);
        Assert.AreEqual(SegmentDetectionOutcome.Completed, run.Outcome);
        Assert.AreEqual(2, run.SegmentCount, "Both a shared intro and a shared outro should be found across the season.");

        var stored = await fixture.Db.EpisodeMediaSegments
            .AsNoTracking()
            .Where(x => x.EpisodeId == episode1.EpisodeId!.Value && x.Source == MediaSegmentSource.Detector)
            .ToListAsync();
        Assert.AreEqual(2, stored.Count);
        var intro = stored.Single(x => x.Kind == MediaSegmentKind.Intro);
        Assert.AreEqual(5_120, intro.StartMs, 400);
        Assert.IsTrue(intro.Confidence >= 0.8);

        var decodeCallsAfterFirstRun = decoder.CallCount;
        Assert.IsTrue(decodeCallsAfterFirstRun > 0);

        var again = await service.RunDetectorAsync(episode1.EpisodeId!.Value, force: false, CancellationToken.None);
        Assert.AreEqual(SegmentDetectionOutcome.Skipped, again.Outcome, "Unchanged media identity and detector version must skip re-analysis.");
        Assert.AreEqual(decodeCallsAfterFirstRun, decoder.CallCount, "A skipped run must not decode audio again.");

        // A manual correction always wins, even though a detector row exists for the same kind.
        await service.SaveManualAsync(episode1.EpisodeId!.Value, MediaSegmentKind.Intro, 6_000, 32_000, CancellationToken.None);
        var resolved = await service.GetSegmentsAsync(episode1.EpisodeId!.Value, CancellationToken.None);
        var resolvedIntro = resolved.Segments.Single(x => x.Kind == MediaSegmentKind.Intro);
        Assert.AreEqual(MediaSegmentSource.Manual, resolvedIntro.Source);
        Assert.AreEqual(32_000, resolvedIntro.EndMs);

        var forcedAfterManual = await service.RunDetectorAsync(episode1.EpisodeId!.Value, force: true, CancellationToken.None);
        Assert.AreEqual(SegmentDetectionOutcome.Completed, forcedAfterManual.Outcome);
        var stillResolved = await service.GetSegmentsAsync(episode1.EpisodeId!.Value, CancellationToken.None);
        Assert.AreEqual(
            MediaSegmentSource.Manual,
            stillResolved.Segments.Single(x => x.Kind == MediaSegmentKind.Intro).Source,
            "A rebuilt detector marker never outranks a manual correction.");
    }

    [TestMethod]
    public async Task DetectorDegradesCleanlyWithoutSiblingsOrFfmpeg()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var episode = await fixture.AddMediaAsync("Solo - S01E01.mkv", [9]);
        fixture.Runner.Returns(episode.Path, ProbeWithDuration(90));
        await fixture.Inventory.ReconcileAsync(fixture.Root.Id, CancellationToken.None);

        // No siblings analysed yet: nothing to corroborate against.
        var decoder = new FakeAudioWindowDecoder();
        decoder.Configure(episode.Path, 5, 25, 4, 22, seed: 1);
        var lonelyDetector = new AudioFingerprintMediaSegmentDetector(
            decoder, NullLogger<AudioFingerprintMediaSegmentDetector>.Instance,
            cacheRoot: Path.Combine(fixture.TempRoot, "fp-lonely"));
        var trickplay = new TrickplayGenerator(
            new MediaProcessRunner(NullLogger<MediaProcessRunner>.Instance),
            new BackgroundJobQueue(BuildScopeFactory(fixture)),
            NullLogger<TrickplayGenerator>.Instance,
            Path.Combine(fixture.TempRoot, "trickplay-lonely"),
            ffmpegExecutable: "jularr-missing-ffmpeg");
        var lonelyService = new MediaSegmentService(fixture.Db, Options.Create(new MediaSegmentOptions()), lonelyDetector, trickplay);

        var lonelyRun = await lonelyService.RunDetectorAsync(episode.EpisodeId!.Value, force: false, CancellationToken.None);
        Assert.AreEqual(SegmentDetectionOutcome.Completed, lonelyRun.Outcome);
        Assert.AreEqual(0, lonelyRun.SegmentCount, "With no siblings there is nothing to corroborate a marker with.");

        // ffmpeg unavailable: the real decoder must degrade to "no fingerprint" without throwing.
        var missingFfmpegDecoder = new FfmpegAudioWindowDecoder(
            new MediaProcessRunner(NullLogger<MediaProcessRunner>.Instance),
            NullLogger<FfmpegAudioWindowDecoder>.Instance,
            ffmpegExecutable: "jularr-missing-ffmpeg");
        var dummyMediaPath = Path.Combine(fixture.TempRoot, "dummy.mkv");
        await File.WriteAllBytesAsync(dummyMediaPath, [0, 1, 2, 3]);
        Assert.IsNull(await missingFfmpegDecoder.DecodeAsync(dummyMediaPath, 0, 10, CancellationToken.None));

        var degradedDetector = new AudioFingerprintMediaSegmentDetector(
            missingFfmpegDecoder,
            NullLogger<AudioFingerprintMediaSegmentDetector>.Instance,
            cacheRoot: Path.Combine(fixture.TempRoot, "fp-degraded"));
        var degraded = await degradedDetector.DetectAsync(
            new MediaSegmentDetectionRequest(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, dummyMediaPath, "identity", 1440, []),
            CancellationToken.None);
        Assert.AreEqual(0, degraded.Count, "Detection degrades to no markers, never an exception, when ffmpeg is unavailable.");
    }

    [TestMethod]
    public async Task SeasonDetectionQueueIsBoundedAndQueuesOneOperationPerSeason()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var scopeFactory = BuildScopeFactory(fixture);
        var jobs = new BackgroundJobQueue(scopeFactory);
        var queue = new SeasonSegmentDetectionQueue(jobs, NullLogger<SeasonSegmentDetectionQueue>.Instance);
        var animeId = Guid.NewGuid();

        Assert.IsTrue(await queue.EnsureQueuedAsync(animeId, 1, "Show · Season 1", CancellationToken.None));
        Assert.IsTrue(queue.IsPending(animeId, 1));
        Assert.IsFalse(
            await queue.EnsureQueuedAsync(animeId, 1, "Show · Season 1", CancellationToken.None),
            "A season already queued is not queued twice.");

        var operationCount = await fixture.Db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM \"Operations\" WHERE \"Kind\" = {SeasonSegmentDetectionQueue.OperationKind}")
            .SingleAsync();
        Assert.AreEqual(1, operationCount);
    }

    private static IServiceScopeFactory BuildScopeFactory(MediaInventoryFixture fixture)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(fixture.Options));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static string ProbeWithDuration(double durationSeconds) =>
        $$"""
        {
          "streams": [
            { "index": 0, "codec_name": "h264", "codec_type": "video", "width": 1280, "height": 720, "pix_fmt": "yuv420p", "disposition": { "default": 1, "forced": 0 } },
            { "index": 1, "codec_name": "aac", "codec_type": "audio", "channels": 2, "channel_layout": "stereo", "disposition": { "default": 1, "forced": 0 } }
          ],
          "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "{{durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}}" }
        }
        """;

    // Builds `lengthSeconds` of mono PCM at AudioFingerprint.SampleRateHz: per-episode noise
    // (seeded, so distinct episodes never coincidentally correlate) with a bit-identical tone
    // burst inserted at [insertOffsetSeconds, insertOffsetSeconds + sharedDurationSeconds).
    private static short[] SyntheticEpisode(
        int seed,
        double insertOffsetSeconds,
        double sharedDurationSeconds,
        double lengthSeconds = 40)
    {
        var sampleRate = AudioFingerprint.SampleRateHz;
        var samples = new short[(int)Math.Round(lengthSeconds * sampleRate)];
        var random = new Random(seed);
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)sampleRate;
            samples[i] = t >= insertOffsetSeconds && t < insertOffsetSeconds + sharedDurationSeconds
                ? SharedToneSample(t - insertOffsetSeconds)
                : (short)random.Next(-2000, 2000);
        }

        return samples;
    }

    // A simple multiplicative hash: deterministic and varies with position, unlike a fixed
    // constant, so a shifted (misaligned) overlap of the run does not also look like a match.
    private static uint SharedRunValue(int position) =>
        (uint)position * 2654435761u ^ 0x9E3779B9u;

    private static short SharedToneSample(double tInShared)
    {
        var value = 9000 * Math.Sin(2 * Math.PI * 300 * tInShared) + 5000 * Math.Sin(2 * Math.PI * 711 * tInShared);
        return (short)Math.Clamp(value, short.MinValue, short.MaxValue);
    }

    // Fakes ffmpeg decode entirely: generates the same kind of per-episode-noise-plus-shared-tone
    // PCM as SyntheticEpisode, but through the IAudioWindowDecoder seam the real detector uses, so
    // AudioFingerprintMediaSegmentDetector can be exercised end to end without ffmpeg.
    private sealed class FakeAudioWindowDecoder : IAudioWindowDecoder
    {
        private readonly Dictionary<string, EpisodeAudio> episodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> callIndexByPath = new(StringComparer.Ordinal);

        public int CallCount { get; private set; }

        public void Configure(
            string mediaPath,
            double introInsertOffsetSeconds,
            double introSharedSeconds,
            double outroInsertOffsetSeconds,
            double outroSharedSeconds,
            int seed) =>
            episodes[mediaPath] = new EpisodeAudio(
                introInsertOffsetSeconds, introSharedSeconds, outroInsertOffsetSeconds, outroSharedSeconds, seed);

        public Task<short[]?> DecodeAsync(
            string mediaPath, double startSeconds, double lengthSeconds, CancellationToken cancellationToken)
        {
            CallCount++;
            if (!episodes.TryGetValue(mediaPath, out var audio))
            {
                return Task.FromResult<short[]?>(null);
            }

            // The detector always decodes the intro window before the outro window per episode.
            var callIndex = callIndexByPath.GetValueOrDefault(mediaPath);
            callIndexByPath[mediaPath] = callIndex + 1;
            var (insertOffset, sharedDuration) = callIndex == 0
                ? (audio.IntroInsertOffsetSeconds, audio.IntroSharedSeconds)
                : (audio.OutroInsertOffsetSeconds, audio.OutroSharedSeconds);

            var sampleRate = AudioFingerprint.SampleRateHz;
            var samples = new short[(int)Math.Round(lengthSeconds * sampleRate)];
            var random = new Random(audio.Seed);
            for (var i = 0; i < samples.Length; i++)
            {
                var t = i / (double)sampleRate;
                samples[i] = t >= insertOffset && t < insertOffset + sharedDuration
                    ? SharedToneSample(t - insertOffset)
                    : (short)random.Next(-2000, 2000);
            }

            return Task.FromResult<short[]?>(samples);
        }

        private sealed record EpisodeAudio(
            double IntroInsertOffsetSeconds,
            double IntroSharedSeconds,
            double OutroInsertOffsetSeconds,
            double OutroSharedSeconds,
            int Seed);
    }
}
