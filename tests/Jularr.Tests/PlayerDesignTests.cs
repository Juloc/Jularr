using Jint;
using System.Text.Json;

namespace Jularr.Tests;

[TestClass]
public sealed class PlayerDesignTests
{
    [TestMethod]
    public void SharedPlayerContractContainsRequiredLearningActions()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "design", "player", "player-actions.json")));

        var ids = document.RootElement
            .GetProperty("actions")
            .EnumerateArray()
            .Select(x => x.GetProperty("id").GetString()!)
            .ToArray();

        CollectionAssert.Contains(ids, "learnCurrentCue");
        CollectionAssert.Contains(ids, "openWord");
        CollectionAssert.Contains(ids, "repeatCurrentCue");
        CollectionAssert.Contains(ids, "closeOverlay");
        Assert.AreEqual(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    public void WebPlayerUsesCanonicalTokenValuesAndLoadsSharedLayerFirst()
    {
        var root = FindRepositoryRoot();
        var tokenPath = Path.Combine(root, "design", "player", "player-tokens.json");
        using var document = JsonDocument.Parse(File.ReadAllText(tokenPath));

        var touchSize = document.RootElement
            .GetProperty("controlSizeDp")
            .GetProperty("touch")
            .GetInt32();

        var css = File.ReadAllText(
            Path.Combine(root, "src", "Jularr.Web", "wwwroot", "css", "player.css"));
        StringAssert.Contains(css, $"--player-control-size: {touchSize}px;");
        // The web player follows the profile accent from the Jularr theme engine; the canonical
        // colours in player-tokens.json remain the defaults for native clients.
        StringAssert.Contains(css, "--player-accent: var(--accent);");

        var page = EpisodePlayerSource.Read(root);
        StringAssert.Contains(page, "~/css/player.css");
        StringAssert.Contains(page, "~/js/player-design.js");
        StringAssert.Contains(page, "data-word-inspector");
        StringAssert.Contains(page, "data-player-action=\"repeatCurrentCue\"");

        var designScriptIndex = page.IndexOf("~/js/player-design.js", StringComparison.Ordinal);
        var episodeScriptIndex = page.IndexOf("~/js/episode-player.js", StringComparison.Ordinal);
        Assert.IsTrue(designScriptIndex >= 0 && designScriptIndex < episodeScriptIndex);
    }

    [TestMethod]
    public void SharedWebLayerRendersInteractiveSubtitleWords()
    {
        var root = FindRepositoryRoot();
        var designScript = File.ReadAllText(
            Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "player-design.js"));
        var learningScript = File.ReadAllText(
            Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "player-learning-design.js"));
        var episodeScript = File.ReadAllText(
            Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "episode-player.js"));

        Assert.IsFalse(designScript.Contains("token.isInteractive", StringComparison.Ordinal));
        StringAssert.Contains(learningScript, "token.isInteractive");
        StringAssert.Contains(learningScript, "actions.openWord");
        StringAssert.Contains(learningScript, "actions.learnCurrentCue");
        StringAssert.Contains(episodeScript, "window.JularrPlayerLearning.renderCue");
        StringAssert.Contains(episodeScript, "design.actions.repeatCurrentCue");
        StringAssert.Contains(learningScript, "learningResumeOnClose");
        StringAssert.Contains(episodeScript, "JularrPlayerLearning?.attachInspector");
    }

    [TestMethod]
    public void LearningRendererIsNotLoadedForNormalPlayback()
    {
        var root = FindRepositoryRoot();
        var pages = Path.Combine(root, "src", "Jularr.Web", "Pages", "Library");
        var scripts = File.ReadAllText(Path.Combine(pages, "_VideoPlayerScripts.cshtml"));
        var stage = File.ReadAllText(Path.Combine(pages, "_VideoPlayerStage.cshtml"));
        var watch = File.ReadAllText(Path.Combine(pages, "Watch.cshtml"));
        var episode = File.ReadAllText(Path.Combine(pages, "Episode.cshtml"));
        var player = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "episode-player.js"));

        StringAssert.Contains(scripts, "@if (Model)");
        StringAssert.Contains(scripts, "~/js/player-learning-design.js");
        StringAssert.Contains(watch, "model=\"@(stage.ShowPlayerTools || stage.Controls?.HasLearningCues == true)\"");
        StringAssert.Contains(episode, "model=\"@(stage.ShowPlayerTools || stage.Controls?.HasLearningCues == true)\"");
        StringAssert.Contains(episode, "@if (stage.ShowPlayerTools)");
        StringAssert.Contains(stage, "@if (Model.ShowPlayerTools || Model.Controls?.HasLearningCues == true)");
        StringAssert.Contains(stage, "@if (Model.Controls is { HasLearningCues: true })");
        foreach (var selector in new[]
                 {
                     "data-playback-video", "data-playback-subtitle", "data-secondary-playback-subtitle",
                     "data-primary-positioned-subtitles", "data-secondary-positioned-subtitles"
                 })
        {
            StringAssert.Contains(stage, selector);
        }
        StringAssert.Contains(player, "data?.textContent || \"[]\"");
        StringAssert.Contains(player, "if (!overlay || !window.JularrPlayerLearning) return;");
    }

    [TestMethod]
    public void BaseDesignWithoutLearning_PreservesPlaybackActionsAndOverlappingCues()
    {
        var root = FindRepositoryRoot();
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(5)));
        engine.Execute("var window = globalThis;");
        engine.Execute(File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "player-design.js")));

        Assert.AreEqual("undefined", engine.Evaluate("typeof window.JularrPlayerLearning").ToString());
        Assert.AreEqual("undefined", engine.Evaluate("typeof window.JularrPlayerDesign.renderCue").ToString());
        Assert.AreEqual("playPause", engine.Evaluate("window.JularrPlayerDesign.actions.playPause").ToString());
        Assert.AreEqual("2", engine.Evaluate("""
            window.JularrPlayerDesign.activeCuesAt([
                {startMs: 1000, endMs: 3000, text: "Sign"},
                {startMs: 1500, endMs: 2500, text: "Dialogue"}
            ], 2000).length
            """).ToString());
        Assert.AreEqual("1000", engine.Evaluate("""
            window.JularrPlayerDesign.cueIndexAt([
                {startMs: 1000, endMs: 3000},
                {startMs: 5000, endMs: 7000}
            ], 2000) === 0 ? "1000" : "invalid"
            """).ToString());
        Assert.AreEqual("10,30", engine.Evaluate("""
            (() => {
                const seek = window.JularrPlayerDesign.seekSeconds({
                    dataset: {seekBackSeconds: "10", seekForwardSeconds: "30"}
                });
                return [seek.back, seek.forward].join(",");
            })()
            """).ToString());
    }

    [TestMethod]
    public void LearningAddonEnabled_RendersTokensWithoutChangingPlaybackDesign()
    {
        var root = FindRepositoryRoot();
        var scripts = Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js");
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(5)));
        engine.Execute("var window = globalThis;");
        engine.Execute(File.ReadAllText(Path.Combine(scripts, "player-design.js")));
        engine.Execute(File.ReadAllText(Path.Combine(scripts, "player-learning-design.js")));
        engine.Execute("""
            var element = () => ({
                dataset: {}, attributes: {}, children: [], hidden: true,
                setAttribute(name, value) { this.attributes[name] = value; },
                removeAttribute(name) { delete this.attributes[name]; },
                addEventListener() {},
                append(...items) { this.children.push(...items); },
                replaceChildren(...items) { this.children = items; }
            });
            var document = { createElement: () => element() };
            var root = element(), overlay = element();
            window.JularrPlayerLearning.renderCue(root, overlay, {
                startMs: 1000,
                tokens: [{ surface: "行く", isInteractive: true }, { surface: "!", isInteractive: false }]
            });
            """);

        Assert.AreEqual("false", engine.Evaluate("String(overlay.hidden)").ToString());
        Assert.AreEqual("3", engine.Evaluate("String(overlay.children[0].children.length)").ToString());
        Assert.AreEqual("openWord", engine.Evaluate("overlay.children[0].children[0].dataset.playerAction").ToString());
        Assert.AreEqual("learnCurrentCue", engine.Evaluate("overlay.children[0].children[2].dataset.playerAction").ToString());
        Assert.AreEqual("2", engine.Evaluate("""
            String(window.JularrPlayerDesign.activeCuesAt([
                {startMs: 1000, endMs: 3000}, {startMs: 1500, endMs: 2500}
            ], 2000).length)
            """).ToString());

        engine.Execute("window.JularrPlayerLearning.renderCue(root, overlay, null);");
        Assert.AreEqual("true", engine.Evaluate("String(overlay.hidden)").ToString());
        Assert.AreEqual("0", engine.Evaluate("String(overlay.children.length)").ToString());
    }

    [TestMethod]
    public void EpisodePlayerWiresTheSharedLanguageInspector()
    {
        // #233: the player opens the shared inspector for subtitle taps when
        // it is available, and only falls back to its own learning sheet
        // otherwise; it also mirrors state changes back into the cached cues.
        var root = FindRepositoryRoot();
        var episodeScript = File.ReadAllText(
            Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "episode-player.js"));

        var learningScript = File.ReadAllText(
            Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "player-learning-design.js"));

        Assert.IsFalse(episodeScript.Contains("window.JularrLanguageInspector", StringComparison.Ordinal));
        StringAssert.Contains(learningScript, "window.JularrLanguageInspector");
        StringAssert.Contains(learningScript, "sharedInspector.open(");
        StringAssert.Contains(learningScript, "sharedInspector.addEventListener(\"open\"");
        StringAssert.Contains(learningScript, "sharedInspector.addEventListener(\"close\"");
        StringAssert.Contains(learningScript, "sharedInspector.addEventListener(\"statechange\"");
    }

    [TestMethod]
    public void LearningInspectorEvents_DoNotChangeBaseTransportOrPlayback()
    {
        var root = FindRepositoryRoot();
        var scripts = Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js");
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(5)));
        engine.Execute("var window = globalThis;");
        engine.Execute(File.ReadAllText(Path.Combine(scripts, "player-design.js")));
        engine.Execute(File.ReadAllText(Path.Combine(scripts, "player-learning-design.js")));
        engine.Execute("""
            var HTMLElement = function HTMLElement() {};
            var makeElement = () => ({
                hidden: true, textContent: "", listeners: {},
                querySelectorAll() { return []; },
                addEventListener(type, listener) { (this.listeners[type] ||= []).push(listener); },
                emit(type, event) { (this.listeners[type] || []).forEach(listener => listener(event)); },
                focus() {}
            });
            var root = makeElement(), overlay = makeElement(), inspector = makeElement();
            var video = {
                paused: false, ended: false, pauses: 0, plays: 0,
                pause() { this.paused = true; this.pauses++; },
                play() { this.paused = false; this.plays++; return { catch() {} }; }
            };
            var handlers = {};
            window.JularrLanguageInspector = {
                available: true,
                addEventListener(type, handler) { handlers[type] = handler; },
                open(text, context) { this.last = { text, context }; }
            };
            var cue = { startMs: 1500, tokens: [{ surface: "駅", canonical: "駅", state: "New" }] };
            var redraws = 0;
            var learning = window.JularrPlayerLearning.attachInspector({
                root, overlay, inspector, video, design: window.JularrPlayerDesign,
                learningKicker: makeElement(), word: makeElement(), reading: makeElement(),
                meaning: makeElement(), state: makeElement(), replay: makeElement(),
                closeLearning: makeElement(),
                getCues: () => [cue],
                getActiveIndex: () => 0,
                renderActiveCue: () => redraws++
            });
            root.emit(window.JularrPlayerDesign.actionEvent, {
                detail: { action: "openWord", cue, token: cue.tokens[0] }
            });
            handlers.open();
            handlers.statechange({ detail: { text: "駅", state: "Known" } });
            handlers.close();
            """);

        Assert.AreEqual("駅", engine.Evaluate("window.JularrLanguageInspector.last.text").ToString());
        Assert.AreEqual("1500", engine.Evaluate("String(window.JularrLanguageInspector.last.context.cueStartMs)").ToString());
        Assert.AreEqual("Known", engine.Evaluate("cue.tokens[0].state").ToString());
        Assert.AreEqual("1", engine.Evaluate("String(redraws)").ToString());
        Assert.AreEqual("1,1", engine.Evaluate("[video.pauses, video.plays].join(',')").ToString());
        Assert.AreEqual("false", engine.Evaluate("String(learning.isSheetOpen())").ToString());
        Assert.AreEqual("1500", engine.Evaluate("String(learning.selectedCueStartMs())").ToString());
        Assert.AreEqual("playPause", engine.Evaluate("window.JularrPlayerDesign.actions.playPause").ToString());

        engine.Execute("""
            window.JularrLanguageInspector.available = false;
            var fallbackRoot = makeElement(), fallbackOverlay = makeElement(), fallbackSheet = makeElement();
            var fallbackVideo = {
                paused: false, ended: false, pauses: 0, plays: 0,
                pause() { this.paused = true; this.pauses++; },
                play() { this.paused = false; this.plays++; return { catch() {} }; }
            };
            var fallback = window.JularrPlayerLearning.attachInspector({
                root: fallbackRoot, overlay: fallbackOverlay, video: fallbackVideo, inspector: fallbackSheet,
                design: window.JularrPlayerDesign,
                learningKicker: makeElement(), word: makeElement(), reading: makeElement(),
                meaning: makeElement(), state: makeElement(), replay: makeElement(),
                closeLearning: makeElement(), getCues: () => [cue],
                getActiveIndex: () => 0, renderActiveCue: () => {}
            });
            fallbackRoot.emit(window.JularrPlayerDesign.actionEvent, {
                detail: { action: "learnCurrentCue", cue }
            });
            var sheetOpened = fallback.isSheetOpen();
            fallbackRoot.emit(window.JularrPlayerDesign.actionEvent, {
                detail: { action: "closeOverlay" }
            });
            """);
        Assert.AreEqual("true", engine.Evaluate("String(sheetOpened)").ToString());
        Assert.AreEqual("false", engine.Evaluate("String(fallback.isSheetOpen())").ToString());
        Assert.AreEqual("1,1", engine.Evaluate("[fallbackVideo.pauses, fallbackVideo.plays].join(',')").ToString());
    }

    [TestMethod]
    public void BasePlayerActions_KeepSeekingAndRepeatingWithoutLearning()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "episode-player.js"));
        var start = source.IndexOf("    root.addEventListener(design.actionEvent, event => {",
            source.IndexOf("    const learningInspector =", StringComparison.Ordinal), StringComparison.Ordinal);
        var end = source.IndexOf("    root.querySelectorAll(\"[data-player-controls]", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);

        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(5)));
        engine.Execute("var window = globalThis;");
        engine.Execute(File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "player-design.js")));
        engine.Execute("""
            var calls = [];
            var listeners = {};
            var root = {
                addEventListener(type, listener) { listeners[type] = listener; },
                emit(action, detail = {}) {
                    listeners[window.JularrPlayerDesign.actionEvent]({ detail: { action, ...detail } });
                }
            };
            var design = window.JularrPlayerDesign;
            var learningInspector = null;
            var currentLineStartMs = () => 1200;
            var seekBase = () => 10;
            var seekSeconds = { back: 10, forward: 30 };
            var seekToAbsolute = (at) => calls.push(at);
            """);
        engine.Execute(source[start..end]);
        Assert.AreEqual("1.2,0,40,7.5", engine.Evaluate("""
            (() => {
                root.emit("repeatCurrentCue");
                root.emit("seekBack10");
                root.emit("seekForward10");
                root.emit("seekTo", { seconds: 7.5 });
                root.emit("seekTo", { seconds: NaN });
                return calls.join(",");
            })()
            """).ToString());
        Assert.AreEqual("1.2,0,40,7.5,1.5", engine.Evaluate("""
            (() => {
                learningInspector = {
                    isSheetOpen: () => true,
                    selectedCueStartMs: () => 1500,
                    close: () => {}
                };
                root.emit("repeatCurrentCue");
                return calls.join(",");
            })()
            """).ToString());
    }

    [TestMethod]
    public void PlaybackSpeedsAndSeekIncrementsComeFromTheCanonicalTokens()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "design", "player", "player-tokens.json")));
        var playback = document.RootElement.GetProperty("playback");
        var speeds = playback.GetProperty("speeds").EnumerateArray().Select(x => x.GetDouble()).ToArray();

        CollectionAssert.AreEqual(speeds, Jularr.Web.Features.Playback.PlayerDesign.PlaybackSpeeds.ToArray(),
            "The server must read speeds from the embedded design tokens, not a copy.");
        CollectionAssert.AreEqual(speeds, Jularr.Web.Features.Progress.PlaybackPreferenceRules.Speeds.ToArray());
        Assert.AreEqual(0.5, speeds.Min());
        Assert.AreEqual(2.0, speeds.Max());
        CollectionAssert.Contains(speeds, 1.0);
        Assert.AreEqual(10, Jularr.Web.Features.Playback.PlayerDesign.SeekBackSeconds, "SPEC: manual seek is 10 seconds back ...");
        Assert.AreEqual(30, Jularr.Web.Features.Playback.PlayerDesign.SeekForwardSeconds, "... and 30 seconds forward.");
        Assert.AreEqual(playback.GetProperty("seekBackSeconds").GetInt32(), Jularr.Web.Features.Playback.PlayerDesign.SeekBackSeconds);
        Assert.AreEqual(playback.GetProperty("seekForwardSeconds").GetInt32(), Jularr.Web.Features.Playback.PlayerDesign.SeekForwardSeconds);
        Assert.IsFalse(playback.TryGetProperty("seekStepSeconds", out _), "There is no single symmetric seek step.");
    }

    [TestMethod]
    public void NativeAndroidPlayersReadTheSeekIncrementsFromTheTokensInsteadOfHardcodingThem()
    {
        var root = FindRepositoryRoot();
        foreach (var file in new[]
                 {
                     Path.Combine("app-tv", "src", "main", "kotlin", "de", "juloc", "jularr", "tv", "TvPlayerScreen.kt"),
                     Path.Combine("app-tv", "src", "main", "kotlin", "de", "juloc", "jularr", "tv", "TvPlayerInteraction.kt"),
                     Path.Combine("app-mobile", "src", "main", "kotlin", "de", "juloc", "jularr", "mobile", "NativePlayerScreen.kt")
                 })
        {
            var source = File.ReadAllText(Path.Combine(root, "clients", "android", file));
            Assert.IsFalse(
                System.Text.RegularExpressions.Regex.IsMatch(source, @"(seekBy|SeekBy)(-?10_000)|currentPosition [-+] 10_000"),
                $"{file} must seek by design.seek (10 s back, 30 s forward), not a hardcoded step.");
            StringAssert.Contains(source, "seek.");
        }
    }

    [TestMethod]
    public void WebPlayerControlsUseCanonicalIconsTokensAndActions()
    {
        var root = FindRepositoryRoot();
        using var icons = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "design", "player", "player-icons.json")));
        using var tokens = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "design", "player", "player-tokens.json")));
        using var actions = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "design", "player", "player-actions.json")));

        var iconIds = icons.RootElement.GetProperty("icons").EnumerateObject().Select(x => x.Name).ToArray();
        var page = EpisodePlayerSource.Read(root);
        var usedIcons = System.Text.RegularExpressions.Regex
            .Matches(page, "_PlayerIcon\" model=\"@\\(\"(?<id>[A-Za-z0-9]+)\"\\)")
            .Select(x => x.Groups["id"].Value)
            .ToArray();

        Assert.IsTrue(usedIcons.Length >= 7, "Player controls render their icons from player-icons.json.");
        foreach (var id in usedIcons)
        {
            CollectionAssert.Contains(iconIds, id);
            Assert.AreEqual(
                icons.RootElement.GetProperty("icons").GetProperty(id).GetProperty("path").GetString(),
                Jularr.Web.Features.Playback.PlayerDesign.Icon(id).Path);
        }

        var partial = File.ReadAllText(
            Path.Combine(root, "src", "Jularr.Web", "Pages", "Shared", "_PlayerIcon.cshtml"));
        StringAssert.Contains(partial, "PlayerDesign.Icon(Model)");

        foreach (var action in new[] { "seekBack10", "seekForward10", "repeatCurrentCue" })
        {
            StringAssert.Contains(page, $"data-player-action=\"{action}\"");
        }

        StringAssert.Contains(page, "data-playback-speed");
        StringAssert.Contains(page, "data-subtitle-track");
        StringAssert.Contains(page, "data-quality-cap");
        StringAssert.Contains(page, "data-audio-track");

        var designScript = File.ReadAllText(
            Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "player-design.js"));
        foreach (var id in actions.RootElement.GetProperty("actions").EnumerateArray()
                     .Select(x => x.GetProperty("id").GetString()!))
        {
            StringAssert.Contains(designScript, $"{id}: \"{id}\"");
        }

        var css = File.ReadAllText(
            Path.Combine(root, "src", "Jularr.Web", "wwwroot", "css", "player.css"));
        var radius = tokens.RootElement.GetProperty("radiusDp").GetProperty("control").GetInt32();
        var playbackSubtitle = tokens.RootElement.GetProperty("typographySp").GetProperty("playbackSubtitle").GetInt32();
        StringAssert.Contains(css, $"--player-control-radius: {radius}px;");
        StringAssert.Contains(css, $"--player-playback-subtitle-size: {playbackSubtitle}px;");
    }

    // docs/mockups/player section 19: the stage is a dark surface in both application themes. The clean themes colour every select and
    // button light, so the player's own fields and buttons take player tokens and the theme rules leave the stage out; otherwise
    // the settings read white text on a near-white field in the light theme.
    [TestMethod]
    public void PlayerFieldsAndButtonsReadPlayerTokensAndTheThemeLeavesThePlayerStageAlone()
    {
        var root = FindRepositoryRoot();
        var css = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "css", "player.css"));
        var themes = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "css", "theme-catalog.css"));

        StringAssert.Contains(css, "color-scheme: dark;");
        foreach (var token in new[] { "--player-field-bg", "--player-field-text", "--player-field-border", "--player-button-bg", "--player-popup-bg" })
        {
            StringAssert.Contains(css, $"{token}:", $"{token} is defined for the stage.");
        }

        var select = System.Text.RegularExpressions.Regex.Match(css, @"\.player-setting select \{(?<body>[^}]*)\}").Groups["body"].Value;
        StringAssert.Contains(select, "background: var(--player-field-bg)");
        StringAssert.Contains(select, "color: var(--player-field-text)");
        StringAssert.Contains(css, ".player-panel .button { background: var(--player-button-bg)");

        // :where() keeps the exclusion at zero specificity, so it never lifts a theme rule above .button-primary.
        Assert.AreEqual(3, System.Text.RegularExpressions.Regex.Matches(themes, @":not\(:where\(\.player-panel \*\)\)").Count);
        StringAssert.DoesNotMatch(themes, new System.Text.RegularExpressions.Regex(@":not\(\.player-panel \*\)"));
    }

    private static string FindRepositoryRoot()
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
