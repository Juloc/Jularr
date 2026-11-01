using System.Globalization;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Details
{
    // The units of a title: a series' episodes, the volumes and chapters of a book, the tracks of an album.
    public sealed partial class DetailsModel
    {
        private enum ProgressStyle { Compact, Icon, Menu }

        private int? SelectedSeason => SeasonNumber is { } chosen && _seasons.Any(season => season.SeasonNumber == chosen) ? chosen
            : _seasons.FirstOrDefault(season => season.SeasonNumber > 0)?.SeasonNumber ?? _seasons.FirstOrDefault()?.SeasonNumber;

        private string SeasonName(WorkSeasonV1 season) => season.SeasonNumber == 0 ? texts["details.specials"]
            : MediaUnitLabel.Format(texts, WorkUnitType.Season, season.SeasonNumber, null)!;

        private bool ListLayout => Layout != "cards";

        // The shown season's episodes: with the season, the order and the layout above them, or the next five of the Overview.
        private IUiComponent Episodes(WorkDetailsV1 work, bool preview)
        {
            var canPlay = texts.CanPlay();
            var episodes = _episodes.Select((episode, index) => (episode, index)).ToList();
            if (preview)
            {
                // From the first one the profile has not finished.
                var next = Math.Max(0, episodes.FindIndex(item => !item.episode.IsCompleted));
                return new Div(episodes.Skip(next).Take(5).Select(item => EpisodeEntry(item.episode, canPlay, true)).ToArray()).Class("details-card details-card--list");
            }

            if (Sort == "desc") episodes.Reverse();
            else if (Sort == "unwatched") episodes = [.. episodes.OrderBy(item => item.episode.IsCompleted).ThenBy(item => item.index)];

            // Staff-named sections head their episodes.
            var parts = new List<IUiComponent>();
            string? group = null;
            foreach (var (episode, _) in episodes)
            {
                if (episode.GroupName is { } name && name != group) parts.Add(new H3(new ValueText(name)));
                group = episode.GroupName;
                parts.Add(EpisodeEntry(episode, canPlay, ListLayout));
            }

            return new Div(EpisodeToolbar(), _episodes.Count == 0 ? new P(new Text("details.noEpisodes")).Class("details-empty")
                : new Div([.. parts]).Class(ListLayout ? "details-card details-card--list" : "details-card details-card--grid"));
        }

        // The season and the order as menus, the buttons that change the season's state, and the switch between lines and cards.
        private IUiComponent EpisodeToolbar()
        {
            var tools = new List<IUiComponent>();
            if (_seasons.Count > 1)
            {
                tools.Add(new SelectLinkMenu(assets, [.. _seasons.Select(season => new NavigationTab(string.Join(" · ", new[] { SeasonName(season), string.Format(CultureInfo.CurrentCulture, texts["details.episodeCount"], season.EpisodeCount),
                    season.WatchedCount > 0 ? string.Format(CultureInfo.CurrentCulture, texts["details.watchedCount"], season.WatchedCount) : null }.OfType<string>()),
                    Address(query => { query["tab"] = "episodes"; query["season"] = season.SeasonNumber.ToString(CultureInfo.InvariantCulture); }), season.SeasonNumber == SelectedSeason, true))],
                    "details.seasons", null, "compact"));
            }

            tools.Add(new SelectLinkMenu(assets, [.. new[] { ("asc", "details.sort.asc"), ("desc", "details.sort.desc"), ("unwatched", "details.sort.unwatched") }
                .Select(item => new NavigationTab(item.Item2, Address(query => { query["tab"] = "episodes"; query["sort"] = item.Item1 == "asc" ? null : item.Item1; }), (Sort ?? "asc") == item.Item1))],
                "details.sort", null, "compact"));
            tools.Add(new Span().Class("details-toolbar__spacer"));

            // The season's own progress; starting over names where it begins.
            var chosen = _seasons.FirstOrDefault(season => season.SeasonNumber == SelectedSeason);
            if (texts.CanReadMedia() && chosen is not null)
            {
                var started = chosen.WatchedCount > 0 || _episodes.Any(episode => episode.ResumePositionMs > 0);
                var seasonStart = _episodes.Where(episode => texts.CanPlay() && episode.MediaAssetId is not null).Select(episode => "/Player/" + episode.MediaAssetId!.Value.ToString(CultureInfo.InvariantCulture)).FirstOrDefault();
                tools.Add(ProgressForm("progress.season.markWatched", ProgressAction.Watched, chosen.Id, null, "check", ProgressStyle.Compact, soft: true));
                tools.Add(ProgressForm("progress.season.markUnwatched", ProgressAction.Unwatched, chosen.Id, null, "eye", ProgressStyle.Compact));
                if (started && seasonStart is not null) tools.Add(ProgressForm("progress.season.startOver", ProgressAction.Unwatched, chosen.Id, seasonStart, "history", ProgressStyle.Compact));
            }

            tools.Add(new LayoutSwitch(assets, [new LayoutOption("list", "details.layout.list", Address(query => { query["tab"] = "episodes"; query["view"] = null; }), ListLayout),
                new LayoutOption("grid", "details.layout.cards", Address(query => { query["tab"] = "episodes"; query["view"] = "cards"; }), !ListLayout)], "details.layout"));
            return new Div([.. tools]).Class("details-toolbar");
        }

        // One episode: its still, its title with length and day, where the profile is, and the ways to play it or change that.
        private IUiComponent EpisodeEntry(WorkEpisodeV1 episode, bool canPlay, bool row)
        {
            var resume = episode.ResumePositionMs is > 0 and { } position && !episode.IsCompleted ? Clock(position) : null;
            var facts = string.Join(" · ", new[] { episode.RuntimeMinutes is { } minutes ? string.Format(CultureInfo.CurrentCulture, texts["details.minutes"], minutes) : null,
                episode.ReleasedOn?.ToString("d", CultureInfo.CurrentCulture) }.OfType<string>());
            var card = new EpisodeCard(assets, "S" + episode.SeasonNumber.ToString("00", CultureInfo.InvariantCulture) + " E" + episode.Number.ToString("00.###", CultureInfo.InvariantCulture),
                    episode.Title, episode.ImageUrl, episode.RuntimeMinutes, "media.state." + episode.MediaStateTypeId,
                    canPlay && episode.MediaAssetId is { } asset ? "/Player/" + asset.ToString(CultureInfo.InvariantCulture) : null, resume)
                .Progress(episode.IsCompleted ? null : episode.ResumePositionMs, episode.DurationMs)
                .Facts(facts.Length == 0 ? null : facts, EpisodeState(episode))
                .Actions(EpisodeActions(episode));
            return row ? card.Row() : card;
        }

        // Watched, how far, not yet seen, to come, requested or not here: always a word and an icon.
        private IUiComponent EpisodeState(WorkEpisodeV1 episode)
        {
            if (episode.IsCompleted) return Chip(new Text("progress.watched"), UiTone.Success, "check");
            if (episode.MediaStateTypeId == MediaStateType.Available)
            {
                return episode.ResumePositionMs is > 0 && episode.DurationMs is > 0
                    ? Chip(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["details.percent"], Math.Clamp(episode.ResumePositionMs.Value * 100 / episode.DurationMs.Value, 1, 99))), UiTone.Info, "play")
                    : Chip(new Text("details.state.unwatched"), UiTone.Neutral, "clock");
            }

            if (episode.IsUpcoming) return Chip(new Text("details.upcoming"), UiTone.Info, "calendar");
            return episode.MediaStateTypeId == MediaStateType.Requested ? Chip(new Text("media.state.Requested"), UiTone.Accent, "clock") : Chip(new Text("details.state.notLocal"), UiTone.Warning, "cloud");
        }

        private IUiComponent Chip(IUiComponent label, UiTone tone, string icon) => new StateChip(label, tone, new Icon(icon, assets, 14));

        // An episode: watched or not with one icon, and starting over where it was begun or finished.
        private IUiComponent? EpisodeActions(WorkEpisodeV1 episode)
        {
            if (!texts.CanReadMedia()) return null;
            var started = episode.IsCompleted || episode.ResumePositionMs > 0;
            return new Div(
                started && texts.CanPlay() && episode.MediaAssetId is { } asset
                    ? ProgressForm("progress.startOver", ProgressAction.Unwatched, episode.Id, "/Player/" + asset.ToString(CultureInfo.InvariantCulture), "history", ProgressStyle.Icon)
                    : new Span(),
                ProgressForm(episode.IsCompleted ? "progress.markUnwatched" : "progress.markWatched", episode.IsCompleted ? ProgressAction.Unwatched : ProgressAction.Watched, episode.Id, null, "check",
                    ProgressStyle.Icon, pressed: episode.IsCompleted));
        }

        // The volumes of a book or comic with their chapters, from the files that are here; a chapter without a volume has none.
        private sealed record VolumeEntry(decimal? Number, WorkAvailabilityV1? Volume, IReadOnlyList<WorkAvailabilityV1> Chapters);

        private static IReadOnlyList<VolumeEntry> Volumes(IReadOnlyList<WorkAvailabilityV1> items) => [.. items
            .Where(item => item.WorkUnitTypeId is WorkUnitType.Volume or WorkUnitType.Chapter)
            .GroupBy(item => item.WorkUnitTypeId == WorkUnitType.Volume ? item.UnitNumber : item.ParentUnitNumber)
            .OrderBy(group => group.Key is null).ThenBy(group => group.Key)
            .Select(group => new VolumeEntry(group.Key, group.FirstOrDefault(item => item.WorkUnitTypeId == WorkUnitType.Volume), [.. group.Where(item => item.WorkUnitTypeId == WorkUnitType.Chapter).OrderBy(item => item.UnitNumber)]))];

        private static bool Finished(VolumeEntry entry) => entry.Volume is { } volume ? volume.IsCompleted : entry.Chapters.Count > 0 && entry.Chapters.All(chapter => chapter.IsCompleted);

        // Volume covers with their state, and under them the chapters of the chosen one; the Overview shows the next volumes.
        private IUiComponent VolumeList(IReadOnlyList<VolumeEntry> volumes, bool preview)
        {
            var chosen = volumes.FirstOrDefault(entry => VolumeKey == VolumeId(entry)) ?? volumes.FirstOrDefault(entry => !Finished(entry)) ?? volumes[0];
            var shown = volumes;
            if (preview)
            {
                var next = Math.Max(0, volumes.ToList().FindIndex(entry => !Finished(entry)));
                shown = [.. volumes.Skip(next).Take(5)];
            }

            // Each volume has its own way to read it, so no volume needs a visit of its own first.
            var covers = new Div([.. shown.Select(entry => (IUiComponent)new Div(VolumeCover(entry, entry == chosen && !preview), entry.Volume is { } own && CanOpen(own) ? Play(own) : new Span())
                .Class("details-volume-item"))]).Class("details-volumes");
            return preview ? covers : new Div(covers, Chapters(chosen));
        }

        private static string VolumeId(VolumeEntry entry) => entry.Number is { } number ? number.ToString("0.###", CultureInfo.InvariantCulture) : "none";

        private IUiComponent VolumeCover(VolumeEntry entry, bool picked)
        {
            var label = entry.Volume is { } volume ? MediaUnitLabel.Format(texts, WorkUnitType.Volume, volume.UnitNumber, null, volume.UnitTitle)! : entry.Number is { } number
                ? MediaUnitLabel.Format(texts, WorkUnitType.Volume, number, null)! : texts["details.wholeWork"];
            var state = entry.Volume is { } own ? ReadState(own) : ReadState(entry.Chapters);
            var facts = string.Join(" · ", new[] { entry.Chapters.Count > 0 ? string.Format(CultureInfo.CurrentCulture, texts["details.chapterCount"], entry.Chapters.Count) : null }.OfType<string>());
            var cover = new Div(
                entry.Volume?.UnitImageUrl is { } image ? new MediaImage(image, "", 300, 450) : new Span(new ValueText(entry.Number?.ToString("0.###", CultureInfo.CurrentCulture) ?? "")).Class("details-volume__number"),
                new Span(Chip(new Text(entry.Volume?.IsAvailable == false ? "library.missing" : "details.state.local"), entry.Volume?.IsAvailable == false ? UiTone.Warning : UiTone.Success,
                    entry.Volume?.IsAvailable == false ? "cloud" : "check")).Class("details-volume__tag")).Class("details-volume__cover");
            var link = new A(cover, new Strong(new Truncated(label)), new Span(new ValueText(facts.Length > 0 ? facts + " · " : ""), state).Class("details-volume__facts"))
                .Class(picked ? "details-volume details-volume--picked" : "details-volume").Attribute("href", Address(query => { query["tab"] = "volumes"; query["volume"] = VolumeId(entry); }));
            return picked ? link.Attribute("aria-current", "true") : link;
        }

        private IUiComponent ReadState(WorkAvailabilityV1 item) => item.IsCompleted ? new Text("progress.read") : item.HasProgress ? new Text("progress.started") : new Text("details.state.unread");

        private IUiComponent ReadState(IReadOnlyList<WorkAvailabilityV1> chapters) => chapters.Count > 0 && chapters.All(chapter => chapter.IsCompleted) ? new Text("progress.read")
            : chapters.Any(chapter => chapter.HasProgress) ? new Text("progress.started") : new Text("details.state.unread");

        // The chosen volume: its own file and the way to change its state above the chapters that are here.
        private IUiComponent Chapters(VolumeEntry entry)
        {
            var head = new List<IUiComponent>();
            var title = entry.Volume is { } volume ? MediaUnitLabel.Format(texts, WorkUnitType.Volume, volume.UnitNumber, null, volume.UnitTitle)
                : entry.Number is { } number ? MediaUnitLabel.Format(texts, WorkUnitType.Volume, number, null) : texts["details.wholeWork"];
            head.Add(new Span(new Truncated(title ?? "")).Class("details-chapters__title"));
            var actions = new List<IUiComponent>();
            if (entry.Volume is { } own)
            {
                if (CanOpen(own)) actions.Add(Play(own));
                actions.Add(Mark(own));
            }

            head.Add(new Div([.. actions]).Class("details-row__actions"));
            var rows = entry.Chapters.Select(chapter => (IUiComponent)new Div(
                new Span(new ValueText((chapter.UnitNumber ?? 0).ToString("0.###", CultureInfo.CurrentCulture))).Class("details-row__number"),
                new Span(new Truncated(MediaUnitLabel.Format(texts, chapter.WorkUnitTypeId, chapter.UnitNumber, null, chapter.UnitTitle) ?? "")).Class("details-row__title"),
                ChapterState(chapter),
                new Div(Play(chapter), Mark(chapter)).Class("details-row__actions")).Class("details-row details-row--chapter"));
            return new Div(new Div([.. head]).Class("details-chapters__head"), new Div([.. rows]).Class("details-chapters__rows")).Class("details-card details-chapters");
        }

        private IUiComponent ChapterState(WorkAvailabilityV1 chapter) =>
            !chapter.IsAvailable ? Chip(new Text("library.missing"), UiTone.Warning, "cloud")
            : chapter.IsCompleted ? Chip(new Text("progress.read"), UiTone.Success, "check")
            : chapter.HasProgress ? Chip(new Text("progress.started"), UiTone.Info, "play")
            : Chip(new Text("details.state.unread"), UiTone.Neutral, "clock");

        // The tracks of an album by disc, with their length, state and the way to play or mark them; the Overview shows the first five.
        private IUiComponent Tracks(IReadOnlyList<WorkAvailabilityV1> tracks, bool preview)
        {
            var discs = tracks.OrderBy(track => track.UnitNumber).GroupBy(track => track.DiscNumber ?? 1).OrderBy(group => group.Key).ToArray();
            var parts = new List<IUiComponent>();
            var left = preview ? 5 : int.MaxValue;
            foreach (var disc in discs)
            {
                if (left <= 0) break;
                var rows = disc.Take(left).ToArray();
                left -= rows.Length;
                if (discs.Length > 1) parts.Add(new H3(new Icon("disc", assets, 14), new ValueText(" "), new ValueText(string.Format(CultureInfo.CurrentCulture, texts["details.disc"], disc.Key))).Class("details-disc"));
                parts.Add(new Table<WorkAvailabilityV1>(rows).Compact()
                    .Content("details.track.number", track => new ValueText((track.UnitNumber ?? 0).ToString("0.###", CultureInfo.CurrentCulture)))
                    .Content("details.track.title", track => new Truncated(track.UnitTitle ?? MediaUnitLabel.Format(texts, track.WorkUnitTypeId, track.UnitNumber, null) ?? ""))
                    .Content("details.duration", track => new ValueText(track.DurationMs is { } duration ? ShortClock(duration) : ""))
                    .Content("table.status", track => TrackState(track))
                    .Content("common.actions", track => new Div(PlayIcon(track), TrackMark(track)).Class("details-row__actions")));
            }

            return new Div([.. parts]).Class("details-tracks");
        }

        private IUiComponent TrackState(WorkAvailabilityV1 track) =>
            !track.IsAvailable ? Chip(new Text("library.missing"), UiTone.Warning, "cloud")
            : track.IsCompleted ? Chip(new Text("progress.heard"), UiTone.Success, "check")
            : track.HasProgress ? Chip(new Text("progress.started"), UiTone.Info, "play")
            : Chip(new Text("details.state.unheard"), UiTone.Neutral, "clock");

        private IUiComponent TrackMark(WorkAvailabilityV1 track) => !texts.CanReadMedia() || track.WorkUnitId is not { } unit || !track.IsAvailable ? new Span()
            : ProgressForm(track.IsCompleted ? "progress.markUnheard" : "progress.markHeard", track.IsCompleted ? ProgressAction.Unwatched : ProgressAction.Watched, unit, null, "check", ProgressStyle.Icon, pressed: track.IsCompleted);

        // A track's play button: an icon, the name in its label.
        private IUiComponent PlayIcon(WorkAvailabilityV1 item) => CanOpen(item)
            ? new A(new Icon("play", assets, 14)).Class("button button--icon").Attribute("href", PlayHref(item)).Attribute("aria-label", texts[PlayKey(item)]).Attribute("title", texts[PlayKey(item)])
            : new Span();

        // A button posting a change of the profile's progress; with a play address it is "start over" and opens the player or reader afterwards.
        private IUiComponent ProgressForm(string labelKey, ProgressAction action, long? unitId, string? playHref = null, string? icon = null, ProgressStyle style = ProgressStyle.Compact, bool soft = false, bool pressed = false)
        {
            var hidden = new List<(string Name, string Value)> { ("action", ((byte)action).ToString(CultureInfo.InvariantCulture)), ("returnUrl", Request.Path + Request.QueryString) };
            if (unitId is { } unit) hidden.Add(("unitId", unit.ToString(CultureInfo.InvariantCulture)));
            if (playHref is not null) hidden.Add(("playHref", playHref));
            HtmlElement button;
            switch (style)
            {
                case ProgressStyle.Menu:
                    button = new Button(new Icon(icon ?? "check", assets, 16), new Span(new Text(labelKey))).Submit().Class("action-menu__item");
                    break;
                case ProgressStyle.Icon:
                    button = new Button(new Icon(icon ?? "check", assets)).IconOnly(texts[labelKey]).Submit().Attribute("title", texts[labelKey]);
                    if (pressed) button.Attribute("aria-pressed", "true");
                    break;
                default:
                    var compact = new Button(icon is null ? new Span(new Text(labelKey)) : new Span(new Icon(icon, assets, 14), new Text(labelKey)).Class("details-button__label"));
                    button = (soft ? compact.Soft() : compact).Compact().Submit();
                    break;
            }

            return new PostForm("/Details/" + Id.ToString(CultureInfo.InvariantCulture) + "?handler=Progress", _csrf, hidden, button);
        }

        private static string Clock(long milliseconds) => TimeSpan.FromMilliseconds(milliseconds).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);

        // A track's length: minutes and seconds, with hours only when it has them.
        private static string ShortClock(long milliseconds) => TimeSpan.FromMilliseconds(milliseconds).ToString(milliseconds >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    }
}
