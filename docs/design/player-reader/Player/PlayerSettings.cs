using System.Globalization;
using UiDesigner.Framework;

namespace UiDesigner.Projects.Jularr.Player
{
    public sealed partial class PlayerPreview
    {
        private int _playerFieldIndex;

        private IUiComponent DefinePlayerSettings(PlayerSource source)
        {
            // Compact controls, no persistence.
            return new Div(
                new Div(
                    new Header(ScopePicker(),
                        new Button(new Icon("player-fullscreen", assets)).IconOnly(texts["design.player.allSettings"]).Attribute("data-action", "settings-page"),
                        new Button(new Icon("close", assets)).IconOnly(texts["common.close"]).Attribute("popovertarget", "player-settings").Attribute("popovertargetaction", "hide")),
                    new Div(
                        SettingsPane("basic", "design.player.basic", [AudioTrack(source), PrimaryTrack(source), SecondaryTrack(source), Speed(), Quality()], true),
                        SettingsPane("audio", "player.audio", AudioControls(source)),
                        SettingsPane("subtitles", "player.subtitles", [PrimaryTrack(source), SecondaryTrack(source),
                            Offset("design.player.primaryOffset", "primaryOffset"), Offset("design.player.secondaryOffset", "secondaryOffset"),
                            Range("design.player.fontSize", "fontSize", 24, 14, 48), Range("design.player.bottomOffset", "bottomOffset", 12, 0, 35)]),
                        SettingsPane("playback", "design.player.playback", [Quality(), Speed(), PictureFit(), Autoplay(),
                            new Div(Skip("intro"), Skip("recap"), Skip("outro")).Class("player-settings__skip")]),
                        SettingsPane("info", "design.player.info", InfoControls())
                    ).Class("player-settings__body"), SettingsTabs("settings", true)
                ).Class("player-settings player-settings--preferences").Attribute("id", "player-settings").Attribute("popover", "auto")
                    .Attribute("role", "dialog").Attribute("aria-label", texts["design.player.settings"]).Attribute("data-popover-screen", ""),
                new Div(new Header(new H2(new Text("design.player.cast")),
                    new Button(new Icon("close", assets)).IconOnly(texts["common.close"]).Attribute("popovertarget", "player-cast").Attribute("popovertargetaction", "hide")),
                    new P(new Text("design.player.castNotice")))
                    .Class("player-settings player-settings--devices").Attribute("id", "player-cast").Attribute("popover", "auto").Attribute("aria-label", texts["design.player.cast"]),
                new Span(new Text("design.player.overridden")).Attribute("data-setting-message", "overridden").Attribute("hidden", "hidden")
            );
        }

        private IUiComponent DefineFullPlayerSettings(PlayerSource source)
        {
            // One preview, shared field definitions.
            return new Section(
                new Header(new Button(new Icon("chevron-left", assets), new Text("design.player.backToPlayer")).Attribute("data-action", "settings-close"), ScopePicker()),
                new Split(split =>
                {
                    split.Primary.Add(new Div(SettingsTabs("full", false),
                        SettingsPane("audio", "player.audio", [.. AudioControls(source),
                            Group("design.player.advanced", Range("design.player.amplification", "amplification", 100, 100, 200),
                                Choice("design.player.downmix", "downmix", "auto", ("auto", "design.player.automatic"), ("stereo", "design.player.stereo")))], true, "full"),
                        SettingsPane("subtitles", "player.subtitles", [PrimaryTrack(source), SecondaryTrack(source),
                            Choice("design.player.subtitlePolicy", "subtitlePolicy", "forced", ("forced", "design.player.forced"), ("full", "design.player.full"), ("off", "player.subtitles.off")),
                            Group("design.player.appearance", Range("design.player.fontSize", "fontSize", 24, 14, 48),
                                Range("design.player.bottomOffset", "bottomOffset", 12, 0, 35), Range("design.player.maxWidth", "maxWidth", 85, 40, 95),
                                Choice("design.player.font", "font", "system", ("system", "design.player.systemFont"), ("sans-serif", "design.player.sans"), ("serif", "design.player.serif"), ("monospace", "design.player.monospace")),
                                Color("design.player.textColor", "textColor", "#ffffff"), Range("design.player.textOpacity", "textOpacity", 100, 30, 100),
                                Range("design.player.backgroundOpacity", "backgroundOpacity", 50, 0, 100), Range("design.player.outline", "outline", 1, 0, 4),
                                SettingToggle("design.player.shadow", "shadow", true), SettingToggle("design.player.overrideSigns", "overrideSigns")),
                            Group("design.player.secondaryStyle", SettingToggle("design.player.separateStyle", "separateStyle"),
                                Range("design.player.fontSize", "secondaryFontSize", 21, 14, 48), Color("design.player.textColor", "secondaryColor", "#ffe6a8")),
                            Group("design.player.synchronization", Offset("design.player.primaryOffset", "primaryOffset"), Offset("design.player.secondaryOffset", "secondaryOffset"),
                                Range("design.player.primaryRate", "primaryRate", 100, 80, 120, .1), Range("design.player.secondaryRate", "secondaryRate", 100, 80, 120, .1))], prefix: "full"),
                        SettingsPane("playback", "design.player.playback", [Quality(), Speed(), Autoplay(),
                            Group("design.player.skipControls", Skip("intro"), Skip("recap"), Skip("outro")),
                            Group("design.player.advanced",
                                Choice("design.player.version", "version", "cinema", ("cinema", "design.player.cinema"), ("extended", "design.player.extended")),
                                Choice("design.player.sleep", "sleep", "off", ("off", "design.player.off"), ("15", "design.player.minutes15"), ("30", "design.player.minutes30"),
                                    ("60", "design.player.minutes60"), ("end", "design.player.untilEnd"), ("custom", "design.player.custom")),
                                Field("design.player.customMinutes", "sleepMinutes", Number(45, 1, 720, 1)),
                                Choice("design.player.background", "background", "continue", ("continue", "design.player.continuePip"), ("pause", "design.player.pauseBackground")),
                                new P(new Text("design.player.runtimeRequired")),
                                new Div(new Button(new Text("design.player.previousFrame")).Attribute("data-action", "frame-back"),
                                    new Button(new Text("design.player.nextFrame")).Attribute("data-action", "frame-next")).Class("player-settings__presets"),
                                SettingToggle("design.player.abLoop", "abLoop"), Field("design.player.loopStart", "loopStart", Number(0, 0, 1450, .1)),
                                Field("design.player.loopEnd", "loopEnd", Number(30, 0, 1450, .1)))], prefix: "full"),
                        SettingsPane("picture", "design.player.picture", [PictureFit(),
                            Group("design.player.pictureAdjustments", Range("design.player.zoom", "zoom", 100, 100, 200),
                                Range("design.player.brightness", "brightness", 100, 50, 150), Range("design.player.contrast", "contrast", 100, 50, 150),
                                Range("design.player.saturation", "saturation", 100, 0, 200))], prefix: "full"),
                        SettingsPane("info", "design.player.info", [.. InfoControls(),
                            Group("design.player.diagnostics", new P(new Text("design.player.diagnosticsNotice")))], prefix: "full"), CopyControls()
                    ).Class("player-preferences__controls"));
                    split.Secondary.Add(new Aside(new P(new Text("design.player.preview")), new Div().Attribute("data-settings-preview", "")).Class("player-preferences__preview"));
                })
            ).Class("player-preferences").Attribute("data-settings-page", "").Attribute("id", "player-settings-page").Attribute("hidden", "hidden");
        }

        private IUiComponent[] AudioControls(PlayerSource source) => [AudioTrack(source), Offset("design.player.audioOffset", "audioOffset"),
            Choice("design.player.audioPolicy", "audioPolicy", "preferred", ("preferred", "design.player.preferred"), ("original", "design.player.original")),
            SettingToggle("design.player.audioDescription", "audioDescription")];

        private IUiComponent AudioTrack(PlayerSource source) => Tracks("player.audio", "audioTrack", source.AudioTracks ?? [], "0", false);
        private IUiComponent PrimaryTrack(PlayerSource source) => Tracks("design.player.primarySubtitle", "primarySubtitle", source.Subtitles ?? [], "forced");
        private IUiComponent SecondaryTrack(PlayerSource source) => Tracks("design.player.secondarySubtitle", "secondarySubtitle", source.Subtitles ?? [], "");
        private IUiComponent Quality() => Choice("design.player.quality", "quality", "auto", ("auto", "design.player.automatic"), ("original", "design.player.original"),
            ("1080", "design.player.quality1080"), ("720", "design.player.quality720"), ("480", "design.player.quality480"));
        private IUiComponent Autoplay() => Choice("design.player.autoplay", "autoplay", "countdown", ("countdown", "design.player.countdown"), ("instant", "design.player.withoutCountdown"), ("off", "design.player.off"));
        private IUiComponent Skip(string part) => Choice("design.player." + part, part, "button", ("button", "design.player.skipButton"), ("auto", "design.player.autoSkip"), ("off", "design.player.off"));
        private IUiComponent Group(string title, params IUiComponent[] content) => new Details([new Summary(new Text(title)), .. content]).Class("player-settings__details");
        private IUiComponent Color(string title, string name, string value) => Field(title, name, new Input().Attribute("type", "color").Attribute("value", value));

        private IUiComponent ScopePicker()
        {
            var id = "preview-scope-" + (++_playerFieldIndex).ToString(CultureInfo.InvariantCulture);
            return new Fieldset(new Legend(new Label(new Text("design.player.scope")).Attribute("for", id)),
                new Select(new[] { "current", "series", "type", "profile", "admin" }.Select(scope => (IUiComponent)new Option(new Text("design.player.scope." + scope)).Attribute("value", scope)).ToArray())
                    .Attribute("id", id).Attribute("data-setting-scope", "")).Class("ui-field__control player-settings__scope");
        }

        private IUiComponent SettingsTabs(string prefix, bool basic)
        {
            var tabs = new List<NavigationTab>();
            if (basic) tabs.Add(new("design.player.basic", "#" + prefix + "-basic", true));
            tabs.AddRange([new("player.audio", "#" + prefix + "-audio", !basic), new("player.subtitles", "#" + prefix + "-subtitles", false), new("design.player.playback", "#" + prefix + "-playback", false)]);
            if (!basic) tabs.Add(new("design.player.picture", "#" + prefix + "-picture", false));
            tabs.Add(new("design.player.info", "#" + prefix + "-info", false));
            return new Div(new NavigationTabs(tabs, "nav.tabs")).Attribute("data-settings-tabs", prefix);
        }

        private IUiComponent SettingsPane(string name, string title, IUiComponent[] content, bool selected = false, string prefix = "settings")
        {
            var pane = new Section(content).Class("player-settings__pane").Attribute("id", prefix + "-" + name).Attribute("aria-label", texts[title]);
            if (!selected) pane.Attribute("hidden", "hidden");
            return pane;
        }

        private IUiComponent[] InfoControls() => [new P(new Text("design.player.infoNotice")),
            new Div(new Span(new Text("design.player.source")), new Strong(new ValueText("1080p · H.264 · 24 FPS · SDR")),
                new Span(new Text("player.audio")), new Strong(new ValueText("AAC · Stereo")),
                new Span(new Text("design.player.quality")), new Strong(new ValueText("8 Mbit/s")),
                new Span(new Text("player.delivery")), new Strong(new Text("player.mode.DirectPlay"))).Class("player-settings__facts")];

        private IUiComponent CopyControls() => Group("design.player.takeValues",
            new Fieldset(new Legend(new Label(new Text("design.player.copySource")).Attribute("for", "preview-copy-source")),
                new Select(new[] { "series", "type", "profile" }.Select(scope => (IUiComponent)new Option(new Text("design.player.scope." + scope)).Attribute("value", scope)).ToArray())
                    .Attribute("id", "preview-copy-source").Attribute("data-copy-source", "")).Class("ui-field__control"),
            new Div(new Button(new Text("design.player.copyTab")).Attribute("data-copy", "tab"), new Button(new Text("design.player.copyAll")).Attribute("data-copy", "all"),
                new Button(new Text("design.player.copyGroups")).Attribute("data-copy", "groups")).Class("player-settings__presets"),
            new Div(new Toggle("player.audio", true), new Toggle("player.subtitles", true), new Toggle("design.player.playback", true), new Toggle("design.player.picture", true)).Attribute("data-copy-groups", ""));

        // Shared surfaces, unique label IDs.
        private IUiComponent Field(string label, string name, HtmlElement control, bool reset = true)
        {
            var id = "preview-" + name + "-" + (++_playerFieldIndex).ToString(CultureInfo.InvariantCulture);
            control.Attribute("id", id).Attribute("name", name).Attribute("data-preview-setting", "");
            var legend = new List<IUiComponent> { new Label(new Text(label)).Attribute("for", id) };
            if (reset) legend.Add(new Small(new Text("design.player.inherited")).Attribute("data-setting-origin", name).Class("player-settings__origin"));
            return new Div([new Fieldset(new Legend([.. legend]), control).Class("ui-field__control"), .. reset ? new[] { Reset(name) } : []])
                .Class("player-settings__field").Attribute("data-setting-row", name);
        }

        private IUiComponent Reset(string name) => new Button(new Icon("history", assets)).IconOnly(texts["design.player.resetInherited"])
            .Attribute("data-reset-setting", name).Attribute("hidden", "hidden");

        private IUiComponent Choice(string label, string name, string selected, params (string Value, string Key)[] options) =>
            Field(label, name, new Select(options.Select(option =>
            {
                var item = new Option(new Text(option.Key)).Attribute("value", option.Value);
                if (option.Value == selected) item.Attribute("selected", "selected");
                return (IUiComponent)item;
            }).ToArray()));

        private IUiComponent Tracks(string label, string name, IReadOnlyList<PlayerTrack> tracks, string selected, bool allowOff = true) =>
            Field(label, name, new Select((allowOff ? new[] { new Option(new Text("player.subtitles.off")).Attribute("value", "") } : Array.Empty<HtmlElement>())
                .Concat(tracks.Select(track =>
                {
                    var item = new Option(new ValueText(track.Label)).Attribute("value", track.Key);
                    if (track.Key == selected) item.Attribute("selected", "selected");
                    return item;
                })).Cast<IUiComponent>().ToArray()));

        private IUiComponent Range(string label, string name, double value, double min, double max, double step = 1) => new Div(
            Field(label, name, new Input().Attribute("type", "range").Attribute("value", Invariant(value)).Attribute("min", Invariant(min)).Attribute("max", Invariant(max)).Attribute("step", Invariant(step))),
            new Span(new ValueText(Invariant(value))).Attribute("data-range-value", name)).Class("player-settings__range");

        private IUiComponent Speed()
        {
            var presets = new[] { .25, .5, .75, 1, 1.25, 1.5, 2, 3, 4 };
            return new Div(Range("player.speed", "speed", 1, .25, 4, .05), Field("design.player.speedValue", "speed", Number(1, .25, 4, .05), reset: false),
                new Div(presets.Select(value => (IUiComponent)new Button(new ValueText(Invariant(value) + "×")).Compact().Attribute("data-speed", Invariant(value))
                    .Attribute("aria-pressed", value == 1 ? "true" : "false").Attribute("aria-label", texts["player.speed"] + " " + Invariant(value) + "×")).ToArray())
                    .Class("player-settings__speed-points")).Class("player-settings__speed");
        }

        private IUiComponent PictureFit()
        {
            var segments = new[] { (Value: "contain", Key: "design.player.fitInside"), (Value: "cover", Key: "design.player.fill"), (Value: "scale-down", Key: "design.player.originalSize") };
            return new Div(new Fieldset(new Legend(new Text("design.player.fit")), new Div(segments.Select(option => (IUiComponent)new Button(new Text(option.Key))
                .Attribute("data-fit", option.Value).Attribute("aria-pressed", option.Value == "contain" ? "true" : "false")).ToArray()).Class("player-settings__segments"))
                .Class("ui-field__control"), Reset("fit")).Class("player-settings__field").Attribute("data-setting-row", "fit");
        }

        private IUiComponent SettingToggle(string label, string name, bool selected = false)
        {
            var toggle = new Toggle(label, selected);
            toggle.Control.Attribute("name", name).Attribute("data-preview-setting", "");
            return new Div(toggle, Reset(name)).Class("player-settings__field").Attribute("data-setting-row", name);
        }

        private IUiComponent Offset(string label, string name) => new Div(Field(label, name, Number(0, -60000, 60000, 50)),
            new Button(new ValueText("−")).IconOnly(texts["design.player.earlier"]).Attribute("data-offset", name).Attribute("data-delta", "-50"),
            new Button(new ValueText("+")).IconOnly(texts["design.player.later"]).Attribute("data-offset", name).Attribute("data-delta", "50")).Class("player-settings__offset");

        private static HtmlElement Number(double value, double min, double max, double step) => new Input().Attribute("type", "number").Attribute("value", Invariant(value))
            .Attribute("min", Invariant(min)).Attribute("max", Invariant(max)).Attribute("step", Invariant(step));
        private static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);

        private sealed class Legend(params IUiComponent[] children) : HtmlElement("legend", children);
        private sealed class Fieldset(params IUiComponent[] children) : HtmlElement("fieldset", children);
        private sealed class Label(params IUiComponent[] children) : HtmlElement("label", children);
        private sealed class Select(params IUiComponent[] children) : HtmlElement("select", children);
        private sealed class Option(params IUiComponent[] children) : HtmlElement("option", children);
    }
}
