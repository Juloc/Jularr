using System.Text.Json;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Learning;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Storage;
using Microsoft.AspNetCore.Http;

namespace Jularr.Tests;

[TestClass]
public sealed class ClientApiTests
{
    [TestMethod]
    public void CapabilitiesReportOnlyImplementedFeatures()
    {
        var capabilities = ClientApiContract.Capabilities();

        Assert.AreEqual(2, capabilities.ApiVersion);
        Assert.AreEqual(2, capabilities.MinimumSupportedApiVersion, "Version 1 clients cannot declare completion and must update.");
        Assert.IsTrue(capabilities.Features.Library);
        Assert.IsTrue(capabilities.Features.NativeSessionAuth);
        Assert.IsTrue(capabilities.Features.DirectPlayback);
        Assert.IsTrue(capabilities.Features.PlaybackProgress);
        Assert.IsTrue(capabilities.Features.HttpRangeRequests);
        Assert.IsTrue(capabilities.Features.MediaTrackMetadata);
        Assert.IsTrue(capabilities.Features.NormalizedLearningCues);
        Assert.IsTrue(capabilities.Features.LiveMp4Fallback);
        Assert.IsTrue(capabilities.Features.HlsFallback);
        Assert.IsTrue(capabilities.Features.PlaybackSessions);
        Assert.IsTrue(capabilities.Features.CompanionPairing);
        Assert.IsTrue(capabilities.Features.CompanionControl);
        Assert.IsTrue(capabilities.Features.StorageAvailability);
        Assert.IsTrue(capabilities.Features.OwnerWakeOnLan);
        Assert.IsTrue(capabilities.Features.Watchlist);
    }

    [TestMethod]
    public void ApiPathDetectionDoesNotCatchNormalRazorPages()
    {
        Assert.IsTrue(ClientApiRoutes.IsClientApi(
            new PathString("/api/client/v1/library")));
        Assert.IsTrue(ClientApiRoutes.IsClientApi(
            new PathString("/api/client/v1")));
        Assert.IsFalse(ClientApiRoutes.IsClientApi(
            new PathString("/Library/Anime/abc")));
        Assert.IsFalse(ClientApiRoutes.IsClientApi(
            new PathString("/api/client/v10/library")));
    }

    [TestMethod]
    public void PlayerMediaContractCannotExposeHostFilesystemPath()
    {
        var propertyNames = typeof(ClientPlayerMedia)
            .GetProperties()
            .Select(x => x.Name)
            .ToArray();

        CollectionAssert.DoesNotContain(propertyNames, "SourcePath");
        CollectionAssert.DoesNotContain(propertyNames, "Path");

        var json = JsonSerializer.Serialize(new ClientPlayerMedia(
            Guid.NewGuid(),
            "episode.mkv",
            "video/x-matroska",
            1234,
            1000,
            "hevc",
            "yuv420p",
            "aac",
            "/api/client/v1/media/id/content",
            true,
            new ClientPlaybackOption("ready", "Direct", false),
            new ClientPlaybackOption("ready", "Server", true),
            new ClientMediaAvailability(
                "available",
                false,
                0,
                false,
                null,
                "/api/client/v1/media/id/availability",
                null)));

        Assert.IsFalse(json.Contains("/media/anime", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("SourcePath", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("WakeMacAddress", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NormalClientAvailabilityDoesNotExposeOwnerWakeDetails()
    {
        var mediaId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var snapshot = new MediaAvailabilitySnapshot(
            mediaId,
            rootId,
            StorageAvailabilityState.Offline,
            true,
            2000,
            true,
            DateTimeOffset.UtcNow);

        var normal = ClientApiMappings.ToClientAvailability(snapshot, isOwner: false);
        var owner = ClientApiMappings.ToClientAvailability(snapshot, isOwner: true);

        Assert.IsTrue(normal.Retryable);
        Assert.IsFalse(normal.CanWake);
        Assert.IsNull(normal.RootId);
        Assert.IsNull(normal.WakeUrl);

        Assert.IsTrue(owner.CanWake);
        Assert.AreEqual(rootId, owner.RootId);
        Assert.AreEqual(ClientApiRoutes.WakeRoot(rootId), owner.WakeUrl);
    }

    [TestMethod]
    public void ProbeParserReturnsAudioAndSubtitleTrackMetadata()
    {
        const string json = """
        {
          "streams": [
            {
              "index": 0,
              "codec_type": "video",
              "codec_name": "hevc",
              "pix_fmt": "yuv420p"
            },
            {
              "index": 1,
              "codec_type": "audio",
              "codec_name": "aac",
              "tags": { "language": "jpn", "title": "Japanese" },
              "disposition": { "default": 1, "forced": 0 }
            },
            {
              "index": 2,
              "codec_type": "subtitle",
              "codec_name": "ass",
              "tags": { "language": "eng", "title": "Signs" },
              "disposition": { "default": 0, "forced": 1 }
            }
          ],
          "format": { "duration": "123.45" }
        }
        """;

        var result = PlaybackProbeResult.From(MediaProbeParser.Parse(json));

        Assert.AreEqual("hevc", result.VideoCodec);
        Assert.AreEqual("yuv420p", result.PixelFormat);
        Assert.AreEqual("aac", result.AudioCodec);
        Assert.AreEqual(123.45, result.DurationSeconds);
        var tracks = result.Tracks ?? throw new AssertFailedException("Expected media tracks.");
        Assert.AreEqual(2, tracks.Count);

        var audio = tracks.Single(x => x.Kind == PlaybackTrackKind.Audio);
        Assert.AreEqual(1, audio.StreamIndex);
        Assert.AreEqual("jpn", audio.Language);
        Assert.AreEqual("Japanese", audio.Title);
        Assert.IsTrue(audio.IsDefault);

        var subtitle = tracks.Single(x => x.Kind == PlaybackTrackKind.Subtitle);
        Assert.AreEqual(2, subtitle.StreamIndex);
        Assert.AreEqual("ass", subtitle.Codec);
        Assert.IsTrue(subtitle.IsText);
        Assert.IsTrue(subtitle.IsForced);
    }

    [TestMethod]
    public void CueMappingPreservesStableCueIdAndLearningState()
    {
        var termId = Guid.NewGuid();
        var cue = new PlaybackCue(
            1000,
            2500,
            [
                new PlaybackToken(
                    "猫",
                    termId,
                    "猫",
                    "ねこ",
                    "cat",
                    UserTermState.Learning.ToString()),
                new PlaybackToken("です", null, null, null, null, null)
            ],
            CueId: 42);

        var mapped = ClientApiMappings.ToClientCue(cue);

        Assert.AreEqual(42, mapped.Id);
        Assert.AreEqual("猫です", mapped.Text);
        Assert.AreEqual("learning", mapped.Tokens[0].State);
        Assert.AreEqual("new", mapped.Tokens[1].State);
        Assert.AreEqual(termId, mapped.Tokens[0].TermId);
    }

    [TestMethod]
    public void ClientUrlsAreVersionedAndNeverContainFilePaths()
    {
        var episodeId = Guid.NewGuid();
        var mediaId = Guid.NewGuid();

        Assert.IsTrue(
            ClientApiRoutes.Player(episodeId)
                .StartsWith("/api/client/v1/", StringComparison.Ordinal));
        Assert.IsTrue(
            ClientApiRoutes.DirectContent(mediaId)
                .StartsWith("/api/client/v1/", StringComparison.Ordinal));
        Assert.IsTrue(
            ClientApiRoutes.Progress(episodeId)
                .EndsWith("/progress", StringComparison.Ordinal));
        Assert.IsFalse(
            ClientApiRoutes.DirectContent(mediaId)
                .Contains("\\", StringComparison.Ordinal));
        Assert.IsTrue(
            ClientApiRoutes.MediaAvailability(mediaId)
                .StartsWith("/api/client/v1/", StringComparison.Ordinal));
        Assert.AreEqual("/api/client/v1/session/login", ClientApiRoutes.Login);
        Assert.AreEqual("/api/client/v1/session/logout", ClientApiRoutes.Logout);
        Assert.AreEqual(
            "/api/client/v1/playback-sessions",
            ClientApiRoutes.PlaybackSessions);
        Assert.AreEqual(
            "/hubs/playback-session",
            ClientApiRoutes.PlaybackHub);
        Assert.AreEqual(
            $"/api/client/v1/episodes/{episodeId:D}/hls",
            ClientApiRoutes.Hls(episodeId));
    }
}
