using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

// Server-rendered player chrome; Modules/Player owns the client behavior.
public sealed record PlayerSource(string Title, string? Context, string BackHref, string StartUrl, string SessionUrl, string AntiforgeryToken);

public sealed class UiPlayer(PlayerSource source, FrontendAssets assets) : IUiComponent
{
    private static readonly string[] Statuses = ["storage_waking", "storage_unavailable", "playback_conversion_unavailable", "not_found", "failed"];
    private static readonly string[] Speeds = ["0.75", "1", "1.25", "1.5", "2"];

    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        var title = new List<IUiComponent> { new H1(new ValueText(source.Title)).Class("player__title") };
        if (source.Context is { } context) title.Add(new P(new ValueText(context)).Class("player__context"));

        new Div(
            new Video().Class("player__video").Attribute("playsinline", "playsinline").Attribute("preload", "none").Attribute("aria-label", source.Title),
            new Div(
                new Div(
                    new A(Icon("close")).Class("player__button").Attribute("href", source.BackHref).Attribute("aria-label", texts["common.back"]).Attribute("data-action", "close"),
                    new Div([.. title]).Class("player__heading")
                ).Class("player__top"),
                new Div(
                    Action("back", "player.back10", "back-10"),
                    new Button(Icon("play"), Icon("pause")).Class("player__button player__button--primary")
                        .Attribute("data-action", "toggle").Attribute("aria-label", texts["player.play"])
                        .Attribute("data-label-play", texts["player.play"]).Attribute("data-label-pause", texts["player.pause"]),
                    Action("forward", "player.forward30", "forward-30")
                ).Class("player__transport"),
                new Div(
                    new Div(
                        new Span(new ValueText("0:00")).Attribute("data-role", "time"),
                        new Input().Class("player__timeline").Attribute("type", "range").Attribute("min", "0").Attribute("max", "0").Attribute("step", "1")
                            .Attribute("value", "0").Attribute("data-role", "timeline").Attribute("aria-label", texts["player.timeline"]),
                        new Span(new ValueText("0:00")).Attribute("data-role", "duration")
                    ).Class("player__progress"),
                    new Div(
                        new Button(Icon("volume"), Icon("muted")).Class("player__button").Attribute("data-action", "mute")
                            .Attribute("aria-label", texts["player.mute"]).Attribute("aria-pressed", "false"),
                        new Input().Class("player__volume").Attribute("type", "range").Attribute("min", "0").Attribute("max", "1").Attribute("step", "0.05")
                            .Attribute("value", "1").Attribute("data-role", "volume").Attribute("aria-label", texts["player.volume"]),
                        new Span().Class("player__spacer"),
                        new Button(new Span(new ValueText("1×")).Attribute("data-role", "speed-label")).Class("player__button")
                            .Attribute("data-action", "speed").Attribute("aria-label", texts["player.speed"]).Attribute("aria-expanded", "false"),
                        Action("pip", "player.pip", "pip").Attribute("hidden", "hidden"),
                        Action("fullscreen", "player.fullscreen", "fullscreen"),
                        Action("more", "player.more", "more").Attribute("aria-expanded", "false")
                    ).Class("player__actions")
                ).Class("player__bottom"),
                new Div([.. Speeds.Select(speed => new Button(new ValueText(speed + "×")).Class("player__option")
                    .Attribute("data-speed", speed).Attribute("role", "menuitemradio").Attribute("aria-checked", speed == "1" ? "true" : "false"))])
                    .Class("player__panel").Attribute("data-panel", "speed").Attribute("role", "menu").Attribute("aria-label", texts["player.speed"]).Attribute("hidden", "hidden"),
                new Div(
                    new P(new Text("player.delivery")),
                    new P().Attribute("data-role", "mode")
                ).Class("player__panel").Attribute("data-panel", "more").Attribute("hidden", "hidden")
            ).Class("player__chrome"),
            new P().Class("player__status").Attribute("role", "status").Attribute("data-role", "status").Attribute("hidden", "hidden"),
            new Div([
                .. Statuses.Select(status => new Span(new Text("player.status." + status)).Attribute("data-status", status)),
                .. new[] { "DirectPlay", "Remux", "Transcode" }.Select(mode => new Span(new Text("player.mode." + mode)).Attribute("data-mode", mode))
            ]).Attribute("hidden", "hidden")
        ).Class("player")
            .Attribute("data-start-url", source.StartUrl)
            .Attribute("data-session-url", source.SessionUrl)
            .Attribute("data-antiforgery", source.AntiforgeryToken)
            .WriteTo(writer, encoder, texts);

        HtmlElement Action(string action, string labelKey, string icon) => new Button(Icon(icon)).Class("player__button")
            .Attribute("data-action", action).Attribute("aria-label", texts[labelKey]);
    }

    private Icon Icon(string name) => new("player-" + name, assets);
}
