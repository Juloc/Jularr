using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Enums.Playback;

namespace Jularr.Frontend.Framework
{
    // Server-rendered player chrome; Modules/Player owns the client behavior.
    public sealed record PlayerSource(string Title, string? Context, string BackHref, string StartUrl, string SessionUrl, string AntiforgeryToken, string? PosterUrl = null,
        IReadOnlyList<PlayerTrack>? AudioTracks = null, IReadOnlyList<PlayerTrack>? Subtitles = null, string? SubtitleUrl = null, string? PreviousHref = null, string? NextHref = null,
        string? TrickplayUrl = null, PlayerPreferences? Preferences = null, IReadOnlyList<PlayerMarker>? Markers = null, IReadOnlyList<PlayerChapter>? Chapters = null);

    // What the profile and the device chose before playing: the speed, whether the next episode follows (after a countdown of that many seconds, none for music), and the quality in use.
    public sealed record PlayerPreferences(decimal Speed, bool AutoplayNext, int CountdownSeconds, string QualityKey, string SpeedUrl, string IntroSkip = "Button", string OutroSkip = "Button", string? SecondSubtitleLanguage = null, string? TranslateUrl = null, string? ProfileLanguage = null);

    // A stretch of the video the viewer may skip (Intro, Credits, Recap or Preview), in seconds.
    public sealed record PlayerMarker(string Kind, double Start, double End);

    // A chapter of the video; without a title it is named by its number.
    public sealed record PlayerChapter(string? Title, double Start);

    // One audio or subtitle choice; unsupported ones stay visible but disabled.
    public sealed record PlayerTrack(string Key, string Label, string? Language, bool IsSelected, bool IsSupported, bool IsStyled = false, bool IsForced = false);

    public sealed class UiPlayer(PlayerSource source, FrontendAssets assets) : IUiComponent
    {
        private static readonly string[] Statuses = ["storage_waking", "storage_unavailable", "playback_conversion_unavailable", "playback_capacity_exhausted", "playback_evicted", "playback_reduced", "playback_slow", "cast_unreachable", "cast_insecure", "not_found", "failed"];
        private static readonly string[] Kinds = ["Intro", "Credits", "Recap", "Preview"];
        private static readonly string[] Presets = ["0.25", "0.5", "0.75", "1", "1.25", "1.5", "2", "3", "4"];
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
                // Touch gestures name what they do: the tapped half lights up while seeking; volume, brightness and speed show a pill at the top centre.
                new Div(new Span(new Span(), new Span(), new Span()).Class("player__seek-arrows"), new Span().Attribute("data-role", "seek-text")).Class("player__seek")
                    .Attribute("data-side", "left").Attribute("aria-hidden", "true").Attribute("hidden", "hidden"),
                new Div(new Span(new Span(), new Span(), new Span()).Class("player__seek-arrows"), new Span().Attribute("data-role", "seek-text")).Class("player__seek")
                    .Attribute("data-side", "right").Attribute("aria-hidden", "true").Attribute("hidden", "hidden"),
                new Div(new Span().Attribute("data-role", "casting-text").Attribute("data-template", texts["player.casting"])).Class("player__casting")
                    .Attribute("data-role", "casting").Attribute("aria-live", "polite").Attribute("hidden", "hidden"),
                new Div(new Span(Icon("volume")).Attribute("data-gesture-icon", "volume"), new Span(Icon("muted")).Attribute("data-gesture-icon", "muted"),
                    new Span(Icon("brightness")).Attribute("data-gesture-icon", "brightness"), new Div(new Div().Class("player__gesture-fill")).Class("player__gesture-bar"),
                    new Span().Attribute("data-role", "gesture-text")).Class("player__gesture").Attribute("data-role", "gesture").Attribute("data-auto", texts["player.brightness.auto"])
                    .Attribute("aria-hidden", "true").Attribute("hidden", "hidden"),
                new Div(
                    new Div(
                        new A(Icon("close")).Class("button button--icon player__button").Attribute("href", source.BackHref).Attribute("aria-label", texts["common.back"]).Attribute("data-action", "close"),
                        new Button(new Icon("chevron-down", assets)).Class("button button--icon player__button").Attribute("data-action", "minimize").Attribute("aria-label", texts["player.minimize"])
                            .Attribute("hidden", "hidden"),
                        new Div([.. title]).Class("player__heading"),
                        new Button(new Icon("chevron-up", assets)).Class("button button--icon player__button player__mini-only").Attribute("data-action", "expand")
                            .Attribute("aria-label", texts["player.expand"]),
                        Action("airplay", "player.airplay", "airplay").Attribute("hidden", "hidden"),
                        Action("cast", "player.cast", "cast").Attribute("hidden", "hidden"),
                        new Button(Icon("close")).Class("button button--icon player__button player__mini-only").Attribute("data-action", "stop").Attribute("aria-label", texts["player.stop"])
                    ).Class("player__top"),
                    new Div(
                        new Div(
                            new Span(new ValueText("0:00")).Attribute("data-role", "time"),
                            new Div(
                                new Input().Class("player__timeline").Attribute("type", "range").Attribute("min", "0").Attribute("max", "0").Attribute("step", "1")
                                    .Attribute("value", "0").Attribute("data-role", "timeline").Attribute("aria-label", texts["player.timeline"]),
                                new Div([.. Chapters.Select((chapter, index) => chapter.Start > 0 ? (IUiComponent)new PlainButton().Class("player__mark").Attribute("data-chapter", Seconds(chapter.Start))
                                    .Attribute("data-title", ChapterName(chapter, index)).Attribute("aria-label", ChapterName(chapter, index) + " " + Clock(chapter.Start)) : new Span())]).Class("player__marks")
                            ).Class("player__track"),
                            new Div().Class("player__preview").Attribute("data-role", "preview").Attribute("aria-hidden", "true").Attribute("hidden", "hidden"),
                            new Span(new ValueText("0:00")).Attribute("data-role", "duration")
                        ).Class("player__progress"),
                        Chapters.Count == 0 ? new Span() : new Span().Class("player__chapter-now").Attribute("data-role", "chapter-now").Attribute("hidden", "hidden"),
                        new Div(
                            new Div(
                                new Button(new Span(Icon("volume")).Attribute("data-volume-icon", "3"), new Span(Icon("volume-medium")).Attribute("data-volume-icon", "2"),
                                    new Span(Icon("volume-low")).Attribute("data-volume-icon", "1"), new Span(Icon("muted")).Attribute("data-volume-icon", "0"))
                                    .Class("button button--icon player__button").Attribute("data-action", "mute")
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
                                // Episodes come first and show where the list is not beside the picture.
                                new Button(Icon("list")).IconOnly(texts["details.episodes"]).Class("button button--icon player__button")
                                    .Attribute("data-action", "episodes").Attribute("popovertarget", "player-sidebar").Attribute("hidden", "hidden"),
                                .. ChapterButton(),
                                SubtitleToggle(),
                                new Button(new Icon("settings", assets)).IconOnly(texts["player.settings"]).Class("button button--icon player__button")
                                    .Attribute("data-action", "settings").Attribute("popovertarget", "player-settings"),
                                Action("theater", "player.theater", "theater").Attribute("aria-pressed", "false"),
                                Action("pip", "player.pip", "pip").Attribute("hidden", "hidden"),
                                Action("fullscreen", "player.fullscreen", "fullscreen")
                            ]).Class("player__options")
                        ).Class("player__actions")
                    ).Class("player__bottom"),
                    Settings(),
                    ChaptersPanel()
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
                .Attribute("data-second-language", source.Preferences?.SecondSubtitleLanguage ?? "")
                .Attribute("data-translate-url", TranslateLanguage is null ? "" : source.Preferences!.TranslateUrl!)
                .Attribute("data-translate-language", TranslateLanguage ?? "")
                .Attribute("data-translating-label", texts["player.subtitles.translating"])
                .Attribute("data-translated-label", texts["player.subtitles.translated"])
                .Attribute("data-translate-failed-label", texts["player.subtitles.translateFailed"])
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

            IEnumerable<IUiComponent> ChapterButton() => Chapters.Count == 0 ? [] : [new Button(Icon("chapters")).IconOnly(texts["player.tab.chapters"]).Class("button button--icon player__button")
                .Attribute("data-action", "chapters").Attribute("popovertarget", "player-chapters")];

            string ChapterName(PlayerChapter chapter, int index) => chapter.Title ?? string.Format(CultureInfo.CurrentCulture, texts["player.chapter"], index + 1);

            // The chapters have their own list beside the controls; many of them scroll there, the settings never do.
            IUiComponent ChaptersPanel() => Chapters.Count == 0 ? new Span() : new Div(
                new Header(new Span(new Text("player.tab.chapters")),
                    new Button(new Icon("close", assets)).IconOnly(texts["common.close"]).Attribute("popovertarget", "player-chapters").Attribute("popovertargetaction", "hide")),
                new Div([.. Chapters.Select((chapter, index) => new Button(new Span(new ValueText(ChapterName(chapter, index))), new Span(new ValueText(Clock(chapter.Start))).Class("player__chapter-time"))
                    .Class("button player__option player__chapter").Attribute("data-chapter", Seconds(chapter.Start)).Attribute("data-title", ChapterName(chapter, index)).Attribute("aria-pressed", "false"))]).Class("player__chapter-list")
            ).Class("player__chapters").Attribute("id", "player-chapters").Attribute("popover", "auto").Attribute("role", "dialog").Attribute("aria-label", texts["player.tab.chapters"]);

            // Compact fields from the shared form surface: the label sits in the outline.
            IUiComponent Surface(string id, string labelKey, IUiComponent control) => new Div(new Fieldset(
                new Legend(new LabelElement(new Text(labelKey)).Class("ui-field__label").Attribute("for", id)), control).Class("ui-field__control")).Class("ui-field");

            IUiComponent ChoiceField(string name, string pane, string labelKey, bool disabled, params IUiComponent[] options)
            {
                var id = "player-" + name + "-" + pane;
                var select = new SelectElement(options).Attribute("id", id).Attribute("data-setting", name);
                if (disabled) select.Attribute("disabled", "disabled");
                return Surface(id, labelKey, select);
            }

            IUiComponent AudioField(string pane) => Audio.Count == 0 ? ChoiceField("audio", pane, "player.audio", true, Choice("", new Text("player.track.unknown"), true, false))
                : ChoiceField("audio", pane, "player.audio", false, [.. Audio.Select(track => Choice(track.Key, new ValueText(track.Label), track.IsSelected, !track.IsSupported, track.Language))]);

            IUiComponent SubtitleField(string pane) => ChoiceField("subtitle", pane, "player.subtitles", !Subtitles.Any(track => track.IsSupported),
                [Choice("", new Text("player.subtitles.off"), !Subtitles.Any(track => track.IsSelected), false), .. Subtitles.Select(track => Choice(track.Key, new ValueText(track.Label), track.IsSelected, !track.IsSupported, track.Language, track.IsStyled, track.IsForced))]);

            // A second language above the first; the player starts it from the profile's choice and a track picked here replaces it.
            IUiComponent SecondSubtitleField(string pane) => ChoiceField("subtitle2", pane, "player.subtitles.second", !Subtitles.Any(track => track.IsSupported),
                [Choice("", new Text("player.subtitles.off"), true, false), .. Subtitles.Select(track => Choice(track.Key, new ValueText(track.Label), false, !track.IsSupported, track.Language, false, track.IsForced)),
                    .. TranslateOption()]);

            // A language the profile wants and the file lacks can be made by the local model, where the administrator has switched translation on.
            IEnumerable<HtmlElement> TranslateOption() => TranslateLanguage is { } language
                ? [Choice("translate", new ValueText(string.Format(CultureInfo.CurrentCulture, texts["player.subtitles.translate"], LanguageLabel.Name(language) ?? language)), false, false)] : [];

            // Styled (ASS) subtitles are drawn by libass over the picture where the device wants it; a touch device starts without.
            IUiComponent LibassField()
            {
                if (!Subtitles.Any(track => track.IsStyled)) return new Span();
                var toggle = new Toggle("player.libass", true);
                toggle.Control.Attribute("data-setting", "libass");
                return toggle;
            }

            IUiComponent TranslateStatus() => new P().Class("player__translate-status").Attribute("data-role", "translate-status").Attribute("aria-live", "polite").Attribute("hidden", "hidden");

            IUiComponent QualityField(string pane) => ChoiceField("quality", pane, "playback.quality", false, [.. Qualities.Select(value => Choice(((byte)value).ToString(CultureInfo.InvariantCulture),
                new Text("playback.quality." + value), value.ToString() == (source.Preferences?.QualityKey ?? "Automatic"), false))]);

            // One slider with a number beside it; the dots mark the usual speeds.
            IUiComponent SpeedField(string pane)
            {
                var id = "player-speed-" + pane;
                return Surface(id, "player.speed", new Div(
                    new Input().Class("player__speed-range").Attribute("type", "range").Attribute("id", id).Attribute("min", "0.25").Attribute("max", "4").Attribute("step", "0.05")
                        .Attribute("value", Speed).Attribute("data-setting", "speed-range"),
                    new Input().Class("player__speed-number").Attribute("type", "number").Attribute("min", "0.25").Attribute("max", "4").Attribute("step", "0.05").Attribute("value", Speed)
                        .Attribute("inputmode", "decimal").Attribute("aria-label", texts["player.speedValue"]).Attribute("data-setting", "speed-number"),
                    new Div([.. Presets.Select(value => new PlainButton().Class("player__speed-point").Attribute("data-speed", value).Attribute("aria-label", texts["player.speed"] + " " + value + "×")
                        .Attribute("aria-pressed", value == Speed ? "true" : "false"))]).Class("player__speed-points")).Class("player__speed"));
            }

            // Audio and subtitles move against the picture in milliseconds: later with a plus, earlier with a minus.
            IUiComponent OffsetField(string name, string labelKey, string pane)
            {
                var id = "player-offset-" + name + "-" + pane;
                return Surface(id, labelKey, new Div(
                    new Input().Class("player__offset-input").Attribute("id", id).Attribute("type", "number").Attribute("min", "-10000").Attribute("max", "10000").Attribute("step", "50")
                        .Attribute("value", "0").Attribute("inputmode", "numeric").Attribute("data-setting", name + "-offset"),
                    new Button(new Text("player.offset.reset")).Class("button player__offset-reset").Attribute("data-action", name + "-offset-reset")).Class("player__offset"));
            }

            IUiComponent FitField()
            {
                var segments = new[] { "contain", "cover", "original" }.Select(value =>
                {
                    var radio = new Input().Attribute("type", "radio").Attribute("name", "player-fit").Attribute("value", value).Attribute("data-setting", "fit");
                    if (value == "contain") radio.Attribute("checked", "checked");
                    return (IUiComponent)new LabelElement(radio, new Span(new Text("player.fit." + value))).Class("ui-segment");
                }).ToArray();
                return new Div(new Fieldset(new Legend(new Span(new Text("player.fit")).Class("ui-field__label")), new Div(segments).Class("ui-segments"))
                    .Class("ui-field__control ui-field__control--segmented")).Class("ui-field");
            }

            IEnumerable<IUiComponent> PreferenceFields()
            {
                if (source.Preferences is not { } preferences) return [];
                var fields = new List<IUiComponent>();
                if (preferences.CountdownSeconds > 0)
                {
                    var autoplay = new Toggle("playback.autoplayNext", preferences.AutoplayNext);
                    autoplay.Control.Attribute("data-setting", "autoplay");
                    fields.Add(autoplay);
                }

                IUiComponent[] SkipOptions(string chosen) => [.. Enum.GetNames<SkipMode>().Select(mode => Choice(mode, new Text("playback.skipMode." + mode), mode == chosen, false))];
                fields.Add(new Div(ChoiceField("skip-intro", "playback", "playback.introSkipMode", false, SkipOptions(preferences.IntroSkip)),
                    ChoiceField("skip-outro", "playback", "playback.outroSkipMode", false, SkipOptions(preferences.OutroSkip))).Class("player__pair"));
                return fields;
            }

            // What plays right now; the client fills in the delivery and the picture size.
            // The file as probed and what the server does with it come from the server, the rest is measured while the pane is open.
            IUiComponent Fact(string key, string role, IUiComponent? value = null) => new Div(new Dt(new Text(key)), (value is null ? new Dd() : new Dd(value)).Attribute("data-role", role));
            IUiComponent Facts() => new Div(
                new Dl(
                    Fact("player.delivery", "mode"),
                    Fact("player.picture", "resolution"),
                    Fact("player.info.container", "fact-container"),
                    Fact("player.info.bitrate", "fact-bitrate"),
                    Fact("player.info.video", "fact-video"),
                    Fact("player.info.audiofile", "fact-audiofile"),
                    Fact("player.audio", "fact-audio", new ValueText(Audio.FirstOrDefault(track => track.IsSelected)?.Label ?? Audio.FirstOrDefault()?.Label ?? "")),
                    Fact("playback.quality", "fact-quality", new Text("playback.quality." + (source.Preferences?.QualityKey ?? "Automatic"))),
                    Fact("player.info.videopath", "fact-videopath"),
                    Fact("player.info.audiopath", "fact-audiopath"),
                    Fact("player.info.frames", "fact-frames"),
                    Fact("player.info.buffer", "fact-buffer"),
                    Fact("player.info.codecs", "fact-codecs"),
                    Fact("player.info.network", "fact-network")).Class("player__facts"),
                new Button(new Text("player.info.copy")).Class("button player__copy").Attribute("data-action", "copy-info").Attribute("hidden", "hidden"))
                .Class("player__info").Attribute("data-copied", texts["player.info.copied"]).Attribute("data-converted", texts["player.info.converted"]);

            IUiComponent Settings()
            {
                var names = new[] { "basic", "audio", "subtitles", "playback", "info" };
                return new Div(
                    new Header(new Span(new Text("player.currentPlayback")),
                        new Button(new Icon("close", assets)).IconOnly(texts["common.close"]).Attribute("popovertarget", "player-settings").Attribute("popovertargetaction", "hide")),
                    new Div(
                        Pane("basic", AudioField("basic"), SubtitleField("basic"), SpeedField("basic"), QualityField("basic")),
                        Pane("audio", AudioField("audio"), new Div(OffsetField("audio", "player.offset.audio", "audio"), OffsetField("subtitle", "player.offset.subtitles", "audio")).Class("player__pair"), SpeedField("audio")),
                        Pane("subtitles", new Div(SubtitleField("subtitles"), SecondSubtitleField("subtitles")).Class("player__pair"), TranslateStatus(), LibassField(), new Div(OffsetField("subtitle", "player.offset.subtitles", "subtitles"), OffsetField("audio", "player.offset.audio", "subtitles")).Class("player__pair"), SpeedField("subtitles")),
                        Pane("playback", [QualityField("playback"), SpeedField("playback"), FitField(), .. PreferenceFields()]),
                        Pane("info", Facts())
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
            }

            // The server marks the choice it made; an unsupported track stays visible but cannot be chosen.
            HtmlElement Choice(string key, IUiComponent label, bool isChosen, bool isDisabled, string? language = null, bool isStyled = false, bool isForced = false)
            {
                var option = new OptionElement(label).Attribute("value", key);
                if (isChosen) option.Attribute("selected", "selected").Attribute("data-chosen", "true");
                if (language is not null) option.Attribute("data-language", language);
                if (isStyled) option.Attribute("data-styled", "true");
                if (isForced) option.Attribute("data-forced", "true");
                if (isDisabled) option.Attribute("disabled", "disabled").Attribute("title", texts["player.track.unsupported"]);
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
        // The language the local model can translate a subtitle into: the profile's second language or its own, when the file has no track in it.
        private string? TranslateLanguage => source.Preferences is { TranslateUrl: not null } preferences
            && (string.IsNullOrEmpty(preferences.SecondSubtitleLanguage) ? preferences.ProfileLanguage : preferences.SecondSubtitleLanguage) is { Length: > 0 } language
            && Subtitles.Any(track => track.IsSupported) && !Subtitles.Any(track => track.Language == language) ? language : null;

        private string Speed => (source.Preferences?.Speed ?? 1m).ToString("0.##", CultureInfo.InvariantCulture);

        private IReadOnlyList<PlayerTrack> Audio => source.AudioTracks ?? [];

        private IReadOnlyList<PlayerTrack> Subtitles => source.Subtitles ?? [];

        private IReadOnlyList<PlayerChapter> Chapters => source.Chapters ?? [];

        private Icon Icon(string name) => new("player-" + name, assets);

        private static string Seconds(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

        private sealed class PlainButton : HtmlElement
        {
            public PlainButton() : base("button") => Attribute("type", "button");
        }
        private sealed class SelectElement(params IUiComponent[] children) : HtmlElement("select", children);
        private sealed class OptionElement(params IUiComponent[] children) : HtmlElement("option", children);
        private sealed class LabelElement(params IUiComponent[] children) : HtmlElement("label", children);
        private sealed class Fieldset(params IUiComponent[] children) : HtmlElement("fieldset", children);
        private sealed class Legend(params IUiComponent[] children) : HtmlElement("legend", children);
    }
}
