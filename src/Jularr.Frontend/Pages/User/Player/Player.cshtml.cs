using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Service.User.Playback.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Player
{
    public sealed class PlayerModel(ReadPlaybackItemV1 read, ReadPlaybackTracksV1 readTracks, IAntiforgery antiforgery, FrontendAssets assets, UiText texts) : JularrPage
    {
        private PlaybackItemV1? _item;
        private IReadOnlyList<PlaybackTrackV1> _tracks = [];

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
                    item.NextMediaAssetId is { } next ? "/Player/" + next.ToString(CultureInfo.InvariantCulture) : null);
                page.Content.Add(new UiPlayer(source, assets));
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
            return new PlayerTrack(key, string.Join(" · ", parts.Distinct(StringComparer.OrdinalIgnoreCase)), track.Language, track.IsDefault, track.IsSupported);
        }
    }
}
