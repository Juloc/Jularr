using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Subtitles;
using Jularr.Web.Features.Vocabulary;
using Jularr.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class MediaInventoryTests
{

    [TestMethod]
    public void ParsesHevcTenBitHdrWithMultipleAudioAndImageSubtitles()
    {
        var info = MediaProbeParser.Parse(MediaProbeFixtures.HevcTenBitHdrMultiAudio);

        Assert.AreEqual("matroska,webm", info.Container);
        Assert.AreEqual(1420.5, info.DurationSeconds!.Value, 0.001);

        var video = info.Video ?? throw new AssertFailedException("Expected a video stream.");
        Assert.AreEqual("hevc", video.Codec);
        Assert.AreEqual("Main 10", video.Profile);
        Assert.AreEqual(3840, video.Width);
        Assert.AreEqual(2160, video.Height);
        Assert.AreEqual("yuv420p10le", video.PixelFormat);
        Assert.AreEqual(10, video.BitDepth);
        Assert.AreEqual("HDR10", video.DynamicRange);

        Assert.AreEqual(2, info.AudioStreams.Count);
        var japanese = info.AudioStreams[0];
        Assert.AreEqual(1, japanese.Index);
        Assert.AreEqual("eac3", japanese.Codec);
        Assert.AreEqual("jpn", japanese.Language);
        Assert.AreEqual("Japanese 5.1", japanese.Title);
        Assert.AreEqual(6, japanese.Channels);
        Assert.AreEqual("5.1(side)", japanese.ChannelLayout);
        Assert.IsTrue(japanese.IsDefault);
        Assert.AreEqual(2, info.AudioStreams[1].Channels);
        Assert.IsFalse(info.AudioStreams[1].IsDefault);

        // Attachments (fonts) are not media streams.
        Assert.AreEqual(2, info.SubtitleStreams.Count);
        var ass = info.SubtitleStreams[0];
        Assert.AreEqual(3, ass.Index);
        Assert.AreEqual("ass", ass.Codec);
        Assert.IsTrue(ass.IsText);
        Assert.IsTrue(ass.IsDefault);
        Assert.AreEqual("Full Dialogue", ass.Title);

        var pgs = info.SubtitleStreams[1];
        Assert.AreEqual("hdmv_pgs_subtitle", pgs.Codec);
        Assert.IsFalse(pgs.IsText);
        Assert.IsTrue(pgs.IsForced);
        Assert.AreEqual("eng", pgs.Language, "Tag names are matched case-insensitively.");
        Assert.IsNull(pgs.Channels);
    }

    [TestMethod]
    public void DetectsDolbyVisionHlgAndSkipsAttachedCoverArt()
    {
        var dolbyVision = MediaProbeParser.Parse("""
        {
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "mjpeg", "pix_fmt": "yuvj420p", "disposition": { "attached_pic": 1 } },
            {
              "index": 1,
              "codec_type": "video",
              "codec_name": "hevc",
              "pix_fmt": "yuv420p10le",
              "bits_per_raw_sample": "10",
              "color_transfer": "smpte2084",
              "side_data_list": [ { "side_data_type": "DOVI configuration record" } ]
            }
          ]
        }
        """);
        Assert.AreEqual("hevc", dolbyVision.Video?.Codec);
        Assert.AreEqual("Dolby Vision", dolbyVision.Video?.DynamicRange);
        Assert.AreEqual(10, dolbyVision.Video?.BitDepth);

        var hlg = MediaProbeParser.Parse("""
        { "streams": [ { "index": 0, "codec_type": "video", "codec_name": "hevc", "pix_fmt": "yuv420p12le", "color_transfer": "arib-std-b67" } ] }
        """);
        Assert.AreEqual("HLG", hlg.Video?.DynamicRange);
        Assert.AreEqual(12, hlg.Video?.BitDepth);

        var sdr = MediaProbeParser.Parse(MediaProbeFixtures.H264Stereo);
        Assert.AreEqual("SDR", sdr.Video?.DynamicRange);
        Assert.AreEqual(8, sdr.Video?.BitDepth);
        Assert.AreEqual("High", sdr.Video?.Profile);
    }

    [TestMethod]
    public async Task UnchangedFileIsServedFromInventoryWithoutProbing()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("episode.mkv", [1, 2, 3, 4]);
        fixture.Runner.Returns(media.Path, MediaProbeFixtures.HevcTenBitHdrMultiAudio);

        var first = await fixture.Inventory.EnsureAnalyzedAsync(media.Id, CancellationToken.None);
        var second = await fixture.Inventory.EnsureAnalyzedAsync(media.Path, CancellationToken.None);

        Assert.AreEqual(1, fixture.Runner.Calls.Count);
        Assert.AreEqual(MediaAnalysisStatus.Succeeded, first?.Status);
        Assert.AreEqual(MediaInventoryService.CurrentProbeVersion, second?.ProbeVersion);
        Assert.AreEqual("hevc", second?.Technical?.Video?.Codec);
        Assert.AreEqual(4, second?.Technical?.Streams.Count);

        var stored = await fixture.Inventory.GetAsync(media.Id, CancellationToken.None);
        Assert.AreEqual(10, stored?.Technical?.Video?.BitDepth);
        Assert.AreEqual(1, (await fixture.Inventory.ListAsync(fixture.Root.Id, CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task SizeChangeTriggersReanalysis()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("episode.mkv", [1, 2, 3, 4]);
        fixture.Runner.Returns(media.Path, MediaProbeFixtures.H264Stereo);
        await fixture.Inventory.EnsureAnalyzedAsync(media.Id, CancellationToken.None);

        fixture.Runner.Returns(media.Path, MediaProbeFixtures.HevcTenBitHdrMultiAudio);
        var modified = File.GetLastWriteTimeUtc(media.Path);
        await File.WriteAllBytesAsync(media.Path, [1, 2, 3, 4, 5]);
        File.SetLastWriteTimeUtc(media.Path, modified);

        var entry = await fixture.Inventory.EnsureAnalyzedAsync(media.Id, CancellationToken.None);

        Assert.AreEqual(2, fixture.Runner.Calls.Count);
        Assert.AreEqual("hevc", entry?.Technical?.Video?.Codec);
        Assert.AreEqual(4, entry?.Technical?.Streams.Count, "Old stream rows are replaced, not merged.");
    }

    [TestMethod]
    public async Task ModifiedTimeOnlyChangeKeepsAnalysisWhenFingerprintMatches()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var content = Enumerable.Range(0, 200_000).Select(x => (byte)(x % 251)).ToArray();
        var media = await fixture.AddMediaAsync("episode.mkv", content);
        fixture.Runner.Returns(media.Path, MediaProbeFixtures.H264Stereo);
        await fixture.Inventory.EnsureAnalyzedAsync(media.Id, CancellationToken.None);

        // A re-copied or touched file: same bytes, new mtime.
        var touched = File.GetLastWriteTimeUtc(media.Path).AddHours(1);
        File.SetLastWriteTimeUtc(media.Path, touched);
        var entry = await fixture.Inventory.EnsureAnalyzedAsync(media.Id, CancellationToken.None);

        Assert.AreEqual(1, fixture.Runner.Calls.Count);
        Assert.AreEqual("h264", entry?.Technical?.Video?.Codec);
        var stored = await fixture.Db.MediaAnalyses.AsNoTracking().SingleAsync();
        Assert.AreEqual(touched, stored.SourceLastWriteTimeUtc);
        Assert.AreEqual(64, stored.SourceFingerprint?.Length);

        // Same size, new mtime and a changed tail: the content differs, so it is re-analysed.
        content[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(media.Path, content);
        File.SetLastWriteTimeUtc(media.Path, touched.AddHours(1));
        await fixture.Inventory.EnsureAnalyzedAsync(media.Id, CancellationToken.None);

        Assert.AreEqual(2, fixture.Runner.Calls.Count);
    }

    [TestMethod]
    public async Task FingerprintCoversLengthHeadAndTail()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jularr-fingerprint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var content = Enumerable.Range(0, 300_000).Select(x => (byte)(x % 13)).ToArray();
            var path = Path.Combine(directory, "a.mkv");
            await File.WriteAllBytesAsync(path, content);
            var original = await MediaInventoryService.TryComputeFingerprintAsync(path, CancellationToken.None);

            content[10] ^= 0xFF;
            await File.WriteAllBytesAsync(path, content);
            var headChanged = await MediaInventoryService.TryComputeFingerprintAsync(path, CancellationToken.None);

            content[10] ^= 0xFF;
            content[^10] ^= 0xFF;
            await File.WriteAllBytesAsync(path, content);
            var tailChanged = await MediaInventoryService.TryComputeFingerprintAsync(path, CancellationToken.None);

            await File.WriteAllBytesAsync(path, content[..^1]);
            var lengthChanged = await MediaInventoryService.TryComputeFingerprintAsync(path, CancellationToken.None);

            Assert.IsNotNull(original);
            CollectionAssert.AllItemsAreUnique(new[] { original, headChanged, tailChanged, lengthChanged });
            Assert.IsNull(await MediaInventoryService.TryComputeFingerprintAsync(
                Path.Combine(directory, "missing.mkv"),
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProbeVersionBumpTriggersReanalysis()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("episode.mkv", [1, 2, 3]);
        fixture.Runner.Returns(media.Path, MediaProbeFixtures.H264Stereo);

        await fixture.Inventory.ReconcileAsync(fixture.Root.Id, CancellationToken.None);
        await fixture.Db.MediaAnalyses.ExecuteUpdateAsync(setters =>
            setters.SetProperty(x => x.ProbeVersion, MediaInventoryService.CurrentProbeVersion - 1));

        var result = await fixture.Inventory.ReconcileAsync(fixture.Root.Id, CancellationToken.None);

        Assert.AreEqual(2, fixture.Runner.Calls.Count);
        Assert.AreEqual(1, result.Analyzed);
        var stored = await fixture.Db.MediaAnalyses.AsNoTracking().SingleAsync();
        Assert.AreEqual(MediaInventoryService.CurrentProbeVersion, stored.ProbeVersion);
    }

    [TestMethod]
    public async Task InvalidMediaRecordsDiagnosticWithoutFailingTheRootOrRetrying()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var broken = await fixture.AddMediaAsync("a-broken.mkv", [0]);
        var garbage = await fixture.AddMediaAsync("b-garbage.mkv", [0, 1]);
        var valid = await fixture.AddMediaAsync("c-valid.mkv", [1, 2, 3]);
        fixture.Runner.Returns(
            broken.Path,
            new MediaProbeRun(MediaProbeRunStatus.Failed, Error: $"{broken.Path}: Invalid data found when processing input"));
        fixture.Runner.Returns(garbage.Path, "{ not json");
        fixture.Runner.Returns(valid.Path, MediaProbeFixtures.H264Stereo);

        var first = await fixture.Inventory.ReconcileAsync(fixture.Root.Id, CancellationToken.None);

        Assert.AreEqual(new MediaInventoryReconciliation(0, 1, 2, 0), first);
        var brokenEntry = await fixture.Inventory.GetAsync(broken.Id, CancellationToken.None);
        Assert.AreEqual(MediaAnalysisStatus.Failed, brokenEntry?.Status);
        Assert.AreEqual("a-broken.mkv: Invalid data found when processing input", brokenEntry?.Diagnostic);
        Assert.IsNull(brokenEntry?.Technical);
        Assert.AreEqual(
            "ffprobe returned invalid JSON.",
            (await fixture.Inventory.GetAsync(garbage.Id, CancellationToken.None))?.Diagnostic);
        Assert.AreEqual(
            MediaAnalysisStatus.Succeeded,
            (await fixture.Inventory.GetAsync(valid.Id, CancellationToken.None))?.Status);

        fixture.Runner.ClearCalls();
        var second = await fixture.Inventory.ReconcileAsync(fixture.Root.Id, CancellationToken.None);

        Assert.AreEqual(0, fixture.Runner.Calls.Count, "Failed media is not re-probed until it changes.");
        Assert.AreEqual(new MediaInventoryReconciliation(3, 0, 0, 0), second);
    }

    [TestMethod]
    public async Task UnavailableProbeIsDeferredAndRetriedAfterTheRetryDelay()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("episode.mkv", [1, 2, 3]);
        fixture.Runner.Returns(
            media.Path,
            new MediaProbeRun(MediaProbeRunStatus.Unavailable, Error: "ffprobe could not be started or timed out."));

        var first = await fixture.Inventory.ReconcileAsync(fixture.Root.Id, CancellationToken.None);
        var immediate = await fixture.Inventory.EnsureAnalyzedAsync(media.Id, CancellationToken.None);

        Assert.AreEqual(1, first.Deferred);
        Assert.AreEqual(MediaAnalysisStatus.Pending, immediate?.Status);
        Assert.AreEqual(1, fixture.Runner.Calls.Count, "A deferred probe is not repeated immediately.");

        await fixture.Db.MediaAnalyses.ExecuteUpdateAsync(setters =>
            setters.SetProperty(x => x.AnalyzedAt, DateTime.UtcNow - MediaInventoryService.PendingRetryDelay - TimeSpan.FromMinutes(1)));
        fixture.Runner.Returns(media.Path, MediaProbeFixtures.H264Stereo);

        var retried = await fixture.Inventory.EnsureAnalyzedAsync(media.Id, CancellationToken.None);

        Assert.AreEqual(2, fixture.Runner.Calls.Count);
        Assert.AreEqual(MediaAnalysisStatus.Succeeded, retried?.Status);
        Assert.IsNull(retried?.Diagnostic);
    }

    [TestMethod]
    public async Task AnUnavailableProbeKeepsTheStoredAnalysisAndItsTracksAndSaysSo()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("episode.mkv", [1, 2, 3, 4]);
        fixture.Runner.Returns(media.Path, MediaProbeFixtures.HevcTenBitHdrMultiAudio);
        await fixture.Inventory.EnsureAnalyzedAsync(media.Id, CancellationToken.None);
        var tracksBefore = await fixture.Db.MediaTracks.Where(x => x.MediaFileId == media.Id).CountAsync();

        // The file changed, so the stored analysis is stale, and ffprobe is missing: the tool says nothing about the file.
        fixture.Runner.Returns(media.Path, new MediaProbeRun(MediaProbeRunStatus.Unavailable, Error: "ffprobe could not be started or timed out."));
        var modified = File.GetLastWriteTimeUtc(media.Path);
        await File.WriteAllBytesAsync(media.Path, [1, 2, 3, 4, 5]);
        File.SetLastWriteTimeUtc(media.Path, modified);

        var entry = await fixture.Inventory.EnsureAnalyzedAsync(media.Id, CancellationToken.None);
        var repeated = await fixture.Inventory.EnsureAnalyzedAsync(media.Id, CancellationToken.None);

        Assert.AreEqual(MediaAnalysisStatus.Pending, entry?.Status, "The re-check did not happen, so it is not reported as done.");
        Assert.AreEqual("hevc", entry?.Technical?.Video?.Codec, "Playback keeps the last stored analysis.");
        Assert.AreEqual(4, entry?.Technical?.Streams.Count);
        StringAssert.Contains(entry?.Diagnostic, "ffprobe could not be started");
        Assert.AreEqual(2, fixture.Runner.Calls.Count, "A tool that could not run is not retried by every reader of the file.");
        Assert.AreEqual(MediaAnalysisStatus.Pending, repeated?.Status);

        var stored = await fixture.Db.MediaAnalyses.AsNoTracking().SingleAsync(x => x.MediaFileId == media.Id);
        Assert.AreEqual(MediaAnalysisStatus.Succeeded, stored.Status);
        Assert.AreEqual("hevc", stored.VideoCodec);
        Assert.AreEqual(10, stored.BitDepth);
        Assert.AreEqual(tracksBefore, await fixture.Db.MediaTracks.Where(x => x.MediaFileId == media.Id).CountAsync());
        Assert.AreEqual(4, stored.SourceSizeBytes, "The stored identity stays, so the file is analysed again once ffprobe answers.");
    }

    [TestMethod]
    public async Task AnExplicitReanalysisTriesAMissingProbeAgainAtOnceAndReplacesTheKeptAnalysis()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("episode.mkv", [1, 2, 3, 4]);
        fixture.Runner.Returns(media.Path, MediaProbeFixtures.H264Stereo);
        await fixture.Inventory.EnsureAnalyzedAsync(media.Id, CancellationToken.None);
        await fixture.Inventory.InvalidateAsync([media.Id], CancellationToken.None);
        fixture.Runner.Returns(media.Path, new MediaProbeRun(MediaProbeRunStatus.Unavailable, Error: "ffprobe could not be started or timed out."));
        var kept = await fixture.Inventory.EnsureAnalyzedAsync(media.Id, CancellationToken.None);

        fixture.Runner.Returns(media.Path, MediaProbeFixtures.HevcTenBitHdrMultiAudio);
        await fixture.Inventory.InvalidateAsync([media.Id], CancellationToken.None);
        var reanalysed = await fixture.Inventory.EnsureAnalyzedAsync(media.Id, CancellationToken.None);

        Assert.AreEqual(MediaAnalysisStatus.Pending, kept?.Status);
        Assert.AreEqual(MediaAnalysisStatus.Succeeded, reanalysed?.Status);
        Assert.AreEqual("hevc", reanalysed?.Technical?.Video?.Codec);
        Assert.IsNull(reanalysed?.Diagnostic);
    }

    [TestMethod]
    public async Task AnUnavailableProbeOfAFileWithoutAnalysisStaysPendingWithTheReason()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("episode.mkv", [1, 2, 3, 4]);
        fixture.Runner.Returns(media.Path, new MediaProbeRun(MediaProbeRunStatus.Unavailable, Error: "ffprobe could not be started or timed out."));

        var entry = await fixture.Inventory.EnsureAnalyzedAsync(media.Id, CancellationToken.None);

        Assert.AreEqual(MediaAnalysisStatus.Pending, entry?.Status);
        Assert.IsNull(entry?.Technical);
        StringAssert.Contains(entry?.Diagnostic, "ffprobe could not be started");
    }

    [TestMethod]
    public async Task PlaybackReadsTechnicalMetadataFromTheInventory()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("episode.mkv", [1, 2, 3]);
        fixture.Runner.Returns(media.Path, MediaProbeFixtures.HevcTenBitHdrMultiAudio);
        await fixture.Inventory.ReconcileAsync(fixture.Root.Id, CancellationToken.None);
        fixture.Runner.ClearCalls();

        var playback = new PlaybackService(
            fixture.Db,
            new PlaybackCueProjector(new EmptyMorphology()),
            fixture.Inventory);

        var snapshot = await playback.GetMediaAsync(media.EpisodeId!.Value, CancellationToken.None);
        var stream = await playback.GetStreamAsync(
            media.EpisodeId!.Value,
            new PlaybackStreamRequest(PlaybackRequestedMode.Device),
            CancellationToken.None);

        Assert.AreEqual(0, fixture.Runner.Calls.Count, "Playback must not run a second probe.");
        Assert.IsNotNull(snapshot);
        Assert.AreEqual("hevc", snapshot.VideoCodec);
        Assert.AreEqual("yuv420p10le", snapshot.PixelFormat);
        Assert.AreEqual("eac3", snapshot.AudioCodec);
        Assert.AreEqual(1420.5, snapshot.DurationSeconds!.Value, 0.001);
        var tracks = snapshot.Tracks ?? throw new AssertFailedException("Expected tracks.");
        Assert.AreEqual(2, tracks.Count(x => x.Kind == PlaybackTrackKind.Audio));
        Assert.IsTrue(tracks.Single(x => x.StreamIndex == 3).IsText);
        Assert.IsTrue(tracks.Single(x => x.StreamIndex == 4).IsForced);
        Assert.IsNotNull(stream?.LivePlan);
        Assert.AreEqual(PlaybackPreparationKind.DeviceHevcRemux, stream.LivePlan.Kind);
    }

    [TestMethod]
    public async Task EmbeddedSubtitleSelectionReadsTheInventory()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("episode.mkv", [1, 2, 3]);
        // Only an English image subtitle: nothing to extract, so no ffmpeg run is needed.
        fixture.Runner.Returns(media.Path, """
        {
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "h264", "pix_fmt": "yuv420p" },
            { "index": 1, "codec_type": "subtitle", "codec_name": "hdmv_pgs_subtitle", "tags": { "language": "eng" } }
          ]
        }
        """);
        var extractor = new EmbeddedSubtitleExtractor(
            new MediaProcessRunner(NullLogger<MediaProcessRunner>.Instance),
            fixture.Inventory,
            NullLogger<EmbeddedSubtitleExtractor>.Instance);

        Assert.IsNull(await extractor.ExtractPreferredTextAsync(media.Path, "ja", CancellationToken.None));
        Assert.IsNull(await extractor.ExtractTextStreamAsync(media.Path, 1, CancellationToken.None));
        Assert.AreEqual(1, fixture.Runner.Calls.Count, "Both calls share one inventory analysis.");
    }


    private sealed class EmptyMorphology : IJapaneseMorphology
    {
        public IReadOnlyList<JapaneseMorphToken> Analyze(string text) => [];
    }
}
