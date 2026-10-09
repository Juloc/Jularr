using System.Diagnostics;
using System.Text.Json;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Progress;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class PlayerControlsTests
{
    private static readonly PlaybackMediaTrack JapaneseAudio =
        new(1, PlaybackTrackKind.Audio, "aac", "jpn", null, true, false, false);
    private static readonly PlaybackMediaTrack EnglishAudio =
        new(2, PlaybackTrackKind.Audio, "eac3", "eng", "English 5.1", false, false, false);
    private static readonly PlaybackMediaTrack EnglishSigns =
        new(3, PlaybackTrackKind.Subtitle, "ass", "eng", "Signs", false, true, true);
    private static readonly PlaybackMediaTrack EnglishFull =
        new(4, PlaybackTrackKind.Subtitle, "ass", "eng", "Full", false, false, true);
    private static readonly PlaybackMediaTrack JapaneseText =
        new(5, PlaybackTrackKind.Subtitle, "subrip", "jpn", null, true, false, true);
    private static readonly PlaybackMediaTrack ImageSubtitle =
        new(6, PlaybackTrackKind.Subtitle, "hdmv_pgs_subtitle", "ger", null, false, false, false);

    private static readonly IReadOnlyList<PlaybackMediaTrack> Tracks =
        [JapaneseAudio, EnglishAudio, EnglishSigns, EnglishFull, JapaneseText, ImageSubtitle];

    // ---- Preference resolution -------------------------------------------------

    [TestMethod]
    public void AudioPreferenceMatchesLanguageAliasesAndFallsBackToFileDefault()
    {
        Assert.AreEqual(2, PlaybackTrackSelection.ResolveAudio(Tracks, "en")?.StreamIndex);
        Assert.AreEqual(2, PlaybackTrackSelection.ResolveAudio(Tracks, "ENG")?.StreamIndex);
        Assert.AreEqual(1, PlaybackTrackSelection.ResolveAudio(Tracks, "ja")?.StreamIndex);
        Assert.AreEqual(1, PlaybackTrackSelection.ResolveAudio(Tracks, "fr")?.StreamIndex, "No match keeps the file default.");
        Assert.AreEqual(1, PlaybackTrackSelection.ResolveAudio(Tracks, null)?.StreamIndex);
        Assert.IsNull(PlaybackTrackSelection.ResolveAudio([], "en"));
    }

    [TestMethod]
    public void SubtitlePreferenceResolvesOffLearningAndEmbeddedTracks()
    {
        Assert.AreEqual(
            PlaybackSubtitleMode.Off,
            PlaybackTrackSelection.ResolveSubtitle(Tracks, true, "off").Mode);

        Assert.AreEqual(
            PlaybackSubtitleMode.Learning,
            PlaybackTrackSelection.ResolveSubtitle(Tracks, true, "jpn").Mode,
            "Japanese preference prefers the interactive learning overlay.");

        var english = PlaybackTrackSelection.ResolveSubtitle(Tracks, true, "en");
        Assert.AreEqual(PlaybackSubtitleMode.Embedded, english.Mode);
        Assert.AreEqual("stream:4", english.TrackId, "Full dialogue wins over forced signs.");

        Assert.AreEqual(
            PlaybackSubtitleMode.Learning,
            PlaybackTrackSelection.ResolveSubtitle(Tracks, true, null).Mode);

        var noLearning = PlaybackTrackSelection.ResolveSubtitle(Tracks, false, null);
        Assert.AreEqual("stream:5", noLearning.TrackId, "Without learning cues the file default text track is used.");

        Assert.AreEqual(
            PlaybackSubtitleMode.Off,
            PlaybackTrackSelection.ResolveSubtitle([EnglishSigns], false, null).Mode,
            "A forced-only default is never auto-selected.");

        Assert.AreEqual(
            "stream:5",
            PlaybackTrackSelection.ResolveSubtitle(Tracks, false, "de").TrackId,
            "An image subtitle cannot satisfy a language preference; the file default text track is used.");
    }

    [TestMethod]
    public void PlayerControlsUseProfilePreferencesAndDeduplicateTheLearningSource()
    {
        var media = Media("episode.mkv", "h264", "yuv420p", "aac", 1080);
        var preferences = new PlaybackPreferencesSnapshot(false, "en", "ja", 1.25);

        var controls = PlayerControls.Build(media, hasLearningCues: true, learningSourceStreamIndex: 5, preferences);

        Assert.AreEqual("stream:1", controls.FileDefaultAudioTrackId);
        Assert.AreEqual("stream:2", controls.InitialAudioTrackId);
        Assert.AreEqual(PlayerControls.SubtitleLearning, controls.InitialSubtitle);
        Assert.AreEqual(1.25, controls.InitialSpeed);
        Assert.IsTrue(controls.SubtitleTracks.Single(x => x.Id == "stream:5").IsLearningSource);
        var image = controls.SubtitleTracks.Single(x => x.Id == "stream:6");
        Assert.IsTrue(image.IsImage, "Picture subtitles are marked so the player asks for a burn-in.");
        Assert.IsTrue(image.IsSelectable);
        Assert.IsFalse(controls.SubtitleTracks.Single(x => x.Id == "stream:5").IsImage);
        Assert.AreEqual("English 5.1", controls.AudioTracks.Single(x => x.Id == "stream:2").Label);

        var embeddedJapanese = PlayerControls.Build(
            media,
            hasLearningCues: true,
            learningSourceStreamIndex: 5,
            new PlaybackPreferencesSnapshot(false, null, "en"));
        Assert.AreEqual("stream:4", embeddedJapanese.InitialSubtitle);
    }

    [TestMethod]
    public async Task PreferenceUpdatesArePartialNormalizedValidatedAndProfileScoped()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var reader = fixture.Service("reader");
        var other = fixture.Service("other");

        var defaults = await reader.GetPreferencesAsync();
        Assert.AreEqual(1.0, defaults.DefaultPlaybackSpeed);
        Assert.IsNull(defaults.PreferredAudioLanguage);
        Assert.IsNull(defaults.PreferredSubtitleLanguage);

        var updated = await reader.UpdatePreferencesAsync(
            new PlaybackPreferencesUpdate(
                PreferredAudioLanguage: "JPN",
                PreferredSubtitleLanguage: "Off",
                DefaultPlaybackSpeed: 1.5));
        Assert.AreEqual("ja", updated.PreferredAudioLanguage);
        Assert.AreEqual("off", updated.PreferredSubtitleLanguage);
        Assert.AreEqual(1.5, updated.DefaultPlaybackSpeed);

        var autoplayOnly = await reader.UpdatePreferencesAsync(new PlaybackPreferencesUpdate(AutoplayNext: true));
        Assert.AreEqual("ja", autoplayOnly.PreferredAudioLanguage, "Omitted fields keep their stored value.");
        Assert.AreEqual(1.5, autoplayOnly.DefaultPlaybackSpeed);

        var cleared = await reader.UpdatePreferencesAsync(new PlaybackPreferencesUpdate(PreferredAudioLanguage: ""));
        Assert.IsNull(cleared.PreferredAudioLanguage, "An empty language clears back to the file default.");

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            reader.UpdatePreferencesAsync(new PlaybackPreferencesUpdate(DefaultPlaybackSpeed: 3.0)));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            reader.UpdatePreferencesAsync(new PlaybackPreferencesUpdate(PreferredAudioLanguage: "off")));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            reader.UpdatePreferencesAsync(new PlaybackPreferencesUpdate(PreferredSubtitleLanguage: "english")));

        var stored = await reader.GetPreferencesAsync();
        Assert.AreEqual(1.5, stored.DefaultPlaybackSpeed, "Rejected updates store nothing.");
        Assert.AreEqual("off", stored.PreferredSubtitleLanguage);

        Assert.AreEqual(PlaybackPreferencesSnapshot.Default, await other.GetPreferencesAsync());
        Assert.AreEqual(1, await fixture.Db.ProfilePlaybackPreferences.CountAsync());
    }

    // ---- Track selection across fallback restarts --------------------------------

    [TestMethod]
    public void SelectedAudioTrackSurvivesDeviceToServerFallbackRestart()
    {
        var probe = Probe("hevc", "yuv420p10le", "aac", 1080);
        var device = PlaybackService.Decide(
            "/media/episode.mkv",
            probe,
            new PlaybackStreamRequest(PlaybackRequestedMode.Device, "stream:2"));
        var server = PlaybackService.Decide(
            "/media/episode.mkv",
            probe,
            new PlaybackStreamRequest(PlaybackRequestedMode.Server, "stream:2"));

        Assert.AreEqual(2, device?.AudioStreamIndex);
        Assert.AreEqual(PlaybackAudioMode.Aac, device?.Plan?.AudioMode, "EAC3 of the selected track is converted.");
        Assert.AreEqual(2, server?.AudioStreamIndex);

        var live = LivePlaybackCommand.BuildArguments("/media/episode.mkv", server!.Plan!, 300, server.AudioStreamIndex).ToList();
        CollectionAssert.Contains(live, "0:2");
        CollectionAssert.DoesNotContain(live, "0:a:0?");

        var hls = HlsPlaybackSessionManager.BuildArguments("/media/episode.mkv", "/data/hls/x", 300, 2).ToList();
        CollectionAssert.Contains(hls, "0:2");
        CollectionAssert.DoesNotContain(hls, "0:a:0?");
    }

    [TestMethod]
    public void NonDefaultAudioOnDirectPlayFileUsesVideoCopyRemuxAndUnknownTrackIsRejected()
    {
        var probe = Probe("h264", "yuv420p", "aac", 720);

        var defaultAudio = PlaybackService.Decide(
            "/media/episode.mp4",
            probe,
            new PlaybackStreamRequest(PlaybackRequestedMode.Device, "stream:1"));
        Assert.IsNotNull(defaultAudio);
        Assert.IsNull(defaultAudio.Plan, "The file default audio track keeps direct play.");

        var english = PlaybackService.Decide(
            "/media/episode.mp4",
            probe,
            new PlaybackStreamRequest(PlaybackRequestedMode.Server, "stream:2"));
        Assert.AreEqual(PlaybackVideoMode.Copy, english?.Plan?.VideoMode, "An audio switch never re-encodes video.");
        Assert.AreEqual(2, english?.AudioStreamIndex);

        Assert.IsNull(PlaybackService.Decide(
            "/media/episode.mp4",
            probe,
            new PlaybackStreamRequest(PlaybackRequestedMode.Device, "stream:9")));
        Assert.IsNull(PlaybackService.Decide(
            "/media/episode.mp4",
            probe,
            new PlaybackStreamRequest(PlaybackRequestedMode.Device, "stream:3")),
            "A subtitle stream is not an audio track.");
    }

    [TestMethod]
    public void CanonicalTrackIdsRoundTripAndRejectForeignShapes()
    {
        Assert.AreEqual("stream:12", PlaybackTrackIds.Format(12));
        Assert.IsTrue(PlaybackTrackIds.TryParse("stream:12", out var index));
        Assert.AreEqual(12, index);
        Assert.IsFalse(PlaybackTrackIds.TryParse("stream:-1", out _));
        Assert.IsFalse(PlaybackTrackIds.TryParse("a:1", out _));
        Assert.IsFalse(PlaybackTrackIds.TryParse("stream:1 ", out _));
        Assert.IsFalse(PlaybackTrackIds.TryParse(null, out _));
    }

    // ---- Quality cap -------------------------------------------------------------

    [TestMethod]
    public void QualityCapNeverTranscodesWhenDirectPlaySatisfiesIt()
    {
        var hd = Probe("h264", "yuv420p", "aac", 720);
        foreach (var cap in new[] { PlaybackQualityCap.Auto, PlaybackQualityCap.P1080, PlaybackQualityCap.P720 })
        {
            foreach (var mode in new[] { PlaybackRequestedMode.Device, PlaybackRequestedMode.Server })
            {
                var decision = PlaybackService.Decide("/media/episode.mp4", hd, new PlaybackStreamRequest(mode, null, cap));
                Assert.IsNotNull(decision);
                Assert.IsNull(decision.Plan, $"{mode}/{cap} must direct play a 720p source.");
            }
        }

        var remux = PlaybackService.Decide(
            "/media/episode.mkv",
            hd,
            new PlaybackStreamRequest(PlaybackRequestedMode.Server, null, PlaybackQualityCap.P720));
        Assert.AreEqual(PlaybackPreparationKind.CompatibleRemux, remux?.Plan?.Kind, "A remux that fits the cap stays a video copy.");

        var unknownHeight = PlaybackService.Decide(
            "/media/episode.mp4",
            Probe("h264", "yuv420p", "aac", null),
            new PlaybackStreamRequest(PlaybackRequestedMode.Server, null, PlaybackQualityCap.LowBandwidth));
        Assert.IsNull(unknownHeight?.Plan, "An unknown source height never justifies a transcode.");
    }

    [TestMethod]
    public void QualityCapEncodesOnlyInServerModeWhenInventoryProvesSourceIsTaller()
    {
        var fullHd = Probe("h264", "yuv420p", "aac", 1080);

        var server = PlaybackService.Decide(
            "/media/episode.mp4",
            fullHd,
            new PlaybackStreamRequest(PlaybackRequestedMode.Server, null, PlaybackQualityCap.P720));
        Assert.AreEqual(PlaybackVideoMode.H264, server?.Plan?.VideoMode);
        var arguments = LivePlaybackCommand.BuildArguments("/media/episode.mp4", server!.Plan!, 0, null, PlaybackQualityCap.P720).ToList();
        Assert.AreEqual("scale=-2:min(ih\\,720)", arguments[arguments.IndexOf("-vf") + 1]);

        var device = PlaybackService.Decide(
            "/media/episode.mp4",
            fullHd,
            new PlaybackStreamRequest(PlaybackRequestedMode.Device, null, PlaybackQualityCap.P720));
        Assert.IsNull(device?.Plan, "Device mode never video-transcodes.");
    }

    [TestMethod]
    public void QualityCapNamesParseStrictly()
    {
        CollectionAssert.AreEqual(new[] { "auto", "1080p", "720p", "low" }, PlaybackQuality.Names.ToArray());
        Assert.IsTrue(PlaybackQuality.TryParse(null, out var none));
        Assert.AreEqual(PlaybackQualityCap.Auto, none);
        Assert.IsTrue(PlaybackQuality.TryParse("720P", out var hd));
        Assert.AreEqual(PlaybackQualityCap.P720, hd);
        Assert.IsFalse(PlaybackQuality.TryParse("4k", out _));
    }

    // ---- Additive native API contract -------------------------------------------

    [TestMethod]
    public void PlaybackPreferencesApiIsAnAdditiveExtension()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var legacy = JsonSerializer.Deserialize<ClientPlaybackPreferencesUpdate>("{\"autoplayNext\":true}", options)!;
        Assert.IsTrue(legacy.AutoplayNext);
        Assert.IsNull(legacy.PreferredAudioLanguage);
        Assert.IsNull(legacy.PreferredSubtitleLanguage);
        Assert.IsNull(legacy.DefaultPlaybackSpeed);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(
            ClientApiMappings.ToClientPreferences(new PlaybackPreferencesSnapshot(true, "ja", "off", 1.25)),
            options));
        var root = document.RootElement;
        Assert.IsTrue(root.GetProperty("autoplayNext").GetBoolean(), "The original field keeps its name and meaning.");
        Assert.AreEqual("ja", root.GetProperty("preferredAudioLanguage").GetString());
        Assert.AreEqual("off", root.GetProperty("preferredSubtitleLanguage").GetString());
        Assert.AreEqual(1.25, root.GetProperty("defaultPlaybackSpeed").GetDouble());

        var features = ClientApiContract.Capabilities().Features;
        Assert.IsTrue(features.PlaybackPreferences);
        Assert.IsTrue(features.EmbeddedSubtitleCues);
        Assert.AreEqual(
            "/api/client/v1/episodes/00000000-0000-0000-0000-000000000000/subtitle-tracks/stream%3A3/cues",
            ClientApiRoutes.SubtitleTrackCues(Guid.Empty, "stream:3"));
    }

    [TestMethod]
    public void PlayerBootstrapKeepsExistingFieldsAndAddsDefaultsAndControls()
    {
        var names = typeof(ClientPlayerBootstrap).GetProperties().Select(x => x.Name).ToArray();
        foreach (var existing in new[]
                 {
                     "ApiVersion", "Episode", "Media", "AudioTracks", "SubtitleTracks", "LearningSubtitles",
                     "ActiveLearningSubtitleTrackId", "DefaultAudioTrackId", "DefaultSubtitleTrackId", "Fallback"
                 })
        {
            CollectionAssert.Contains(names, existing);
        }

        CollectionAssert.Contains(names, "Defaults");
        CollectionAssert.Contains(names, "Controls");
        CollectionAssert.AreEqual(
            PlaybackPreferenceRules.Speeds.ToArray(),
            new ClientPlayerControls(PlaybackPreferenceRules.Speeds, PlaybackQuality.Names).PlaybackSpeeds.ToArray());
    }

    // ---- Cue timing at playback speed ---------------------------------------------

    [TestMethod]
    public void DualSubtitlesRenderIndependentTextTracksOnTheSameClock()
    {
        var root = RepositoryRoot();
        var player = Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "episode-player.js");
        var stage = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "Pages", "Library", "_VideoPlayerStage.cshtml"));
        StringAssert.Contains(stage, "data-secondary-subtitle-track");
        StringAssert.Contains(stage, "data-secondary-playback-subtitle");
        StringAssert.Contains(stage, "track.IsSelectable && !track.IsImage");

        var script = """
            const fs = require("fs");
            const source = fs.readFileSync(process.argv[2], "utf8");
            const start = source.indexOf("    let playbackCues = [];");
            const end = source.indexOf("    const updateRepeatAvailability = () => {", start);
            if (start < 0 || end < 0) throw new Error("subtitle owner not found");
            (async () => {
                const design = { activeCuesAt: (items, ms) => items.filter(c => c.startMs <= ms && ms <= c.endMs) };
                const cues = [];
                let subtitleChoice = "stream:2";
                let secondarySubtitleChoice = "stream:3";
                const subtitleSelect = { selectedOptions: [{ dataset: {} }] };
                const burnInSubtitleTrackId = () => null;
                const playbackSubtitle = { textContent: "", hidden: true };
                const secondaryPlaybackSubtitle = { textContent: "", hidden: true };
                const subtitleCanvas = null;
                const primaryPositionedSubtitles = null;
                const secondaryPositionedSubtitles = null;
                const controlsData = { subtitleCuesUrlTemplate: "/cues/__track__" };
                const text = {};
                const showPlayerError = () => {};
                const urls = [];
                const fetch = async url => {
                    urls.push(url);
                    return { ok: true, json: async () => ({
                        cues: [{ startMs: 1000, endMs: 2000, text: url.endsWith("stream%3A2") ? "Original" : "Translation" }]
                    }) };
                };
                const result = await eval(source.slice(start, end) + `
                    (async () => {
                        playbackCues = await loadSubtitleCues("stream:2");
                        secondaryPlaybackCues = await loadSubtitleCues("stream:3");
                        renderPlaybackSubtitle(1500);
                        const shown = playbackSubtitle.textContent + "|" + secondaryPlaybackSubtitle.textContent;
                        renderPlaybackSubtitle(3000);
                        const hidden = playbackSubtitle.hidden && secondaryPlaybackSubtitle.hidden;
                        secondarySubtitleChoice = subtitleChoice;
                        const duplicateBlocked = secondaryTrackId() === null;
                        await loadSubtitleCues("stream:2");
                        return [shown, hidden, duplicateBlocked].join("|");
                    })()
                `);
                console.log(result + "|" + urls.length);
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        Assert.AreEqual("Original|Translation|true|true|2", RunNode(script, player));
    }

    [TestMethod]
    public void StyledSubtitlesKeepOverlappingSignAndDialogueIndependent()
    {
        var root = RepositoryRoot();
        var player = Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "episode-player.js");
        var script = """
            const fs = require("fs");
            const source = fs.readFileSync(process.argv[2], "utf8");
            const start = source.indexOf("    let playbackCues = [];");
            const end = source.indexOf("    const updateRepeatAvailability = () => {", start);
            if (start < 0 || end < 0) throw new Error("subtitle renderer missing");

            const fake = () => ({
                textContent: "", hidden: true, children: [], style: {},
                replaceChildren(...children) { this.children = children; }
            });
            const document = { createElement: () => ({ style: {}, textContent: "" }) };
            const design = { activeCuesAt: (items, ms) => items.filter(c => c.startMs <= ms && ms <= c.endMs) };
            const cues = [];
            let subtitleChoice = "stream:2";
            let secondarySubtitleChoice = "stream:3";
            const video = { videoWidth: 640, videoHeight: 360 };
            const stage = { clientWidth: 640, clientHeight: 360 };
            const subtitleCanvas = { style: {} };
            const primaryPositionedSubtitles = fake();
            const secondaryPositionedSubtitles = fake();
            const playbackSubtitle = fake();
            const secondaryPlaybackSubtitle = fake();
            const subtitleSelect = { selectedOptions: [{ dataset: {} }] };
            const burnInSubtitleTrackId = () => null;
            const controlsData = {};
            const showPlayerError = () => {};
            const text = {};
            const output = eval(source.slice(start, end) + `
                (() => {
                    playbackCues = [
                        { startMs: 0, endMs: 2000, text: "駅前",
                            presentation: { alignment: 7, xPercent: 25, yPercent: 10, layer: 3, color: "#0099FF" } },
                        { startMs: 0, endMs: 2000, text: "Dialogue" }
                    ];
                    secondaryPlaybackCues = [{ startMs: 0, endMs: 2000, text: "Translation" }];
                    renderPlaybackSubtitle(1500);
                    return [
                        primaryPositionedSubtitles.children.length,
                        playbackSubtitle.children.length,
                        secondaryPlaybackSubtitle.children.length,
                        primaryPositionedSubtitles.children[0].style.left,
                        primaryPositionedSubtitles.children[0].style.top,
                        primaryPositionedSubtitles.children[0].style.color,
                        playbackSubtitle.children[0].textContent,
                        secondaryPlaybackSubtitle.children[0].textContent
                    ].join("|");
                })()
            `);
            console.log(output);
            """;
        Assert.AreEqual("1|1|1|25%|10%|#0099FF|Dialogue|Translation", RunNode(script, player));
    }

    [TestMethod]
    public void SubtitleCollisionPolicyMovesSecondaryButKeepsAuthoredPrimaryPosition()
    {
        var root = RepositoryRoot();
        var player = Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "episode-player.js");
        var script = """
            const fs = require("fs");
            const source = fs.readFileSync(process.argv[2], "utf8");
            const start = source.indexOf("    const resolveSubtitleCollisions = () => {");
            const end = source.indexOf("    const renderPlaybackSubtitle = timeMs => {", start);
            if (start < 0 || end < 0) throw new Error("collision policy missing");

            const box = (left, top, right, bottom) => ({ left, top, right, bottom, height: bottom - top });
            const sourceSign = {
                style: { transform: "translate(0%, 0%)" },
                getBoundingClientRect: () => box(80, 60, 240, 102)
            };
            const translatedSign = {
                style: { transform: "translate(0%, 0%)" },
                getBoundingClientRect: () => box(80, 60, 240, 102)
            };
            const primaryPositionedSubtitles = {
                querySelectorAll: () => [sourceSign]
            };
            const secondaryPositionedSubtitles = {
                querySelectorAll: () => [translatedSign]
            };
            const subtitleCanvas = { getBoundingClientRect: () => box(0, 0, 640, 360) };
            const stage = {
                getBoundingClientRect: () => ({ ...box(0, 0, 640, 360), height: 360 })
            };
            const subtitleStack = {
                style: { bottom: "" },
                getBoundingClientRect() {
                    const bottom = Number.parseFloat(this.style.bottom) || 76;
                    return box(120, 360 - bottom - 50, 520, 360 - bottom);
                }
            };
            const resolve = eval(source.slice(start, end) + "resolveSubtitleCollisions");
            resolve();
            const authored = sourceSign.style.transform;
            const translated = translatedSign.style.transform;
            primaryPositionedSubtitles.querySelectorAll = () => [{
                getBoundingClientRect: () => box(140, 245, 500, 272)
            }];
            secondaryPositionedSubtitles.querySelectorAll = () => [];
            resolve();
            console.log([authored, translated, subtitleStack.style.bottom].join("|"));
            """;

        Assert.AreEqual("translate(0%, 0%)|translate(0%, 0%) translateY(-48px)|121px", RunNode(script, player));
    }

    [TestMethod]
    public void WebCueLookupFollowsTheMediaClockAtEveryPlaybackSpeed()
    {
        var root = RepositoryRoot();
        var player = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "episode-player.js"));

        StringAssert.Contains(player, "video.defaultPlaybackRate = playbackSpeed;", "A source restart must keep the speed.");
        StringAssert.Contains(player, "design.cueIndexAt(cues, nowMs)");
        StringAssert.Contains(player, "design.activeCuesAt(trackCues, timeMs)");
        StringAssert.Contains(player, "const nowMs = Math.floor(absoluteCurrentTime() * 1000);");
        Assert.IsFalse(player.Contains("Date.now() - playbackStarted", StringComparison.Ordinal));

        // Simulate 0.5x, 1x and 2x playback: wall-clock progress differs, media time
        // (what video.currentTime reports) decides the cue.
        var script = """
            const window = {};
            const document = {};
            eval(require("fs").readFileSync(process.argv[2], "utf8"));
            const d = window.JularrPlayerDesign;
            const cues = [{ startMs: 1000, endMs: 2000 }, { startMs: 2500, endMs: 4000 }, { startMs: 6000, endMs: 7000 }];
            const out = [];
            for (const speed of [0.5, 1, 2]) {
                for (const wallMs of [0, 1500, 2000, 3000]) {
                    const mediaMs = wallMs * speed;
                    out.push(d.cueIndexAt(cues, mediaMs));
                }
            }
            out.push(d.lineStartAt(cues, 5000), d.lineStartAt(cues, 500), d.activeCuesAt(cues, 3000).length);
            console.log(out.join(","));
            """;
        var output = RunNode(script, Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "player-design.js"));

        // 0.5x: media 0,750,1000,1500 ; 1x: 0,1500,2000,3000 ; 2x: 0,3000,4000,6000
        Assert.AreEqual("-1,-1,0,0,-1,0,0,1,-1,1,1,2,2500,,1", output);
    }

    internal static PlaybackProbeResult Probe(string video, string pixelFormat, string audio, int? height) =>
        new(video, pixelFormat, audio, 1420, Tracks, height);

    internal static PlaybackMedia Media(string fileName, string video, string pixelFormat, string audio, int? height)
    {
        var ready = new PlaybackOption(PlaybackOptionAvailability.Ready, "Ready");
        return new PlaybackMedia(
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"/media/{fileName}",
            fileName,
            "video/mp4",
            video,
            ready,
            ready,
            1420,
            pixelFormat,
            audio,
            1,
            Tracks,
            VideoHeight: height);
    }

    private static string RunNode(string script, string argument)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"jularr-cue-{Guid.NewGuid():N}.js");
        File.WriteAllText(scriptPath, script);
        try
        {
            using var process = Process.Start(new ProcessStartInfo("node")
            {
                ArgumentList = { scriptPath, argument },
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null)
            {
                Assert.Inconclusive("Node.js is not available to execute the web player helpers.");
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, errors);
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Inconclusive("Node.js is not available to execute the web player helpers.");
            throw;
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }
}
