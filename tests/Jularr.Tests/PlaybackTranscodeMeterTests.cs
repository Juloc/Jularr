using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Playback.Transcoding;
using static Jularr.Tests.PlaybackTestPlans;

namespace Jularr.Tests;

/// <summary>
/// The measured speed of a transcode: ffmpeg's <c>-progress</c> output parsed from canned text, the warm-up and the 1.0x / 1.15x
/// thresholds on a clock the test moves. No real ffmpeg runs anywhere here.
/// </summary>
[TestClass]
public sealed class PlaybackTranscodeMeterTests
{
    private static readonly DateTimeOffset s_start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private const string CannedBlock = """
        frame=120
        fps=48.50
        stream_0_0_q=-1.0
        bitrate=N/A
        total_size=N/A
        out_time_us=5000000
        out_time_ms=5000000
        out_time=00:00:05.000000
        dup_frames=0
        drop_frames=0
        speed=1.94x
        progress=continue
        """;

    private static List<PlaybackTranscodeSample> Parse(string output, out List<string> otherLines)
    {
        var parser = new FfmpegProgressParser();
        var samples = new List<PlaybackTranscodeSample>();
        otherLines = [];
        foreach (var line in output.Split('\n'))
        {
            if (!parser.TryFeed(line.TrimEnd('\r'), out var sample))
            {
                otherLines.Add(line);
            }
            else if (sample is not null)
            {
                samples.Add(sample);
            }
        }

        return samples;
    }

    private static PlaybackTranscodeSample Sample(double? speed, double? seconds = 30, double? fps = 40) => new(speed, fps, seconds);

    [TestMethod]
    public void ACannedProgressBlockBecomesOneSampleAndNothingElseOnThePipeIsConsumed()
    {
        var samples = Parse("Error opening input: No such file\n" + CannedBlock + "\n[h264 @ 0x55] error while decoding MB 3 4\n", out var others);

        var sample = samples.Single();
        Assert.AreEqual(1.94, sample.Speed);
        Assert.AreEqual(48.5, sample.Fps);
        Assert.AreEqual(5.0, sample.OutputSeconds);
        CollectionAssert.AreEqual(new[] { "Error opening input: No such file", "[h264 @ 0x55] error while decoding MB 3 4", "" }, others, "Diagnostic lines stay with the caller.");
    }

    [TestMethod]
    public void GarbagePartialAndNotAvailableValuesNeverThrowAndNeverInventNumbers()
    {
        var block = "speed=N/A\nfps=abc\nout_time_us=N/A\nout_time=N/A\nstream_9_12_q=28.0\nspeed=-3x\nprogress=continue\n";
        var sample = Parse(block, out var others).Single();

        Assert.IsNull(sample.Speed, "A negative or unreadable speed is absent.");
        Assert.IsNull(sample.Fps);
        Assert.IsNull(sample.OutputSeconds);
        Assert.AreEqual(1, others.Count, "Only the trailing empty line is left over; every key=value line above belongs to the protocol.");

        Assert.IsNull(Parse("speed=1e999x\nout_time=-00:00:00.04\nprogress=end\n", out _).Single().Speed, "A value no encoder reports is not a speed.");
        Assert.IsNull(Parse("out_time=-00:00:00.04\nprogress=end\n", out _).Single().OutputSeconds, "A negative media time is startup noise.");

        var parser = new FfmpegProgressParser();
        foreach (var junk in new[] { "", "=", "=5", "speed", "no equals here", "frame =", "progress", "\u0000\u0001=2", new string('x', 10_000) })
        {
            Assert.IsFalse(parser.TryFeed(junk, out var none) && none is not null, $"'{junk.Length}' junk produced a sample.");
        }
    }

    [TestMethod]
    public void ABlockCutOffBeforeItsEndNeverLeaksIntoTheNextOne()
    {
        var parser = new FfmpegProgressParser();
        Assert.IsTrue(parser.TryFeed("speed=0.40x", out _));
        Assert.IsTrue(parser.TryFeed("out_time_us=1000000", out _));
        Assert.IsTrue(parser.TryFeed("progress=continue", out var first));
        Assert.IsNotNull(first);

        Assert.IsTrue(parser.TryFeed("progress=continue", out var second));

        Assert.IsNull(second!.Speed, "Nothing of the finished block survives it.");
        Assert.IsNull(second.OutputSeconds);
    }

    [TestMethod]
    public void MediaTimeComesFromWhicheverKeyThisFfmpegVersionPrints()
    {
        Assert.AreEqual(12.5, Parse("out_time_ms=12500000\nprogress=continue\n", out _).Single().OutputSeconds, "The millisecond key carries microseconds in ffmpeg.");
        Assert.AreEqual(61.25, Parse("out_time=00:01:01.250000\nprogress=continue\n", out _).Single().OutputSeconds);
        Assert.AreEqual(3.0, Parse("out_time_us=3000000\nout_time=00:09:09.000000\nprogress=end\n", out _).Single().OutputSeconds, "The first readable key wins.");
    }

    [TestMethod]
    public void TheWarmUpNeverCountsAsSlow()
    {
        var clock = new ManualTimeProvider(s_start);
        var meter = new PlaybackTranscodeMeter(clock);
        var run = meter.BeginRun(PlaybackHardwareBackend.Software, judgesSpeed: true);

        run.Record(Sample(0.2, seconds: PlaybackTranscodeMeter.WarmUpOutputSeconds - 0.1));
        clock.Advance(PlaybackTranscodeMeter.SustainedFor * 3);
        run.Record(Sample(0.2, seconds: PlaybackTranscodeMeter.WarmUpOutputSeconds - 0.1));

        Assert.AreEqual(PlaybackTranscodeSpeedState.Measuring, meter.Read().State, "Startup dominates ffmpeg's cumulative speed for the first seconds of output.");
    }

    [TestMethod]
    public void SpeedBelowRealTimeIsTooSlowOnlyOnceItLastedAndTheBoundaryIsExact()
    {
        var clock = new ManualTimeProvider(s_start);
        var meter = new PlaybackTranscodeMeter(clock);
        var run = meter.BeginRun(PlaybackHardwareBackend.Software, judgesSpeed: true);

        run.Record(Sample(0.99));
        Assert.AreEqual(PlaybackTranscodeSpeedState.Measuring, meter.Read().State, "One slow report proves nothing.");
        clock.Advance(PlaybackTranscodeMeter.SustainedFor - TimeSpan.FromSeconds(1));
        run.Record(Sample(0.99));
        Assert.AreEqual(PlaybackTranscodeSpeedState.Measuring, meter.Read().State);
        clock.Advance(TimeSpan.FromSeconds(1));
        run.Record(Sample(0.99));
        var reading = meter.Read();

        Assert.AreEqual(PlaybackTranscodeSpeedState.TooSlow, reading.State);
        Assert.AreEqual(0.99, reading.Speed);
        Assert.AreEqual(40, reading.Fps);
        Assert.AreEqual(PlaybackHardwareBackend.Software, reading.Backend);
    }

    [TestMethod]
    public void ExactlyRealTimeIsNotTooSlowButBelowTheTargetMarginItBlocksRaisingQuality()
    {
        var clock = new ManualTimeProvider(s_start);
        var meter = new PlaybackTranscodeMeter(clock);
        var run = meter.BeginRun(PlaybackHardwareBackend.Qsv, judgesSpeed: true);

        void Hold(double speed)
        {
            run.Record(Sample(speed));
            clock.Advance(PlaybackTranscodeMeter.SustainedFor);
            run.Record(Sample(speed));
        }

        Hold(1.0);
        Assert.AreEqual(PlaybackTranscodeSpeedState.BelowTarget, meter.Read().State, "1.00x keeps real time but not the 1.15x margin.");
        Hold(PlaybackTranscodeMeter.TargetSpeed - 0.001);
        Assert.AreEqual(PlaybackTranscodeSpeedState.BelowTarget, meter.Read().State);
        Hold(PlaybackTranscodeMeter.TargetSpeed);
        Assert.AreEqual(PlaybackTranscodeSpeedState.Sustainable, meter.Read().State, "The target itself is enough.");
    }

    [TestMethod]
    public void ARecoveredSpeedEndsTheDipAndSilenceMeansNothingIsKnown()
    {
        var clock = new ManualTimeProvider(s_start);
        var meter = new PlaybackTranscodeMeter(clock);
        var run = meter.BeginRun(PlaybackHardwareBackend.Software, judgesSpeed: true);

        run.Record(Sample(0.6));
        clock.Advance(TimeSpan.FromSeconds(9));
        run.Record(Sample(1.4));
        clock.Advance(TimeSpan.FromSeconds(9));
        run.Record(Sample(0.6));
        Assert.AreEqual(PlaybackTranscodeSpeedState.Measuring, meter.Read().State, "The dip restarted; the earlier one does not add up.");

        clock.Advance(PlaybackTranscodeMeter.StaleAfter + TimeSpan.FromSeconds(1));
        Assert.AreEqual(PlaybackTranscodeSpeedState.Unknown, meter.Read().State, "A progress pipe that went quiet is not evidence.");

        run.Record(Sample(null));
        Assert.AreEqual(PlaybackTranscodeSpeedState.Measuring, meter.Read().State, "N/A after the warm-up is no speed.");
    }

    [TestMethod]
    public void OnlyTheNewestRunCountsAndALateSampleOfTheReplacedProcessIsDropped()
    {
        var clock = new ManualTimeProvider(s_start);
        var meter = new PlaybackTranscodeMeter(clock);
        var first = meter.BeginRun(PlaybackHardwareBackend.Nvenc, judgesSpeed: true);
        first.Record(Sample(0.5));
        clock.Advance(PlaybackTranscodeMeter.SustainedFor);
        first.Record(Sample(0.5));
        Assert.AreEqual(PlaybackTranscodeSpeedState.TooSlow, meter.Read().State);

        var second = meter.BeginRun(PlaybackHardwareBackend.Software, judgesSpeed: true);
        Assert.AreEqual(PlaybackTranscodeSpeedState.Unknown, meter.Read().State, "A restart (seek, fallback, new plan) starts over.");
        first.Record(Sample(0.5));
        Assert.AreEqual(PlaybackTranscodeSpeedState.Unknown, meter.Read().State);

        second.Record(Sample(2.0));
        var reading = meter.Read();
        Assert.AreEqual(PlaybackTranscodeSpeedState.Sustainable, reading.State);
        Assert.AreEqual(PlaybackHardwareBackend.Software, reading.Backend);
    }

    [TestMethod]
    public void OnlyAVideoTranscodeIsMeasuredAndItsCallbackFeedsTheSessionMeter()
    {
        var store = new PlaybackStreamSessionStore(new ManualTimeProvider(s_start));
        var selections = new PlaybackStreamSelections(null, null, false, PlaybackQualityPreset.Auto, PlaybackModePreference.Auto, "web");
        var transcoding = store.Create("p", Guid.NewGuid(), Guid.NewGuid(), "/m/a.mkv", 1400, Transcode(Video()), selections);
        var remuxing = store.Create("p", Guid.NewGuid(), Guid.NewGuid(), "/m/b.mkv", 1400, Remux(), selections);

        var progress = transcoding.BeginTranscodeRun(PlaybackHardwareBackend.Software);
        progress!(Sample(2.0));

        Assert.IsNull(remuxing.BeginTranscodeRun(PlaybackHardwareBackend.Software), "A stream copy has no speed worth measuring.");
        Assert.AreEqual(2.0, transcoding.Transcode.Read().Speed);
        Assert.AreEqual(PlaybackTranscodeSpeedState.Unknown, remuxing.Transcode.Read().State);
    }

    [TestMethod]
    public void AnEncodeWhoseSpeedAlsoDependsOnTheReaderIsObservedButNeverJudged()
    {
        var clock = new ManualTimeProvider(s_start);
        var meter = new PlaybackTranscodeMeter(clock);
        var run = meter.BeginRun(PlaybackHardwareBackend.Software, judgesSpeed: false);

        run.Record(Sample(0.3));
        clock.Advance(PlaybackTranscodeMeter.SustainedFor * 6);
        run.Record(Sample(0.3));
        var reading = meter.Read();

        Assert.AreEqual(PlaybackTranscodeSpeedState.Observed, reading.State, "A client that stopped reading blocks ffmpeg: that is no verdict on the encoder.");
        Assert.AreEqual(0.3, reading.Speed, "The value still reaches the diagnostics.");
        Assert.AreEqual(PlaybackHardwareBackend.Software, reading.Backend);
    }

    [TestMethod]
    public void ProgressNeverReplacesTheFailingStepAsTheErrorSummaryAndTheTailStaysBounded()
    {
        var reader = new FfmpegStderrReader();
        var samples = new List<PlaybackTranscodeSample>();
        reader.ProgressReported += samples.Add;

        reader.Feed("Error while opening encoder for output stream #0:0 - maybe incorrect parameters");
        foreach (var line in CannedBlock.Split('\n'))
        {
            reader.Feed(line);
        }

        Assert.AreEqual(1, samples.Count);
        Assert.AreEqual("Error while opening encoder for output stream #0:0 - maybe incorrect parameters", reader.LastLine, "The progress block that followed did not replace the cause.");

        for (var index = 0; index < 100; index++)
        {
            reader.Feed($"warning {index} " + new string('x', 500));
            reader.Feed(null);
            reader.Feed("   ");
        }

        var tail = reader.Tail.Split('\n');
        Assert.AreEqual(FfmpegStderrReader.MaxTailLines, tail.Length);
        Assert.IsTrue(tail.All(x => x.Length <= FfmpegStderrReader.MaxLineLength));
        StringAssert.StartsWith(tail[^1], "warning 99 ", "The newest lines are kept.");
    }

    [TestMethod]
    public async Task TheStderrPumpSeparatesProgressFromDiagnosticsAndKeepsOnlyTheLastLines()
    {
        var lines = new List<string> { "Input #0, matroska,webm, from 'a.mkv':" };
        for (var index = 0; index < 100; index++)
        {
            lines.Add($"warning number {index}");
            lines.AddRange(CannedBlock.Split('\n'));
        }

        lines.Add("Conversion failed!");
        var samples = new List<PlaybackTranscodeSample>();

        var diagnostics = await LivePlaybackStream.ReadDiagnosticsAsync(new StringReader(string.Join('\n', lines)), samples.Add);

        Assert.AreEqual(100, samples.Count, "Every block reaches the meter.");
        var kept = diagnostics.Split('\n');
        Assert.AreEqual("Conversion failed!", kept[^1], "The failing step is last, like before.");
        Assert.IsTrue(kept.Length <= 20, "A stream that runs for hours keeps a bounded tail.");
        Assert.IsFalse(diagnostics.Contains("speed=", StringComparison.Ordinal), "Progress lines are never diagnostics.");
    }

    [TestMethod]
    public void OnlyAnEncodeAsksFfmpegForItsProgressAndTheArgumentsStayWithTheCommandOwner()
    {
        var encode = PlaybackDeliveryCommand.Hls("/media/a.mkv", Transcode(Video()), 0, "/cache/x").ToList();
        var progressive = PlaybackDeliveryCommand.Progressive("/media/a.mkv", Transcode(Video(), PlaybackTransport.ProgressiveMp4), 0).ToList();
        var copy = PlaybackDeliveryCommand.Hls("/media/a.mkv", Remux(), 0, "/cache/x").ToList();

        Assert.AreEqual("pipe:2", encode[encode.IndexOf("-progress") + 1], "stdout carries the progressive media, so progress shares stderr.");
        CollectionAssert.Contains(encode, "-nostats");
        CollectionAssert.Contains(progressive, "-progress");
        CollectionAssert.DoesNotContain(copy, "-progress", "A lossless remux has no speed to protect.");
    }
}
