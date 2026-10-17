using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Server-rendered player chrome; Modules/Player owns the client behavior.
    public sealed record PlayerSource(string Title, string? Context, string BackHref, string StartUrl, string SessionUrl, string AntiforgeryToken, string? PosterUrl = null,
        IReadOnlyList<PlayerTrack>? AudioTracks = null, IReadOnlyList<PlayerTrack>? Subtitles = null, string? SubtitleUrl = null, string? PreviousHref = null, string? NextHref = null,
        string? TrickplayUrl = null);

    // One audio or subtitle choice; unsupported ones stay visible but disabled.
    public sealed record PlayerTrack(string Key, string Label, string? Language, bool IsDefault, bool IsSupported);

    public sealed class UiPlayer(PlayerSource source, FrontendAssets assets) : IUiComponent
    {
        private static readonly string[] Statuses = ["storage_waking", "storage_unavailable", "playback_conversion_unavailable", "not_found", "failed"];
        private static readonly string[] Speeds = ["0.25", "0.5", "0.75", "1", "1.25", "1.5", "2", "3", "4"];

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var title = new List<IUiComponent> { new H1(new ValueText(source.Title)).Class("player__title") };
            if (source.Context is { } context) title.Add(new P(new ValueText(context)).Class("player__context"));

            // Preserve one video element.
            var video = new Video().Class("player__video").Attribute("playsinline", "playsinline").Attribute("preload", "none").Attribute("aria-label", source.Title);
            if (source.PosterUrl is { } poster) video.Attribute("poster", poster);

            new Div(
                video,
                new Div(
                    new Div(
                        new A(Icon("close")).Class("player__button").Attribute("href", source.BackHref).Attribute("aria-label", texts["common.back"]).Attribute("data-action", "close"),
                        new Button(new Icon("chevron-down", assets)).Class("player__button").Attribute("data-action", "minimize").Attribute("aria-label", texts["player.minimize"])
                            .Attribute("hidden", "hidden"),
                        new Div([.. title]).Class("player__heading"),
                        new Button(new Icon("chevron-up", assets)).Class("player__button player__mini-only").Attribute("data-action", "expand")
                            .Attribute("aria-label", texts["player.expand"]),
                        new Button(Icon("close")).Class("player__button player__mini-only").Attribute("data-action", "stop").Attribute("aria-label", texts["player.stop"])
                    ).Class("player__top"),
                    new Div(
                        new Div(
                            new Span(new ValueText("0:00")).Attribute("data-role", "time"),
                            new Input().Class("player__timeline").Attribute("type", "range").Attribute("min", "0").Attribute("max", "0").Attribute("step", "1")
                                .Attribute("value", "0").Attribute("data-role", "timeline").Attribute("aria-label", texts["player.timeline"]),
                            new Div().Class("player__preview").Attribute("data-role", "preview").Attribute("aria-hidden", "true").Attribute("hidden", "hidden"),
                            new Span(new ValueText("0:00")).Attribute("data-role", "duration")
                        ).Class("player__progress"),
                        new Div(
                            new Div(
                                new Button(Icon("volume"), Icon("muted")).Class("player__button").Attribute("data-action", "mute")
                                    .Attribute("aria-label", texts["player.mute"]).Attribute("aria-pressed", "false"),
                                new Input().Class("player__volume").Attribute("type", "range").Attribute("min", "0").Attribute("max", "1").Attribute("step", "0.05")
                                    .Attribute("value", "1").Attribute("data-role", "volume").Attribute("aria-label", texts["player.volume"])
                            ).Class("player__audio"),
                            new Div(
                                Neighbour(Action("previous", "player.previous", "previous"), source.PreviousHref),
                                Action("back", "player.back10", "back-10"),
                                new Button(Icon("play"), Icon("pause")).Class("player__button player__button--primary")
                                    .Attribute("data-action", "toggle").Attribute("aria-label", texts["player.play"])
                                    .Attribute("data-label-play", texts["player.play"]).Attribute("data-label-pause", texts["player.pause"]),
                                Action("forward", "player.forward30", "forward-30"),
                                Neighbour(Action("next", "player.next", "next"), source.NextHref)
                            ).Class("player__transport"),
                            new Div([
                                .. Choice("subtitles", "player.subtitles", new Icon("subtitles", assets), Subtitles.Count > 0),
                                .. Choice("audio", "player.audio", new Icon("headphones", assets), Audio.Count > 1),
                                new Button(new Span(new ValueText("1×")).Attribute("data-role", "speed-label")).Class("player__button")
                                    .Attribute("data-action", "speed").Attribute("aria-label", texts["player.speed"]).Attribute("aria-expanded", "false"),
                                Action("pip", "player.pip", "pip").Attribute("hidden", "hidden"),
                                Action("fullscreen", "player.fullscreen", "fullscreen"),
                                Action("more", "player.more", "more").Attribute("aria-expanded", "false")
                            ]).Class("player__options")
                        ).Class("player__actions")
                    ).Class("player__bottom"),
                    new Div(
                        new Div(new Input().Class("player__speed-range").Attribute("type", "range").Attribute("min", "0.25").Attribute("max", "4")
                            .Attribute("step", "0.05").Attribute("value", "1").Attribute("data-role", "speed-control").Attribute("aria-label", texts["player.speed"]),
                            new Span(new ValueText("1×")).Class("player__speed-current").Attribute("data-role", "speed-current")).Class("player__speed-adjust"),
                        new Div([.. Speeds.Select(speed => new Button(new ValueText(speed + "×")).Class("player__option")
                            .Attribute("data-speed", speed).Attribute("aria-pressed", speed == "1" ? "true" : "false"))]).Class("player__speed-presets")
                    ).Class("player__panel player__panel--speed").Attribute("data-panel", "speed").Attribute("role", "group")
                        .Attribute("aria-label", texts["player.speed"]).Attribute("hidden", "hidden"),
                    TrackPanel("audio", "player.audio", Audio.Select(track => Option("audio", track.Key, new ValueText(track.Label), track.IsDefault, track.IsSupported, track.Language))),
                    TrackPanel("subtitles", "player.subtitles", [
                        Option("subtitle", "", new Text("player.subtitles.off"), true, true, null),
                        .. Subtitles.Select(track => Option("subtitle", track.Key, new ValueText(track.Label), false, track.IsSupported, track.Language))
                    ]),
                    new Div(
                        new P(new Text("player.delivery")),
                        new P().Attribute("data-role", "mode")
                    ).Class("player__panel").Attribute("data-panel", "more").Attribute("hidden", "hidden")
                ).Class("player__chrome"),
                new P().Class("player__status").Attribute("role", "status").Attribute("data-role", "status").Attribute("hidden", "hidden"),
                new P(new Text("progress.saveFailed")).Class("player__save-error").Attribute("role", "status").Attribute("data-role", "save-error").Attribute("hidden", "hidden"),
                new Div([
                    .. Statuses.Select(status => new Span(new Text("player.status." + status)).Attribute("data-status", status)),
                    .. new[] { "DirectPlay", "Remux", "Transcode" }.Select(mode => new Span(new Text("player.mode." + mode)).Attribute("data-mode", mode))
                ]).Attribute("hidden", "hidden")
            ).Class("player")
                .Attribute("data-start-url", source.StartUrl)
                .Attribute("data-session-url", source.SessionUrl)
                .Attribute("data-antiforgery", source.AntiforgeryToken)
                .Attribute("data-subtitle-url", source.SubtitleUrl ?? "")
                .Attribute("data-trickplay-url", source.TrickplayUrl ?? "")
                .WriteTo(writer, encoder, texts);

            // Without a neighbour the button stays visible but inactive.
            static HtmlElement Neighbour(HtmlElement button, string? href) => href is null ? button.Attribute("disabled", "disabled") : button.Attribute("data-href", href);

            HtmlElement Action(string action, string labelKey, string icon) => new Button(Icon(icon)).Class("player__button")
                .Attribute("data-action", action).Attribute("aria-label", texts[labelKey]);

            // Direct actions open their own part of the shared settings panels.
            IEnumerable<IUiComponent> Choice(string action, string labelKey, IUiComponent icon, bool visible) => visible
                ? [new Button(icon).Class("player__button").Attribute("data-action", action).Attribute("aria-label", texts[labelKey]).Attribute("aria-expanded", "false")]
                : [];

            HtmlElement TrackPanel(string name, string labelKey, IEnumerable<IUiComponent> options) => new Div([.. options]).Class("player__panel")
                .Attribute("data-panel", name).Attribute("role", "menu").Attribute("aria-label", texts[labelKey]).Attribute("hidden", "hidden");

            HtmlElement Option(string kind, string key, IUiComponent label, bool isChecked, bool isSupported, string? language)
            {
                var option = new Button(label).Class("player__option").Attribute("data-" + kind, key).Attribute("role", "menuitemradio")
                    .Attribute("aria-checked", isChecked ? "true" : "false");
                if (language is not null) option.Attribute("data-language", language);
                if (!isSupported) option.Attribute("disabled", "disabled").Attribute("title", texts["player.track.unsupported"]);
                return option;
            }
        }

        private IReadOnlyList<PlayerTrack> Audio => source.AudioTracks ?? [];

        private IReadOnlyList<PlayerTrack> Subtitles => source.Subtitles ?? [];

        private Icon Icon(string name) => new("player-" + name, assets);
    }
}
