using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jularr.Backend.Data.Enums.Account;

namespace Jularr.Frontend.Framework
{
    // Server-rendered player chrome; Modules/Player owns the client behavior.
    public sealed record PlayerSource(string Title, string? Context, string BackHref, string StartUrl, string SessionUrl, string AntiforgeryToken, string? PosterUrl = null,
        IReadOnlyList<PlayerTrack>? AudioTracks = null, IReadOnlyList<PlayerTrack>? Subtitles = null, string? SubtitleUrl = null, string? PreviousHref = null, string? NextHref = null,
        string? TrickplayUrl = null, PlayerPreferences? Preferences = null, IReadOnlyList<PlayerMarker>? Markers = null, IReadOnlyList<PlayerChapter>? Chapters = null);

    // What the profile and the device chose before playing: the speed, whether the next episode follows (after a countdown of that many seconds, none for music), and the quality in use.
    public sealed record PlayerPreferences(decimal Speed, bool AutoplayNext, int CountdownSeconds, string QualityKey, string SpeedUrl, string IntroSkip = "Button", string OutroSkip = "Button");

    // A stretch of the video the viewer may skip (Intro, Credits, Recap or Preview), in seconds.
    public sealed record PlayerMarker(string Kind, double Start, double End);

    // A chapter of the video; without a title it is named by its number.
    public sealed record PlayerChapter(string? Title, double Start);

    // One audio or subtitle choice; unsupported ones stay visible but disabled.
    public sealed record PlayerTrack(string Key, string Label, string? Language, bool IsSelected, bool IsSupported);

    public sealed class UiPlayer(PlayerSource source, FrontendAssets assets) : IUiComponent
    {
        private static readonly string[] Statuses = ["storage_waking", "storage_unavailable", "playback_conversion_unavailable", "playback_capacity_exhausted", "playback_evicted", "playback_reduced", "not_found", "failed"];
        private static readonly string[] Kinds = ["Intro", "Credits", "Recap", "Preview"];
        private static readonly string[] Speeds = ["0.5", "0.75", "1", "1.25", "1.5", "1.75", "2"];
        private static readonly PlaybackQuality[] Qualities = Enum.GetValues<PlaybackQuality>();
        private IUiComponent? _sidebar;

        public UiPlayer Sidebar(IUiComponent content)
        {
            _sidebar = content;
            return this;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var title = new List<IUiComponent> { new H1(new ValueText(source.Title)).Class("player__title") };
            if (source.Context is { } context) title.Add(new P(new ValueText(context)).Class("player__context"));

            // Preserve one video element.
            var video = new Video().Class("player__video").Attribute("playsinline", "playsinline").Attribute("preload", "none").Attribute("aria-label", source.Title);
            if (source.PosterUrl is { } poster) video.Attribute("poster", poster);

            var stage = new Div(
                video,
                // Playback feedback stays above the picture, never over the controls.
                new Div(new Span().Class("player__loading-ring")).Class("player__loading")
                    .Attribute("data-role", "loading").Attribute("aria-hidden", "true").Attribute("hidden", "hidden"),
                new Div(Icon("play"), Icon("pause")).Class("player__feedback")
                    .Attribute("data-role", "transport-feedback").Attribute("aria-hidden", "true").Attribute("hidden", "hidden"),
                new Div(
                    new Div(
                        new A(Icon("close")).Class("button button--icon player__button").Attribute("href", source.BackHref).Attribute("aria-label", texts["common.back"]).Attribute("data-action", "close"),
                        new Button(new Icon("chevron-down", assets)).Class("button button--icon player__button").Attribute("data-action", "minimize").Attribute("aria-label", texts["player.minimize"])
                            .Attribute("hidden", "hidden"),
                        new Div([.. title]).Class("player__heading"),
                        new Button(Icon("list")).IconOnly(texts["details.episodes"]).Class("button button--icon player__button")
                            .Attribute("data-action", "episodes").Attribute("popovertarget", "player-sidebar").Attribute("hidden", "hidden"),
                        new Button(new Icon("chevron-up", assets)).Class("button button--icon player__button player__mini-only").Attribute("data-action", "expand")
                            .Attribute("aria-label", texts["player.expand"]),
                        new Button(Icon("close")).Class("button button--icon player__button player__mini-only").Attribute("data-action", "stop").Attribute("aria-label", texts["player.stop"])
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
                                new Button(Icon("volume"), Icon("muted")).Class("button button--icon player__button").Attribute("data-action", "mute")
                                    .Attribute("aria-label", texts["player.mute"]).Attribute("aria-pressed", "false"),
                                new Input().Class("player__volume").Attribute("type", "range").Attribute("min", "0").Attribute("max", "1").Attribute("step", "0.05")
                                    .Attribute("value", "1").Attribute("data-role", "volume").Attribute("aria-label", texts["player.volume"])
                            ).Class("player__audio"),
                            new Div(
                                Neighbour(Action("previous", "player.previous", "previous"), source.PreviousHref),
                                Action("back", "player.back10", "back-10"),
                                new Button(Icon("play"), Icon("pause")).Class("button button--icon player__button player__button--primary")
                                    .Attribute("data-action", "toggle").Attribute("aria-label", texts["player.play"])
                                    .Attribute("data-label-play", texts["player.play"]).Attribute("data-label-pause", texts["player.pause"]),
                                Action("forward", "player.forward30", "forward-30"),
                                Neighbour(Action("next", "player.next", "next"), source.NextHref)
                            ).Class("player__transport"),
                            new Div([
                                SubtitleToggle(),
                                new Button(new Icon("settings", assets)).IconOnly(texts["player.settings"]).Class("button button--icon player__button")
                                    .Attribute("data-action", "settings").Attribute("popovertarget", "player-settings"),
                                Action("theater", "player.theater", "theater").Attribute("aria-pressed", "false"),
                                Action("pip", "player.pip", "pip").Attribute("hidden", "hidden"),
                                Action("fullscreen", "player.fullscreen", "fullscreen")
                            ]).Class("player__options")
                        ).Class("player__actions")
                    ).Class("player__bottom"),
                    Settings()
                ).Class("player__chrome"),
                // Skip offers and the note after an automatic skip stay above the picture.
                new Button(new Span().Attribute("data-role", "skip-text")).Class("button button--primary player__skip").Attribute("data-action", "skip").Attribute("hidden", "hidden"),
                new Div(
                    new Span().Attribute("data-role", "skipped-text"),
                    new Button(new Text("player.skipped.undo")).Class("button").Attribute("data-action", "skip-undo")
                ).Class("player__skipped").Attribute("role", "status").Attribute("data-role", "skipped").Attribute("hidden", "hidden"),
                // The next episode follows after a countdown the viewer can end.
                new Div(
                    new Span().Attribute("data-role", "next-text").Attribute("data-template", texts["player.next.countdown"]),
                    new Button(new Text("player.next.play")).Class("button button--primary").Attribute("data-action", "next-now"),
                    new Button(new Text("player.next.cancel")).Class("button").Attribute("data-action", "next-cancel")
                ).Class("player__next").Attribute("data-role", "next").Attribute("role", "status").Attribute("hidden", "hidden"),
                new P().Class("player__status").Attribute("role", "status").Attribute("data-role", "status").Attribute("hidden", "hidden"),
                new P(new Text("progress.saveFailed")).Class("player__save-error").Attribute("role", "status").Attribute("data-role", "save-error").Attribute("hidden", "hidden"),
                new Div([
                    .. Statuses.Select(status => new Span(new Text("player.status." + status)).Attribute("data-status", status)),
                    .. new[] { "DirectPlay", "Remux", "Transcode" }.Select(mode => new Span(new Text("player.mode." + mode)).Attribute("data-mode", mode)),
                    .. Kinds.Select(kind => new Span(new Text("player.skip." + kind)).Attribute("data-skip-label", kind)),
                    .. Kinds.Select(kind => new Span(new Text("player.skipped." + kind)).Attribute("data-skipped-label", kind)),
                    new Span(new Text("player.skipped.next")).Attribute("data-skipped-label", "Next")
                ]).Attribute("hidden", "hidden")
            ).Class("player__stage");

            var content = new List<IUiComponent> { stage };
            if (_sidebar is not null)
            {
                content.Add(new Div(
                    new Button(new Icon("close", assets)).IconOnly(texts["common.close"]).Class("button button--icon player__sidebar-close")
                        .Attribute("popovertarget", "player-sidebar").Attribute("popovertargetaction", "hide"),
                    _sidebar).Class("player__sidebar").Attribute("id", "player-sidebar").Attribute("aria-label", texts["details.episodes"]));
            }
            new Div([.. content]).Class("player").Attribute("hx-ignore", "")
                .Attribute("data-start-url", source.StartUrl)
                .Attribute("data-session-url", source.SessionUrl)
                .Attribute("data-antiforgery", source.AntiforgeryToken)
                .Attribute("data-subtitle-url", source.SubtitleUrl ?? "")
                .Attribute("data-trickplay-url", source.TrickplayUrl ?? "")
                .Attribute("data-preferred-speed", Speed)
                .Attribute("data-autoplay", source.Preferences is { AutoplayNext: true } ? "true" : "false")
                .Attribute("data-countdown", (source.Preferences?.CountdownSeconds ?? 0).ToString(CultureInfo.InvariantCulture))
                .Attribute("data-speed-url", source.Preferences?.SpeedUrl ?? "")
                .Attribute("data-skip-intro", source.Preferences?.IntroSkip ?? "Button")
                .Attribute("data-skip-outro", source.Preferences?.OutroSkip ?? "Button")
                .Attribute("data-markers", JsonSerializer.Serialize((source.Markers ?? []).Select(marker => new { kind = marker.Kind, start = marker.Start, end = marker.End })))
                .WriteTo(writer, encoder, texts);

            // Without a neighbour the button stays visible but inactive.
            static HtmlElement Neighbour(HtmlElement button, string? href) => href is null ? button.Attribute("disabled", "disabled") : button.Attribute("data-href", href);

            HtmlElement Action(string action, string labelKey, string icon) => new Button(Icon(icon)).IconOnly(texts[labelKey]).Class("button button--icon player__button")
                .Attribute("data-action", action).Attribute("aria-label", texts[labelKey]);

            HtmlElement SubtitleToggle()
            {
                var button = new Button(new Icon("subtitles", assets)).IconOnly(texts["player.subtitles"]).Class("button button--icon player__button")
                    .Attribute("data-action", "subtitle-toggle").Attribute("aria-pressed", "false");
                if (!Subtitles.Any(track => track.IsSupported)) button.Attribute("disabled", "disabled");
                return button;
            }

            IUiComponent Settings()
            {
                var audio = Audio.Count == 0 ? (IUiComponent)new P(new Text("player.track.unknown"))
                    : new Div([.. Audio.Select(track => Option("audio", track.Key, new ValueText(track.Label), track.IsSelected, track.IsSupported, track.Language))]);
                var subtitles = new Div(Option("subtitle", "", new Text("player.subtitles.off"), !Subtitles.Any(track => track.IsSelected), true, null),
                    new Div([.. Subtitles.Select(track => Option("subtitle", track.Key, new ValueText(track.Label), track.IsSelected, track.IsSupported, track.Language))]));
                var speed = new Div([.. Speeds.Select(value => new Button(new ValueText(value + "×")).Class("button player__option")
                    .Attribute("data-speed", value).Attribute("aria-pressed", value == Speed ? "true" : "false"))]).Class("player__speeds");
                var quality = new Div([.. Qualities.Select(value => new Button(new Text("playback.quality." + value)).Class("button player__option")
                    .Attribute("data-quality", ((byte)value).ToString(CultureInfo.InvariantCulture)).Attribute("aria-pressed", value.ToString() == source.Preferences?.QualityKey ? "true" : "false"))]).Class("player__qualities");
                var chapters = source.Chapters ?? [];
                var names = chapters.Count > 0 ? new[] { "basic", "audio", "subtitles", "playback", "chapters", "info" } : new[] { "basic", "audio", "subtitles", "playback", "info" };
                return new Div(
                    new Header(new Span(new Text("player.currentPlayback")),
                        new Button(new Icon("close", assets)).IconOnly(texts["common.close"]).Attribute("popovertarget", "player-settings").Attribute("popovertargetaction", "hide")),
                    new Div(
                        Pane("basic", new Div(
                            Jump("audio", "player.audio", Audio.FirstOrDefault(track => track.IsSelected)?.Label ?? Audio.FirstOrDefault()?.Label),
                            Jump("subtitles", "player.subtitles", Subtitles.FirstOrDefault(track => track.IsSelected)?.Label),
                            Jump("playback", "player.speed", Speed + "×", "speed"),
                            Jump("playback", "playback.quality", texts["playback.quality." + (source.Preferences?.QualityKey ?? "Automatic")], "quality"))),
                        Pane("audio", audio), Pane("subtitles", subtitles),
                        Pane("playback", new H2(new Text("player.speed")), speed, new H2(new Text("playback.quality")), quality),
                        Pane("chapters", new Div([.. chapters.Select((chapter, index) => new Button(
                            new Span(new ValueText(chapter.Title ?? string.Format(CultureInfo.CurrentCulture, texts["player.chapter"], index + 1))),
                            new Span(new ValueText(Clock(chapter.Start))).Class("player__chapter-time")).Class("button player__option player__chapter")
                            .Attribute("data-chapter", chapter.Start.ToString("0.###", CultureInfo.InvariantCulture)).Attribute("aria-pressed", "false"))])),
                        Pane("info", new P(new Text("player.delivery")), new P().Attribute("data-role", "mode"),
                            new P().Attribute("data-role", "resolution"))
                    ).Class("player__settings-body"),
                    new Div(new NavigationTabs(names.Select(name => new NavigationTab("player.tab." + name, "#player-" + name, name == "basic")).ToArray(), "player.settings"))
                        .Attribute("data-player-tabs", "")
                ).Class("player__settings").Attribute("id", "player-settings").Attribute("popover", "auto")
                    .Attribute("role", "dialog").Attribute("aria-label", texts["player.settings"]);

                HtmlElement Pane(string name, params IUiComponent[] children)
                {
                    var pane = new Div(children).Class("player__settings-pane").Attribute("id", "player-" + name).Attribute("data-panel", name);
                    if (name != "basic") pane.Attribute("hidden", "hidden");
                    return pane;
                }
                HtmlElement Jump(string pane, string labelKey, string? value, string? current = null) => new Button(new Icon(pane == "audio" ? "headphones" : pane == "subtitles" ? "subtitles" : "settings", assets),
                    new Span(new Text(labelKey), new Span(value is null ? new Text(pane == "audio" ? "player.track.unknown" : "player.subtitles.off") : new ValueText(value)).Attribute("data-current", current ?? pane)),
                    new Icon("chevron-down", assets)).Class("button player__setting-link").Attribute("data-setting-pane", pane);
            }

            HtmlElement Option(string kind, string key, IUiComponent label, bool isChecked, bool isSupported, string? language)
            {
                var option = new Button(label).Class("button player__option").Attribute("data-" + kind, key).Attribute("aria-pressed", isChecked ? "true" : "false");
                if (language is not null) option.Attribute("data-language", language);
                if (!isSupported) option.Attribute("disabled", "disabled").Attribute("title", texts["player.track.unsupported"]);
                return option;
            }
        }

        // A position as the timeline shows it: m:ss, or h:mm:ss from an hour on.
        private static string Clock(double seconds)
        {
            var total = (long)Math.Max(0, Math.Floor(seconds));
            return total >= 3600 ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", total / 3600, total / 60 % 60, total % 60)
                : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", total / 60, total % 60);
        }

        // The speed as the player writes it: 1, 1.25 and so on.
        private string Speed => (source.Preferences?.Speed ?? 1m).ToString("0.##", CultureInfo.InvariantCulture);

        private IReadOnlyList<PlayerTrack> Audio => source.AudioTracks ?? [];

        private IReadOnlyList<PlayerTrack> Subtitles => source.Subtitles ?? [];

        private Icon Icon(string name) => new("player-" + name, assets);
    }
}
