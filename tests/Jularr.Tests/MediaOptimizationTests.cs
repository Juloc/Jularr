using System.Text.Json.Nodes;
using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Media.Compatibility;
using Jularr.Web.Features.Media.Optimization;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Subtitles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Jularr.Tests.MediaOptimizationFixtures;

namespace Jularr.Tests;

[TestClass]
public sealed class MediaOptimizationTests
{
    [TestMethod]
    public void DetailParserKeepsEveryStreamChapterAndDisposition()
    {
        var detail = MediaProbeParser.ParseDetail(MkvAnimeAssWithFonts);

        Assert.AreEqual(6, detail.Streams.Count);
        Assert.AreEqual(2, detail.OfType(MediaProbeStreamType.Attachment).Count());
        Assert.AreEqual("Roboto-Medium.ttf", detail.Streams[4].FileName);
        Assert.IsTrue(detail.Streams[3].Dispositions.Contains("forced"));
        Assert.AreEqual("Signs & Songs", detail.Streams[3].Title);

        var web = MediaProbeParser.ParseDetail(MkvH264Aac);
        Assert.AreEqual(3, web.Chapters.Count);
        Assert.AreEqual("Ending", web.Chapters[2].Title);
        Assert.AreEqual(1330, web.Chapters[2].StartSeconds, 0.001);
        Assert.AreEqual("Frieren - S01E02", web.Title);
        Assert.AreEqual(34046L, web.PrimaryVideo!.FrameCount, "mkvmerge statistics provide the frame count.");
        Assert.AreEqual(48000, web.DefaultAudio!.SampleRate);
        Assert.AreEqual(8, web.PrimaryVideo.BitDepth);

        var hdr = MediaProbeParser.ParseDetail(MkvHevcHdr10Aac).PrimaryVideo!;
        Assert.AreEqual("HDR10", hdr.DynamicRange);
        CollectionAssert.Contains(hdr.SideDataTypes.ToList(), "Mastering display metadata");
        Assert.AreEqual("Dolby Vision", MediaProbeParser.ParseDetail(MkvHevcDolbyVision).PrimaryVideo!.DynamicRange);
    }

    [TestMethod]
    public void CompatibilityProfilesDecideFromStreamsNotFileNames()
    {
        var mkv = new MediaPlaybackCharacteristics(MediaContainerFamily.Matroska, "h264", null, "yuv420p", "aac");
        Assert.AreEqual(0, MediaPlaybackCompatibility.DirectPlayProfiles(mkv).Count, "Matroska is no browser Direct Play target.");

        var mp4 = mkv with { Container = MediaContainerFamily.Mp4 };
        Assert.AreEqual(PlaybackClientProfiles.All.Count, MediaPlaybackCompatibility.DirectPlayProfiles(mp4).Count);

        // MP4 alone does not guarantee Direct Play.
        var hevcHev1 = mp4 with { VideoCodec = "hevc", VideoCodecTag = "hev1", PixelFormat = "yuv420p10le" };
        Assert.AreEqual(0, MediaPlaybackCompatibility.DirectPlayProfiles(hevcHev1).Count);
        var hevcHvc1 = hevcHev1 with { VideoCodecTag = "hvc1" };
        CollectionAssert.AreEqual(
            new[] { PlaybackClientProfiles.AppleWebKit },
            MediaPlaybackCompatibility.DirectPlayProfiles(hevcHvc1).ToArray());

        var evaluation = MediaPlaybackCompatibility.Evaluate(PlaybackClientProfiles.BrowserBaseline, mp4 with { AudioCodec = "ac3" });
        Assert.IsFalse(evaluation.CanDirectPlay);
        Assert.AreEqual("ac3 audio", evaluation.Reason);
        Assert.IsTrue(MediaPlaybackCompatibility.Evaluate(PlaybackClientProfiles.AppleWebKit, mp4 with { AudioCodec = "ac3" }).CanDirectPlay);

        Assert.AreEqual(MediaContainerFamily.WebM, MediaContainers.FromProbe("matroska,webm", "/a/b.webm"));
        Assert.AreEqual(MediaContainerFamily.Matroska, MediaContainers.FromProbe("matroska,webm", "/a/b.mp4"), "The demuxer, not the extension, identifies Matroska.");
        Assert.AreEqual(MediaContainerFamily.Mp4, MediaContainers.FromProbe("mov,mp4,m4a,3gp,3g2,mj2", "/a/b.mkv"));
    }

    [TestMethod]
    public void BrowserFriendlyMkvIsPlannedAsLosslessMp4Remux()
    {
        var decision = Decide(MkvH264Aac);

        Assert.AreEqual(MediaOptimizationOutcome.Remux, decision.Outcome, decision.Summary);
        Assert.AreEqual(0, decision.SourceDirectPlay.Count);
        Assert.AreEqual(PlaybackClientProfiles.All.Count, decision.TargetDirectPlay.Count);
        var plan = decision.Plan!;
        Assert.AreEqual(".mp4", plan.TargetExtension);
        Assert.IsNull(plan.HevcStreamIndex);
        Assert.AreEqual("Japanese", plan.StreamTitles[1]);
        Assert.AreEqual("Deutsch", plan.StreamTitles[2]);
    }

    [TestMethod]
    public void HevcRemuxIsTaggedHvc1ForSafari()
    {
        var decision = Decide(MkvHevcHdr10Aac);

        Assert.AreEqual(MediaOptimizationOutcome.Remux, decision.Outcome, decision.Summary);
        CollectionAssert.AreEqual(new[] { PlaybackClientProfiles.AppleWebKit }, decision.TargetDirectPlay.ToArray());
        Assert.AreEqual(0, decision.Plan!.HevcStreamIndex);
    }

    [TestMethod]
    public void AnimeWithStyledSubtitlesAndFontsKeepsTheOriginalMkv()
    {
        var decision = Decide(MkvAnimeAssWithFonts);

        Assert.AreEqual(MediaOptimizationOutcome.KeepOriginal, decision.Outcome);
        Assert.IsNull(decision.Plan);
        var reasons = decision.Findings.Select(x => x.Reason).ToArray();
        Assert.AreEqual(2, reasons.Count(x => x == MediaOptimizationReason.StyledSubtitlesRequireMatroska));
        CollectionAssert.Contains(reasons, MediaOptimizationReason.AttachmentsRequireMatroska);
        StringAssert.Contains(decision.Summary, "ASS/SSA");
        StringAssert.Contains(decision.Summary, "2 embedded font(s)");
    }

    [TestMethod]
    [DataRow(nameof(MkvHevcDolbyVision), MediaOptimizationOutcome.KeepOriginal, MediaOptimizationReason.DolbyVisionNotVerifiable)]
    [DataRow(nameof(MkvH264Dts), MediaOptimizationOutcome.KeepOriginal, MediaOptimizationReason.NoCompatibilityGain)]
    [DataRow(nameof(MkvH264AacWithTrueHd), MediaOptimizationOutcome.KeepOriginal, MediaOptimizationReason.AudioNotRemuxable)]
    [DataRow(nameof(MkvH264AacSrt), MediaOptimizationOutcome.KeepOriginal, MediaOptimizationReason.SubtitleNotRemuxable)]
    [DataRow(nameof(MkvH264Hi10pAac), MediaOptimizationOutcome.KeepOriginal, MediaOptimizationReason.NoCompatibilityGain)]
    [DataRow(nameof(MkvH264AacCoverArt), MediaOptimizationOutcome.KeepOriginal, MediaOptimizationReason.CoverArtNotPreserved)]
    [DataRow(nameof(Mp4H264Aac), MediaOptimizationOutcome.AlreadyOptimal, MediaOptimizationReason.AlreadyBrowserFriendly)]
    [DataRow(nameof(WebMVp9Opus), MediaOptimizationOutcome.AlreadyOptimal, MediaOptimizationReason.AlreadyBrowserFriendly)]
    public void UnsafeOrUselessRemuxesAreNotPlanned(
        string fixture,
        MediaOptimizationOutcome outcome,
        MediaOptimizationReason reason)
    {
        var json = (string)typeof(MediaOptimizationFixtures).GetField(fixture)!.GetValue(null)!;
        var path = fixture.StartsWith("WebM", StringComparison.Ordinal) ? "/library/episode.webm" : "/library/episode.mkv";
        var decision = MediaOptimizationPlanner.Decide(MediaProbeParser.ParseDetail(json), path);

        Assert.AreEqual(outcome, decision.Outcome, decision.Summary);
        Assert.IsNull(decision.Plan);
        CollectionAssert.Contains(decision.Findings.Select(x => x.Reason).ToList(), reason, decision.Summary);
    }

    [TestMethod]
    public void RemuxArgumentsCopyEveryStreamAndNeverEncode()
    {
        var plan = Decide(MkvHevcHdr10Aac).Plan! with { StreamTitles = new Dictionary<int, string> { [1] = "Japanese 5.1" } };
        var arguments = FfmpegMediaContainerRemuxer.BuildArguments("/library/a.mkv", "/library/a.mp4.jularr-partial", plan);
        var joined = string.Join(" ", arguments);

        StringAssert.Contains(joined, "-map 0 -map_metadata 0 -map_chapters 0 -c copy");
        StringAssert.Contains(joined, "-tag:0 hvc1");
        StringAssert.Contains(joined, "-metadata:s:1 handler_name=Japanese 5.1");
        StringAssert.Contains(joined, "-movflags +faststart -f mp4 /library/a.mp4.jularr-partial");
        Assert.IsFalse(arguments.Any(x => x.Contains("libx26", StringComparison.Ordinal) || x == "aac" || x.StartsWith("-crf", StringComparison.Ordinal)));
        Assert.IsFalse(arguments.Contains("-sn") || arguments.Contains("-dn") || arguments.Contains("-an"), "No stream is dropped.");
    }

    [TestMethod]
    public void VerifierAcceptsAFaithfulRemux()
    {
        var problems = Verify(MkvH264Aac, Mp4RemuxOfMkvH264Aac);

        Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
    }

    [TestMethod]
    [DataRow("lost-stream", "streams")]
    [DataRow("lost-chapter", "chapters")]
    [DataRow("lost-title", "track name")]
    [DataRow("lost-language", "language")]
    [DataRow("lost-disposition", "dub flag")]
    [DataRow("changed-default", "default audio")]
    [DataRow("truncated", "Duration")]
    [DataRow("dropped-frames", "frame count")]
    [DataRow("re-encoded", "changed from Video h264")]
    public void VerifierRejectsAnyLoss(string damage, string expected)
    {
        var output = Mutate(Mp4RemuxOfMkvH264Aac, root =>
        {
            var streams = root["streams"]!.AsArray();
            switch (damage)
            {
                case "lost-stream":
                    streams.RemoveAt(3);
                    streams.RemoveAt(2);
                    break;
                case "lost-chapter":
                    root["chapters"]!.AsArray().RemoveAt(2);
                    break;
                case "lost-title":
                    Stream(root, 2)["tags"]!.AsObject().Remove("handler_name");
                    break;
                case "lost-language":
                    Stream(root, 1)["tags"]!["language"] = "und";
                    break;
                case "lost-disposition":
                    Stream(root, 2)["disposition"]!["dub"] = 0;
                    break;
                case "changed-default":
                    Stream(root, 1)["disposition"]!["default"] = 0;
                    Stream(root, 2)["disposition"]!["default"] = 1;
                    break;
                case "truncated":
                    root["format"]!["duration"] = "1200.000000";
                    break;
                case "dropped-frames":
                    Stream(root, 0)["nb_frames"] = "34000";
                    break;
                case "re-encoded":
                    Stream(root, 0)["codec_name"] = "mpeg4";
                    break;
            }
        });

        var problems = Verify(MkvH264Aac, output);

        Assert.IsTrue(problems.Any(x => x.Contains(expected, StringComparison.Ordinal)), string.Join(Environment.NewLine, problems));
    }

    [TestMethod]
    public void VerifierRequiresHdrSignallingToSurvive()
    {
        var output = Mutate(MkvHevcHdr10Aac, root =>
        {
            root["format"]!["format_name"] = "mov,mp4,m4a,3gp,3g2,mj2";
            Stream(root, 0)["codec_tag_string"] = "hvc1";
            Stream(root, 0).Remove("side_data_list");
        });

        var problems = MediaRemuxVerifier.Verify(
            MediaProbeParser.ParseDetail(MkvHevcHdr10Aac),
            MediaProbeParser.ParseDetail(output),
            MediaContainerFamily.Mp4,
            "/library/a.mp4",
            1000,
            1000);

        Assert.IsTrue(problems.Any(x => x.Contains("Mastering display metadata", StringComparison.Ordinal)), string.Join(Environment.NewLine, problems));
    }

    [TestMethod]
    public async Task OptimizerRemuxesVerifiesAndAdoptsTheOutputInPlace()
    {
        await using var fixture = await OptimizerFixture.CreateAsync();
        var media = await fixture.AddMkvAsync(MkvH264Aac);
        var targetPath = Path.ChangeExtension(media.Path, ".mp4");
        var embedded = new SubtitleTrack
        {
            EpisodeId = media.EpisodeId!.Value,
            Path = EmbeddedSubtitleExtractor.BuildSourceKey(media.Path, 2),
            Format = "embedded"
        };
        var sidecar = new SubtitleTrack { EpisodeId = media.EpisodeId!.Value, Path = Path.ChangeExtension(media.Path, ".ja.srt"), Format = "srt" };
        fixture.Inventory.Db.SubtitleTracks.AddRange(embedded, sidecar);
        await fixture.Inventory.Db.SaveChangesAsync();

        var result = await fixture.Optimizer.OptimizeFileAsync(media.Id, fixture.Operation, CancellationToken.None);

        Assert.AreEqual(MediaOptimizationFileStatus.Optimized, result.Status, result.Message);
        StringAssert.Contains(result.Message, "without re-encoding");
        Assert.IsFalse(File.Exists(media.Path), "The source is removed only after adoption.");
        Assert.IsTrue(File.Exists(targetPath));
        Assert.IsFalse(File.Exists(targetPath + MediaContainerOptimizer.PartialSuffix));
        Assert.AreEqual(1, fixture.Remuxer.Calls.Count);

        await using var db = new AppDbContext(fixture.Inventory.Options);
        var row = await db.MediaFiles.SingleAsync(x => x.Id == media.Id);
        Assert.AreEqual(targetPath, row.Path, "The MediaFile keeps its id, so progress and segments stay attached.");
        Assert.AreEqual(new FileInfo(targetPath).Length, row.SizeBytes);
        var tracks = await db.SubtitleTracks.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Path);
        Assert.AreEqual(EmbeddedSubtitleExtractor.BuildSourceKey(targetPath, 2), tracks[embedded.Id]);
        Assert.AreEqual(sidecar.Path, tracks[sidecar.Id], "Sidecar subtitles keep their own path.");

        Assert.AreEqual(1, fixture.Participant.Replaced.Count);
        Assert.AreEqual(targetPath, fixture.Participant.Replaced[0].TargetPath);
        Assert.AreEqual(0, fixture.Journal.ReadAll().Count);

        var logs = await fixture.LogsAsync();
        Assert.IsTrue(logs.Any(x => x.Contains("Remux to MP4 without re-encoding", StringComparison.Ordinal)), string.Join(Environment.NewLine, logs));
        Assert.AreEqual("Optimizing container: Episode 01.mkv", fixture.Remuxer.OperationMessageDuringRemux, "The step is visible on the Operation.");

        // Idempotent: the adopted MP4 is already optimal.
        fixture.Runner.Returns(targetPath, Mp4RemuxOfMkvH264Aac);
        var again = await fixture.Optimizer.OptimizeFileAsync(media.Id, fixture.Operation, CancellationToken.None);
        Assert.AreEqual(MediaOptimizationFileStatus.Unchanged, again.Status, again.Message);
        Assert.AreEqual(1, fixture.Remuxer.Calls.Count);
    }

    [TestMethod]
    public async Task UnsafeCandidateIsNeverRemuxedAndTheDecisionIsLogged()
    {
        await using var fixture = await OptimizerFixture.CreateAsync();
        var media = await fixture.AddMkvAsync(MkvAnimeAssWithFonts);

        var results = await fixture.Optimizer.OptimizeAsync([media.Id], fixture.Operation, CancellationToken.None);

        Assert.AreEqual(MediaOptimizationFileStatus.Kept, results.Single().Status);
        Assert.AreEqual(0, fixture.Remuxer.Calls.Count);
        Assert.IsTrue(File.Exists(media.Path));
        var logs = await fixture.LogsAsync();
        Assert.IsTrue(logs.Any(x => x.Contains("ASS/SSA; its styling requires MKV", StringComparison.Ordinal)), string.Join(Environment.NewLine, logs));
    }

    [TestMethod]
    public async Task FailedVerificationKeepsTheSourceAndDiscardsTheOutput()
    {
        await using var fixture = await OptimizerFixture.CreateAsync();
        var media = await fixture.AddMkvAsync(MkvH264Aac);
        fixture.OutputProbe = Mutate(Mp4RemuxOfMkvH264Aac, root => root["chapters"]!.AsArray().Clear());

        var result = await fixture.Optimizer.OptimizeFileAsync(media.Id, fixture.Operation, CancellationToken.None);

        Assert.AreEqual(MediaOptimizationFileStatus.Kept, result.Status);
        StringAssert.Contains(result.Message, "chapters");
        await fixture.AssertUntouchedAsync(media);
    }

    [TestMethod]
    public async Task FailedRemuxKeepsTheSource()
    {
        await using var fixture = await OptimizerFixture.CreateAsync();
        var media = await fixture.AddMkvAsync(MkvH264Aac);
        fixture.Remuxer.Result = new MediaRemuxRun(false, "Could not write header");

        var result = await fixture.Optimizer.OptimizeFileAsync(media.Id, fixture.Operation, CancellationToken.None);

        Assert.AreEqual(MediaOptimizationFileStatus.Failed, result.Status);
        StringAssert.Contains(result.Message, "Could not write header");
        await fixture.AssertUntouchedAsync(media);
    }

    [TestMethod]
    public async Task BlockedReplacementKeepsTheSource()
    {
        await using var fixture = await OptimizerFixture.CreateAsync();
        var media = await fixture.AddMkvAsync(MkvH264Aac);
        fixture.Participant.Checks.Enqueue(new MediaReplacementCheck(MediaReplacementVerdict.Blocked, "Ownership: Sonarr still monitors it."));

        var result = await fixture.Optimizer.OptimizeFileAsync(media.Id, fixture.Operation, CancellationToken.None);

        Assert.AreEqual(MediaOptimizationFileStatus.Kept, result.Status);
        StringAssert.Contains(result.Message, "Sonarr still monitors it");
        await fixture.AssertUntouchedAsync(media);
    }

    [TestMethod]
    public async Task ReplacementWaitsForRunningLibraryWork()
    {
        await using var fixture = await OptimizerFixture.CreateAsync();
        var media = await fixture.AddMkvAsync(MkvH264Aac);
        fixture.Participant.Checks.Enqueue(new MediaReplacementCheck(MediaReplacementVerdict.Wait, "Waiting for 'Anime import' to finish."));
        fixture.Participant.Checks.Enqueue(new MediaReplacementCheck(MediaReplacementVerdict.Wait, "Waiting for 'Anime import' to finish."));

        var result = await fixture.Optimizer.OptimizeFileAsync(media.Id, fixture.Operation, CancellationToken.None);

        Assert.AreEqual(MediaOptimizationFileStatus.Optimized, result.Status, result.Message);
        Assert.AreEqual(3, fixture.Participant.CheckCount);
    }

    [TestMethod]
    public async Task SourceChangedDuringRemuxIsLeftAlone()
    {
        await using var fixture = await OptimizerFixture.CreateAsync();
        var media = await fixture.AddMkvAsync(MkvH264Aac);
        fixture.Remuxer.OnRemux = () => File.AppendAllText(media.Path, "upgraded");

        var result = await fixture.Optimizer.OptimizeFileAsync(media.Id, fixture.Operation, CancellationToken.None);

        Assert.AreEqual(MediaOptimizationFileStatus.Skipped, result.Status);
        Assert.IsTrue(File.Exists(media.Path));
        Assert.IsFalse(File.Exists(Path.ChangeExtension(media.Path, ".mp4")));
        Assert.IsFalse(File.Exists(Path.ChangeExtension(media.Path, ".mp4") + MediaContainerOptimizer.PartialSuffix));
    }

    [TestMethod]
    public async Task RecoveryDiscardsAnInterruptedRemux()
    {
        await using var fixture = await OptimizerFixture.CreateAsync();
        var media = await fixture.AddMkvAsync(MkvH264Aac);
        var entry = fixture.Entry(media, MediaOptimizationStage.Remuxing);
        await File.WriteAllTextAsync(entry.PartialPath, "half-written");
        fixture.Journal.Write(entry);

        Assert.AreEqual(1, await fixture.Optimizer.RecoverAsync(fixture.Operation, CancellationToken.None));

        await fixture.AssertUntouchedAsync(media);
    }

    [TestMethod]
    public async Task RecoveryRollsBackACommitTheDatabaseNeverRecorded()
    {
        await using var fixture = await OptimizerFixture.CreateAsync();
        var media = await fixture.AddMkvAsync(MkvH264Aac);
        var entry = fixture.Entry(media, MediaOptimizationStage.Committing);
        await File.WriteAllBytesAsync(entry.TargetPath, new byte[entry.OutputSizeBytes!.Value]);
        fixture.Journal.Write(entry);

        await fixture.Optimizer.RecoverAsync(fixture.Operation, CancellationToken.None);

        await fixture.AssertUntouchedAsync(media);
    }

    [TestMethod]
    public async Task RecoveryCompletesACommitTheDatabaseAlreadyRecorded()
    {
        await using var fixture = await OptimizerFixture.CreateAsync();
        var media = await fixture.AddMkvAsync(MkvH264Aac);
        var entry = fixture.Entry(media, MediaOptimizationStage.Committing);
        await File.WriteAllBytesAsync(entry.TargetPath, new byte[entry.OutputSizeBytes!.Value]);
        fixture.Journal.Write(entry);
        await fixture.Inventory.Db.MediaFiles
            .Where(x => x.Id == media.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Path, entry.TargetPath));

        // The next optimization of the same file resolves its journal first.
        fixture.Runner.Returns(entry.TargetPath, Mp4RemuxOfMkvH264Aac);
        var result = await fixture.Optimizer.OptimizeFileAsync(media.Id, fixture.Operation, CancellationToken.None);

        Assert.AreEqual(MediaOptimizationFileStatus.Unchanged, result.Status, result.Message);
        Assert.IsFalse(File.Exists(media.Path), "The source of the recorded commit is removed.");
        Assert.IsTrue(File.Exists(entry.TargetPath));
        Assert.AreEqual(0, fixture.Journal.ReadAll().Count);
        Assert.AreEqual(1, fixture.Participant.Replaced.Count, "Path records follow the recovered commit.");
    }

    private static MediaOptimizationDecision Decide(string json) =>
        MediaOptimizationPlanner.Decide(MediaProbeParser.ParseDetail(json), "/library/Episode 01.mkv");

    private static IReadOnlyList<string> Verify(string source, string output) =>
        MediaRemuxVerifier.Verify(
            MediaProbeParser.ParseDetail(source),
            MediaProbeParser.ParseDetail(output),
            MediaContainerFamily.Mp4,
            "/library/Episode 01.mp4",
            1_000_000,
            995_000);

    private sealed class OptimizerFixture : IAsyncDisposable
    {
        private OptimizerFixture(MediaInventoryFixture inventory, ServiceProvider services, Guid operationId)
        {
            Inventory = inventory;
            Services = services;
            Journal = new MediaOptimizationJournal(Path.Combine(inventory.TempRoot, "journal"));
            Remuxer = new FakeRemuxer(this);
            Operation = new OperationExecutionContext(operationId, services);
            Optimizer = new MediaContainerOptimizer(
                inventory.Db,
                inventory.Runner,
                Remuxer,
                inventory.Inventory,
                Journal,
                [Participant],
                NullLogger<MediaContainerOptimizer>.Instance,
                new MediaOptimizationOptions { BusyPollInterval = TimeSpan.FromMilliseconds(1) });
        }

        public MediaInventoryFixture Inventory { get; }
        public ServiceProvider Services { get; }
        public FakeMediaProbeRunner Runner => Inventory.Runner;
        public MediaOptimizationJournal Journal { get; }
        public FakeRemuxer Remuxer { get; }
        public FakeParticipant Participant { get; } = new();
        public OperationExecutionContext Operation { get; }
        public MediaContainerOptimizer Optimizer { get; }
        public string OutputProbe { get; set; } = Mp4RemuxOfMkvH264Aac;

        public static async Task<OptimizerFixture> CreateAsync()
        {
            var inventory = await MediaInventoryFixture.CreateAsync();
            var collection = new ServiceCollection();
            collection.AddScoped(_ => new AppDbContext(inventory.Options));
            var services = collection.BuildServiceProvider();
            await using var scope = services.CreateAsyncScope();
            var operationId = await new OperationStore(scope.ServiceProvider.GetRequiredService<AppDbContext>())
                .CreateAsync(new OperationDescriptor(MediaContainerOptimizer.OperationKind, "Library", "Optimize media for Direct Play"));
            return new OptimizerFixture(inventory, services, operationId);
        }

        public async Task<MediaFile> AddMkvAsync(string probeJson)
        {
            var media = await Inventory.AddMediaAsync("Episode 01.mkv", new byte[4096]);
            Runner.Returns(media.Path, probeJson);
            return media;
        }

        public MediaOptimizationJournalEntry Entry(MediaFile media, MediaOptimizationStage stage)
        {
            var target = Path.ChangeExtension(media.Path, ".mp4");
            return new(media.Id, media.Path, target, target + MediaContainerOptimizer.PartialSuffix, media.SizeBytes, stage, stage == MediaOptimizationStage.Committing ? 4000 : null, DateTime.UtcNow);
        }

        public async Task<IReadOnlyList<string>> LogsAsync()
        {
            await using var db = new AppDbContext(Inventory.Options);
            return
            [
                .. (await new OperationStore(db).ListLogsAsync(new OperationLogFilter(OperationId: Operation.OperationId, Limit: 200)))
                    .Select(x => x.Message)
            ];
        }

        public async Task AssertUntouchedAsync(MediaFile media)
        {
            var target = Path.ChangeExtension(media.Path, ".mp4");
            Assert.IsTrue(File.Exists(media.Path), "The source stays the library file.");
            Assert.AreEqual(4096, new FileInfo(media.Path).Length);
            Assert.IsFalse(File.Exists(target));
            Assert.IsFalse(File.Exists(target + MediaContainerOptimizer.PartialSuffix), "No half-written output is left behind.");
            Assert.AreEqual(0, Journal.ReadAll().Count);
            await using var db = new AppDbContext(Inventory.Options);
            Assert.AreEqual(media.Path, (await db.MediaFiles.SingleAsync(x => x.Id == media.Id)).Path);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Inventory.DisposeAsync();
        }
    }

    // Writes a same-size output and registers the fixture's ffprobe view of it.
    private sealed class FakeRemuxer(OptimizerFixture fixture) : IMediaContainerRemuxer
    {
        public List<(string Source, string Output, MediaRemuxPlan Plan)> Calls { get; } = [];
        public MediaRemuxRun Result { get; set; } = new(true);
        public Action? OnRemux { get; set; }
        public string? OperationMessageDuringRemux { get; private set; }

        public async Task<MediaRemuxRun> RemuxAsync(string sourcePath, string outputPath, MediaRemuxPlan plan, CancellationToken cancellationToken)
        {
            Calls.Add((sourcePath, outputPath, plan));
            await using (var db = new AppDbContext(fixture.Inventory.Options))
            {
                OperationMessageDuringRemux = (await new OperationStore(db).GetAsync(fixture.Operation.OperationId, cancellationToken))?.Message;
            }

            OnRemux?.Invoke();
            if (!Result.Succeeded)
            {
                await File.WriteAllTextAsync(outputPath, "partial", cancellationToken);
                return Result;
            }

            await File.WriteAllBytesAsync(outputPath, new byte[4000], cancellationToken);
            fixture.Runner.Returns(outputPath, fixture.OutputProbe);
            return Result;
        }
    }

    private sealed class FakeParticipant : IMediaFileReplacementParticipant
    {
        public Queue<MediaReplacementCheck> Checks { get; } = new();
        public int CheckCount { get; private set; }
        public List<MediaFileReplacement> Replaced { get; } = [];

        public Task<MediaReplacementCheck> CheckAsync(MediaFileReplacement replacement, CancellationToken cancellationToken)
        {
            CheckCount++;
            return Task.FromResult(Checks.TryDequeue(out var check) ? check : MediaReplacementCheck.Allowed);
        }

        public Task OnReplacedAsync(MediaFileReplacement replacement, CancellationToken cancellationToken)
        {
            Replaced.Add(replacement);
            return Task.CompletedTask;
        }
    }
}
