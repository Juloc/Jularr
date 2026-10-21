using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.User.Playback.V1;
using Jularr.Backend.Service.User.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Player
{
    public sealed class PlayerModel(ReadPlaybackItemV1 read, ReadPlaybackTracksV1 readTracks, ReadPlaybackPreferencesV1 readPreferences, ReadPlaybackMarkersV1 readMarkers, ReadPlaybackChaptersV1 readChapters,
        ReadWorkV1 readWork, ReadWorkEpisodesV1 readEpisodes, IAntiforgery antiforgery, FrontendAssets assets, UiText texts) : JularrPage
    {
        // The next episode starts after this many seconds unless the viewer stops it; an album's next track follows at once.
        private const int EpisodeCountdownSeconds = 10;

        private PlaybackPreferencesV1? _preferences;
        private PlaybackItemV1? _item;
        private IReadOnlyList<PlaybackTrackV1> _tracks = [];
        private IReadOnlyList<PlaybackMarkerV1> _markers = [];
        private IReadOnlyList<PlaybackChapterV1> _chapters = [];
        private WorkDetailsV1? _work;
        private IReadOnlyList<WorkEpisodeV1> _episodes = [];

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        protected override void Define(Page page)
        {
            page.Title("player.title");
            page.Asset("Modules/Player/player.entry.ts");
            page.Shell = false;
            page.IsPlayer = true;

            // The plan is requested with the browser's capabilities.
            if (_item is { } item)
            {
                var asset = item.MediaAssetId.ToString(CultureInfo.InvariantCulture);
                var context = MediaUnitLabel.Format(texts, item.WorkUnitTypeId, item.UnitNumber, item.ParentUnitNumber, item.UnitTitle);
                var source = new PlayerSource(item.CanonicalTitle, context, "/Details/" + item.WorkId.ToString(CultureInfo.InvariantCulture), "/api/v1/playback/assets/" + asset + "/sessions",
                    "/api/v1/playback/sessions/", antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "", null,
                    [.. _tracks.Where(track => track.MediaTrackTypeId == MediaTrackType.Audio).Select(Track)],
                    [.. _tracks.Where(track => track.MediaTrackTypeId == MediaTrackType.Subtitle).Select(Track)],
                    "/api/v1/playback/assets/" + asset + "/subtitles/",
                    item.PreviousMediaAssetId is { } previous ? "/Player/" + previous.ToString(CultureInfo.InvariantCulture) : null,
                    item.NextMediaAssetId is { } next ? "/Player/" + next.ToString(CultureInfo.InvariantCulture) : null,
                    texts.CanPreviewSeeking() ? "/api/v1/playback/assets/" + asset + "/trickplay" : null,
                    _preferences is { } preferences ? new PlayerPreferences(preferences.Speed, preferences.AutoplayNext,
                        item.WorkUnitTypeId == WorkUnitType.Episode ? EpisodeCountdownSeconds : 0, preferences.DeviceQuality.ToString(), "/api/v1/playback/preferences/speed",
                        preferences.IntroSkipModeTypeId.ToString(), preferences.OutroSkipModeTypeId.ToString()) : null,
                    [.. _markers.Select(marker => new PlayerMarker(marker.MediaMarkerKindTypeId.ToString(), marker.StartMs / 1000d, marker.EndMs / 1000d))],
                    [.. _chapters.Select(chapter => new PlayerChapter(chapter.Title, chapter.StartMs / 1000d))]);
                var player = new UiPlayer(source, assets);
                if (_work is { } work)
                {
                    var panes = new List<IUiComponent>();
                    var tabs = new List<NavigationTab>();
                    if (_episodes.Count > 0)
                    {
                        tabs.Add(new("details.episodes", "#player-episodes", true));
                        panes.Add(new Div(_episodes.Select(episode => (IUiComponent)new EpisodeCard(assets,
                            MediaUnitLabel.Format(texts, WorkUnitType.Episode, episode.Number, episode.SeasonNumber, null) ?? "",
                            episode.Title, null, episode.ImageUrl, episode.RuntimeMinutes, "media.state." + episode.MediaStateTypeId,
                            episode.MediaAssetId is { } episodeAsset ? "/Player/" + episodeAsset.ToString(CultureInfo.InvariantCulture) : null)
                            .Progress(episode.IsCompleted ? episode.DurationMs : episode.ResumePositionMs, episode.DurationMs)
                            .Compact(episode.MediaAssetId == item.MediaAssetId)).ToArray()).Class("player__episodes").Attribute("id", "player-episodes"));
                    }
                    tabs.Add(new("media.details", "#player-details", _episodes.Count == 0));
                    var details = new Div(new H2(new ValueText(work.CanonicalTitle)), new P(new ValueText(work.Overview ?? "")),
                        new A(new Text("media.details")).Class("button").Attribute("href", source.BackHref).Attribute("data-action", "close")).Attribute("id", "player-details");
                    if (_episodes.Count > 0) details.Attribute("hidden", "hidden");
                    panes.Add(details);
                    player.Sidebar(new Div(new Div(new NavigationTabs(tabs, "nav.tabs")).Attribute("data-player-tabs", ""), new Div([.. panes])));
                }
                page.Content.Add(player);
            }
            else if (FormError is not null)
            {
                page.Content.Add(new P(new Text(FormError.ResourceKey)).Class("player__status").Attribute("role", "alert"));
            }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _item = await read.ExecuteAsync<PlaybackItemV1>(new MediaAssetParametersV1(Id), HttpContext.RequestAborted);
            _tracks = await readTracks.ExecuteAsync<BaseDataList<PlaybackTrackV1>>(new MediaAssetParametersV1(Id), HttpContext.RequestAborted);
            _preferences = await readPreferences.ExecuteAsync<PlaybackPreferencesV1>(new PlaybackPreferencesParametersV1(), HttpContext.RequestAborted);
            _markers = await readMarkers.ExecuteAsync<BaseDataList<PlaybackMarkerV1>>(new MediaAssetParametersV1(Id), HttpContext.RequestAborted);
            _chapters = await readChapters.ExecuteAsync<BaseDataList<PlaybackChapterV1>>(new MediaAssetParametersV1(Id), HttpContext.RequestAborted);
            if (_item is { } item && texts.Can("profile.media.read", "Library"))
            {
                _work = await readWork.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(item.WorkId), HttpContext.RequestAborted);
                if (item.ParentUnitNumber is { } season && item.WorkUnitTypeId == WorkUnitType.Episode)
                {
                    _episodes = await readEpisodes.ExecuteAsync<BaseDataList<WorkEpisodeV1>>(new ReadWorkEpisodesParametersV1(item.WorkId, (int)season), HttpContext.RequestAborted);
                }
            }
        }

        // Language, then the track's own title and channel layout.
        private PlayerTrack Track(PlaybackTrackV1 track, int index)
        {
            var parts = new List<string> { LanguageLabel.Name(track.Language) ?? texts["player.track.unknown"] + " " + (index + 1).ToString(CultureInfo.InvariantCulture) };
            if (!string.IsNullOrWhiteSpace(track.Title)) parts.Add(track.Title);
            if (track.Channels is { } channels)
            {
                parts.Add(channels switch
                {
                    1 => texts["player.channels.mono"], 2 => texts["player.channels.stereo"], 6 => "5.1", 8 => "7.1",
                    _ => string.Format(CultureInfo.InvariantCulture, texts["player.channels.count"], channels)
                });
            }
            if (track.IsForced) parts.Add(texts["player.subtitles.forced"]);
            var key = track.MediaTrackTypeId == MediaTrackType.Audio ? track.Key[1..] : track.Key;
            return new PlayerTrack(key, string.Join(" · ", parts.Distinct(StringComparer.OrdinalIgnoreCase)), track.Language, track.IsSelected, track.IsSupported);
        }
    }
}
