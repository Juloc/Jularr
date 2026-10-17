using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // One settings surface for the live video element. All playback decisions remain server-owned.
    public sealed class UiPlayerSettings(PlayerSource source, FrontendAssets assets) : IUiComponent
    {
        private static readonly string[] Speeds = ["0.25", "0.5", "0.75", "1", "1.25", "1.5", "2", "3", "4"];

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            new Div(
                new Div(
                    new H2(new Text("player.settings")).Class("player__settings-title"),
                    new Button(new Icon("close", assets)).Class("player__button player__settings-close")
                        .Attribute("type", "button").Attribute("data-action", "settings-close")
                        .Attribute("aria-label", texts["player.settings.close"])
                ).Class("player__settings-header"),
                new Div(
                    Tab("speed", "player.speed", true),
                    Tab("audio", "player.audio"),
                    Tab("subtitles", "player.subtitles"),
                    Tab("info", "player.delivery")
                ).Class("player__settings-tabs").Attribute("role", "group").Attribute("aria-label", texts["player.settings"]),
                new Div(
                    Pane("speed", "player.speed", [
                        new Div(
                            new Input().Class("player__speed-range").Attribute("type", "range")
                                .Attribute("min", "0.25").Attribute("max", "4").Attribute("step", "0.05")
                                .Attribute("value", "1").Attribute("data-role", "speed-control")
                                .Attribute("aria-label", texts["player.speed"]),
                            new Span(new ValueText("1×")).Class("player__speed-current").Attribute("data-role", "speed-current")
                        ).Class("player__speed-adjust"),
                        new Div([.. Speeds.Select(speed => new Button(new ValueText(speed + "×"))
                            .Class("player__option").Attribute("type", "button")
                            .Attribute("data-speed", speed).Attribute("aria-pressed", speed == "1" ? "true" : "false"))])
                            .Class("player__speed-presets"),
                        new Div(
                            new P(new Text("player.fit")),
                            new Div(
                                Fit("contain", "player.fit.inside", true),
                                Fit("cover", "player.fit.fill"),
                                Fit("scale-down", "player.fit.original")
                            ).Class("player__fit-options")
                        ).Class("player__settings-group")
                    ], true),
                    Pane("audio", "player.audio", [.. (source.AudioTracks ?? [])
                        .Select(track => Track("audio", track.Key, new ValueText(track.Label), track.IsDefault, track.IsSupported, track.Language))]),
                    Pane("subtitles", "player.subtitles", [
                        Track("subtitle", "", new Text("player.subtitles.off"), true, true, null),
                        .. (source.Subtitles ?? []).Select(track => Track("subtitle", track.Key, new ValueText(track.Label), false, track.IsSupported, track.Language)),
                        new Div(
                            new P(new Text("player.subtitle.delay")),
                            new Div(
                                new Input().Class("player__speed-range").Attribute("type", "range")
                                    .Attribute("min", "-10").Attribute("max", "10").Attribute("step", "0.05")
                                    .Attribute("value", "0").Attribute("data-role", "subtitle-delay")
                                    .Attribute("aria-label", texts["player.subtitle.delay"]).Attribute("disabled", "disabled"),
                                new Span(new ValueText("0 s")).Class("player__speed-current").Attribute("data-role", "subtitle-delay-label"),
                                new Button(new Text("player.subtitle.reset")).Class("player__option").Attribute("type", "button")
                                    .Attribute("data-action", "subtitle-delay-reset").Attribute("disabled", "disabled")
                            ).Class("player__speed-adjust")
                        ).Class("player__settings-group")
                    ]),
                    Pane("info", "player.delivery", [
                        new Div(
                            new P(new Text("player.delivery")),
                            new P().Attribute("data-role", "mode").Attribute("aria-live", "polite")
                        ).Class("player__settings-group")
                    ])
                ).Class("player__settings-body")
            ).Class("player__panel player__panel--settings")
                .Attribute("data-panel", "settings").Attribute("role", "dialog")
                .Attribute("aria-label", texts["player.settings"]).Attribute("hidden", "hidden")
                .WriteTo(writer, encoder, texts);

            IUiComponent Tab(string key, string labelKey, bool selected = false) =>
                new Button(new Text(labelKey)).Class("player__settings-tab").Attribute("type", "button")
                    .Attribute("data-settings-tab", key).Attribute("aria-pressed", selected ? "true" : "false");

            IUiComponent Fit(string value, string labelKey, bool selected = false) =>
                new Button(new Text(labelKey)).Class("player__option").Attribute("type", "button")
                    .Attribute("data-fit", value).Attribute("aria-pressed", selected ? "true" : "false");

            IUiComponent Pane(string key, string labelKey, IUiComponent[] content, bool selected = false)
            {
                var pane = new Div(content).Class("player__settings-pane").Attribute("data-settings-pane", key)
                    .Attribute("role", "group").Attribute("aria-label", texts[labelKey]);
                if (!selected) pane.Attribute("hidden", "hidden");
                return pane;
            }

            IUiComponent Track(string kind, string key, IUiComponent label, bool selected, bool supported, string? language)
            {
                var button = new Button(label).Class("player__option").Attribute("type", "button")
                    .Attribute("data-" + kind, key).Attribute("aria-pressed", selected ? "true" : "false");
                if (language is not null) button.Attribute("data-language", language);
                if (!supported) button.Attribute("disabled", "disabled")
                    .Attribute("aria-description", texts["player.track.unsupported"]);
                return button;
            }
        }
    }
}
