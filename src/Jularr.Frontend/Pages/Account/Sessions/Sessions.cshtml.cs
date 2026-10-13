using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.User.Session.V1;
using Jularr.Backend.Service.User.Session.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Jularr.Backend.Service.Core;

namespace Jularr.Frontend.Pages.Account.Sessions
{
    public sealed class SessionsModel(ReadSessionsV1 readSessions, RevokeSessionV1 revokeSession, IAntiforgery antiforgery) : JularrPage
    {
        private PageResult<SessionDetailsV1>? _sessions;
        private int _pageNumber = 1;

        protected override void Define(Page page)
        {
            page.Title("settings.sessions");
            page.Navigation("nav.settings");
            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Class("field-error").Attribute("role", "alert"));
            if (_sessions is not null)
            {
                foreach (var session in _sessions.Items)
                {
                    page.Content.Add(new UiSection(session.IsCurrent ? "session.current" : "session.other", [
                        new P(new Text("session.created"), new ValueText(session.CreatedAt.ToString("u", CultureInfo.InvariantCulture))),
                        new P(new Text("session.expires"), new ValueText(session.ExpiresAt.ToString("u", CultureInfo.InvariantCulture))),
                        new Form<RevokeSessionParametersV1>(form =>
                        {
                            form.Hidden(x => x.SessionPublicId);
                            form.Submit(session.IsCurrent ? "account.logout.submit" : "session.revoke");
                        }, Url.Page("/Account/Sessions/Sessions"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, new RevokeSessionParametersV1(session.Id))
                    ]));
                }

                if (_sessions.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", Url.Page("/Account/Sessions/Sessions", new { pageNumber = _sessions.Page - 1 })!));
                if (_sessions.Items.Count == _sessions.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", Url.Page("/Account/Sessions/Sessions", new { pageNumber = _sessions.Page + 1 })!));
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
