using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.User.Session.V1;
using Jularr.Backend.Service.User.Session.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Jularr.Backend.Service.Core;

namespace Jularr.Frontend.Pages.Account.Sessions
{
    public sealed class SessionsModel(ReadSessionsV1 readSessions, RevokeSessionV1 revokeSession, RenameSessionDeviceV1 renameDevice, UpdateSessionPlaybackQualityV1 updatePlaybackQuality,
        IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private PageResult<SessionDetailsV1>? _sessions;
        private int _pageNumber = 1;

        protected override void Define(Page page)
        {
            page.Title("settings.sessions");
            page.Parent("settings.title", "/Settings");
            page.Navigation("nav.settings");
            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Class("field-error").Attribute("role", "alert"));
            if (_sessions is not null)
            {
                foreach (var session in _sessions.Items)
                {
                    page.Content.Add(new UiSection(session.IsCurrent ? "session.current" : "session.other", [
                        .. (session.DevicePlatform is { } platform
                            ? new IUiComponent[]
                            {
                                new P(new ValueText(DeviceNames.Show(session.DeviceName, texts) + " · " + platform)),
                                new Form<RenameSessionDeviceDataV1>(form =>
                                {
                                    form.Text(item => item.DeviceName, "devices.rename");
                                    form.Save();
                                }, Url.Page("/Account/Sessions/Sessions", "Rename", new { sessionPublicId = session.Id }), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                                    new RenameSessionDeviceDataV1(session.DeviceName))
                            }
                            : []),
                        new Form<SessionPlaybackQualityDataV1>(form =>
                        {
                            form.Select(item => item.PlaybackQuality, "playback.quality", Enum.GetValues<PlaybackQuality>()
                                .Select(quality => new SelectOption(((byte)quality).ToString(CultureInfo.InvariantCulture), texts["playback.quality." + quality])));
                            form.Save();
                        }, Url.Page("/Account/Sessions/Sessions", "PlaybackQuality", new { sessionPublicId = session.Id }), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                            new SessionPlaybackQualityDataV1(session.PlaybackQuality)),
                        new P(new Text("session.created"), new LocalTime(session.CreatedAt)),
                        new P(new Text("session.expires"), new LocalTime(session.ExpiresAt)),
                        new Form<RevokeSessionParametersV1>(form =>
                        {
                            form.Hidden(x => x.SessionPublicId);
                            form.Submit(session.IsCurrent ? "account.logout.submit" : "session.revoke");
                        }, Url.Page("/Account/Sessions/Sessions"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, new RevokeSessionParametersV1(session.Id))
                    ]));
                }

                page.Content.Add(new Span().Paged(_sessions.Items.Count, _sessions.PageSize));
            }

            page.Content.Add(new A(new Text("nav.settings")).Attribute("href", "/Settings"));
        }

        public async Task<IActionResult> OnGetAsync(int pageNumber = 1)
        {
            _pageNumber = pageNumber;
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostRenameAsync(Guid sessionPublicId, string? deviceName)
        {
            await renameDevice.ExecuteAsync<UpdateResult>(new SessionDeviceParametersV1(sessionPublicId), new RenameSessionDeviceDataV1(deviceName), HttpContext.RequestAborted);
            return RedirectToPage("/Account/Sessions/Sessions");
        }

        public async Task<IActionResult> OnPostPlaybackQualityAsync(Guid sessionPublicId, PlaybackQuality playbackQuality)
        {
            await updatePlaybackQuality.ExecuteAsync<UpdateResult>(new SessionDeviceParametersV1(sessionPublicId), new SessionPlaybackQualityDataV1(playbackQuality), HttpContext.RequestAborted);
            return RedirectToPage("/Account/Sessions/Sessions");
        }

        public async Task<IActionResult> OnPostAsync(Guid sessionPublicId)
        {
            // Revoke, then update authentication.
            var result = await revokeSession.ExecuteAsync<RevokeSessionResultV1>(new RevokeSessionParametersV1(sessionPublicId), default, HttpContext.RequestAborted);
            await ServiceApi.ApplyAuthenticationAsync(HttpContext, result, antiforgery);
            return RedirectToPage("/Account/Sessions/Sessions");
        }

        protected override async Task LoadDataAsync()
        {
            _sessions = await readSessions.ExecuteAsync<PageResult<SessionDetailsV1>>(new ReadSessionsParametersV1(_pageNumber), HttpContext.RequestAborted);
        }
    }
}
