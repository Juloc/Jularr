using System.Globalization;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Development.Design
{
    public sealed partial class DesignModel
    {
        private IUiComponent DefinePlayerSettings(PlayerSource source)
        {
            // Presentation controls, no service form.
            var audio = new IUiComponent[]
            {
                Tracks("player.audio", "audioTrack", source.AudioTracks ?? [], "0", false),
                Choice("design.player.audioPolicy", "audioPolicy", "preferred", ("preferred", "design.player.preferred"), ("original", "design.player.original")),
                SettingToggle("design.player.audioDescription", "audioDescription"),
                Offset("design.player.audioOffset", "audioOffset")
            };
            var subtitles = new IUiComponent[]
            {
                Choice("design.player.subtitlePolicy", "subtitlePolicy", "forced", ("forced", "design.player.forced"), ("full", "design.player.full"), ("off", "player.subtitles.off")),
                Tracks("design.player.primarySubtitle", "primarySubtitle", source.Subtitles ?? [], "forced"),
                Tracks("design.player.secondarySubtitle", "secondarySubtitle", source.Subtitles ?? [], ""),
                new Details(new Summary(new Text("design.player.appearance")),
                    Choice("design.player.font", "font", "system", ("system", "design.player.systemFont"), ("sans-serif", "design.player.sans"), ("serif", "design.player.serif"), ("monospace", "design.player.monospace")),
                    Range("design.player.fontSize", "fontSize", 24, 14, 48),
                    Range("design.player.bottomOffset", "bottomOffset", 12, 0, 35),
                    Range("design.player.maxWidth", "maxWidth", 85, 40, 95),
                    Field("design.player.textColor", "textColor", new Input().Attribute("type", "color").Attribute("value", "#ffffff")),
                    Range("design.player.textOpacity", "textOpacity", 100, 30, 100),
                    Range("design.player.backgroundOpacity", "backgroundOpacity", 50, 0, 100),
                    Range("design.player.outline", "outline", 1, 0, 4),
                    SettingToggle("design.player.shadow", "shadow", true),
                    SettingToggle("design.player.overrideSigns", "overrideSigns"),
                    new Details(new Summary(new Text("design.player.secondaryStyle")),
                        SettingToggle("design.player.separateStyle", "separateStyle"),
                        Range("design.player.fontSize", "secondaryFontSize", 21, 14, 48),
                        Field("design.player.textColor", "secondaryColor", new Input().Attribute("type", "color").Attribute("value", "#ffe6a8")))
                ).Class("player-settings__details"),
                new Details(new Summary(new Text("design.player.synchronization")),
                    Offset("design.player.primaryOffset", "primaryOffset"), Offset("design.player.secondaryOffset", "secondaryOffset"))
                    .Class("player-settings__details"),
                new Button(new Icon("spark", assets), new Text("design.player.translate")).Compact().Attribute("data-action", "translate-preview")
            };
            var playback = new IUiComponent[]
            {
                Choice("design.player.quality", "quality", "auto", ("auto", "design.player.automatic"), ("original", "design.player.original"),
                    ("1080", "design.player.quality1080"), ("720", "design.player.quality720"), ("480", "design.player.quality480")),
                Choice("design.player.version", "version", "cinema", ("cinema", "design.player.cinema"), ("extended", "design.player.extended")),
                new Div(new Span(new Text("player.speed")), new Div(new[] { "0.75", "1", "1.25", "1.5", "2" }
                    .Select(speed => (IUiComponent)new Button(new ValueText(speed + "×")).Compact().Attribute("data-speed", speed)
                        .Attribute("aria-pressed", speed == "1" ? "true" : "false")).ToArray()).Class("player-settings__presets")),
                Range("player.speed", "speed", 1, .5, 2, .05),
                Choice("design.player.fit", "fit", "contain", ("contain", "design.player.fitInside"), ("cover", "design.player.fill"), ("zoom", "design.player.zoom")),
                Choice("design.player.autoplay", "autoplay", "countdown", ("countdown", "design.player.countdown"), ("instant", "design.player.withoutCountdown"), ("off", "design.player.off")),
                Choice("design.player.intro", "intro", "button", ("button", "design.player.skipButton"), ("auto", "design.player.autoSkip"), ("off", "design.player.off")),
                Choice("design.player.outro", "outro", "button", ("button", "design.player.skipButton"), ("auto", "design.player.autoSkip"), ("off", "design.player.off")),
                Choice("design.player.recap", "recap", "button", ("button", "design.player.skipButton"), ("auto", "design.player.autoSkip"), ("off", "design.player.off")),
                Choice("design.player.sleep", "sleep", "off", ("off", "design.player.off"), ("15", "design.player.minutes15"), ("30", "design.player.minutes30"),
                    ("60", "design.player.minutes60"), ("end", "design.player.untilEnd"), ("custom", "design.player.custom")),
                Field("design.player.customMinutes", "sleepMinutes", new Input().Attribute("type", "number").Attribute("min", "1").Attribute("max", "720").Attribute("value", "45")),
                Choice("design.player.background", "background", "continue", ("continue", "design.player.continuePip"), ("pause", "design.player.pauseBackground")),
                new Button(new Icon("key", assets), new Text("design.player.lock")).Compact().Attribute("data-action", "lock-preview")
            };

            return new Div(
                new Div(
                    new Header(
                        new Div(new NavigationTabs([
                            new("player.audio", "#settings-audio", true), new("player.subtitles", "#settings-subtitles", false),
                            new("design.player.playback", "#settings-playback", false), new("design.player.info", "#settings-info", false)
                        ], "nav.tabs")).Attribute("data-settings-tabs", ""),
                        new Button(new Icon("close", assets)).IconOnly(texts["common.close"]).Attribute("popovertarget", "player-settings").Attribute("popovertargetaction", "hide")),
                    new Div(
                        SettingsPane("audio", "player.audio", audio, true),
                        SettingsPane("subtitles", "player.subtitles", subtitles),
                        SettingsPane("playback", "design.player.playback", playback),
                        SettingsPane("info", "design.player.info", [new P(new Text("design.player.infoNotice")),
                            new Div(new Span(new Text("player.delivery")), new Strong(new ValueText("Direct Play")),
                                new Span(new Text("design.player.source")), new Strong(new ValueText("1080p · H.264 · AAC · 8 Mbit/s")),
                                new Span(new Text("design.player.buffer")), new Strong(new ValueText("Sample · 30 s"))).Class("player-settings__facts"),
                            new Details(new Summary(new Text("design.player.diagnostics")), new P(new Text("design.player.diagnosticsNotice")))])
                    ).Class("player-settings__body"),
                    new Div(new P(new Text("design.player.localOnly")),
                        new Button(new Text("design.player.saveDefaults")).Primary().Disabled().Attribute("data-action", "save-defaults"))
                        .Class("player-settings__footer"),
                    new P(new Text("design.player.localOnly")).Attribute("data-settings-feedback", "").Attribute("role", "status")
                ).Class("player-settings").Attribute("id", "player-settings").Attribute("popover", "auto").Attribute("role", "dialog").Attribute("aria-label", texts["design.player.settings"]),
                new Div(new H2(new Text("design.player.cast")), new P(new Text("design.player.castNotice")),
                    new Button(new Icon("devices", assets), new Text("design.player.thisDevice")).Attribute("data-action", "cast-preview"),
                    new Button(new Icon("devices", assets), new Text("design.player.sampleTv")).Attribute("data-action", "cast-preview"))
                    .Class("player-settings player-settings--devices").Attribute("id", "player-cast").Attribute("popover", "auto").Attribute("aria-label", texts["design.player.cast"]),
                new Div(new Span(new Text("design.player.savedPreview")).Attribute("data-message", "saved"),
                    new Span(new Text("design.player.localOnly")).Attribute("data-message", "local")).Attribute("hidden", "hidden")
            );
        }

        private IUiComponent SettingsPane(string name, string title, IUiComponent[] content, bool selected = false)
        {
            var pane = new Section(content).Class("player-settings__pane").Attribute("id", "settings-" + name).Attribute("aria-label", texts[title]);
            if (!selected) pane.Attribute("hidden", "hidden");
            return pane;
        }

        // Reuse the shared field surfaces.
        private IUiComponent Field(string label, string name, HtmlElement control)
        {
            var id = "preview-" + name;
            control.Attribute("id", id).Attribute("name", name).Attribute("data-preview-setting", "");
            return new Fieldset(new Legend(new Label(new Text(label)).Attribute("for", id)), control).Class("ui-field__control");
        }

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
            Field(label, name, new Input().Attribute("type", "range").Attribute("value", value.ToString(CultureInfo.InvariantCulture))
                .Attribute("min", min.ToString(CultureInfo.InvariantCulture)).Attribute("max", max.ToString(CultureInfo.InvariantCulture)).Attribute("step", step.ToString(CultureInfo.InvariantCulture))),
            new Span(new ValueText(value.ToString(CultureInfo.InvariantCulture))).Attribute("data-range-value", name)).Class("player-settings__range");

        private IUiComponent SettingToggle(string label, string name, bool selected = false)
        {
            var toggle = new Toggle(label, selected);
            toggle.Control.Attribute("name", name).Attribute("data-preview-setting", "");
            return toggle;
        }

        private IUiComponent Offset(string label, string name) => new Div(
            Field(label, name, new Input().Attribute("type", "number").Attribute("value", "0").Attribute("min", "-60000").Attribute("max", "60000").Attribute("step", "50")),
            new Div(new Button(new Text("design.player.earlier")).Compact().Attribute("data-offset", name).Attribute("data-delta", "-50"),
                new Button(new Text("design.player.later")).Compact().Attribute("data-offset", name).Attribute("data-delta", "50"),
                new Button(new Text("design.player.reset")).Compact().Attribute("data-offset", name).Attribute("data-delta", "0")).Class("player-settings__presets"))
            .Class("player-settings__offset");

        private sealed class Legend(params IUiComponent[] children) : HtmlElement("legend", children);
        private sealed class Label(params IUiComponent[] children) : HtmlElement("label", children);
        private sealed class Select(params IUiComponent[] children) : HtmlElement("select", children);
        private sealed class Option(params IUiComponent[] children) : HtmlElement("option", children);
        private sealed class Strong(params IUiComponent[] children) : HtmlElement("strong", children);
    }
}
