using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.Public.Instance.V1;
using Jularr.Backend.Data.Public.Setup.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Backend.Service.Public.Instance.V1;
using Jularr.Backend.Service.Public.Setup.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Modules.Appearance;
using Jularr.Frontend.Pages.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jularr.Frontend.Pages.Account.Login
{
    [AllowAnonymous]
    [EnableRateLimiting("public")]
    public sealed class LoginModel(LoginV1 login, ReadAuthenticationV1 authentication, StartPlexV1 startPlex, CompletePlexV1 completePlex,
        ReadSetupV1 setup, ReadInstancePresentationV1 presentation, IAntiforgery antiforgery, FrontendAssets assets) : JularrPage
    {
        private string _email = "";
        private AuthenticationResultV1? _authentication;
        private StartPlexResultV1? _plex;
        private Guid? _plexChallengeId;
        private bool _requiresApproval;
        private bool _allowsRegistration;

        protected override void Define(Page page)
        {
            page.Shell = false;
            page.Title("account.login.title");
            page.Asset("Pages/Account/Login/Login.entry.scss");
            page.Asset("Modules/Appearance/appearance.entry.scss");
            var action = Url.Page("/Account/Login/Login", new { returnUrl = SafeReturnUrl(Request.Query["ReturnUrl"]) })!;
            var token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            page.Content.Add(new Section([
                new Brand(assets, login: true),
                new H1(new Text("account.login.title")).Class("visually-hidden"),
                .. TempData["BackupRestored"] is true ? new IUiComponent[] { new P(new Text("admin.backup.restored")).Attribute("role", "status") } : [],
                new Form<LoginDataV1>(form =>
                {
                    form.Text(x => x.Email, "account.field.email", "username");
                    form.Password(x => x.Password, "account.field.password", "current-password");
                    form.Submit("account.login.submit");
                }, action, token, new LoginDataV1(_email, ""), FormError),
                .. _allowsRegistration ? new IUiComponent[] { new A(new Text("account.register.title")).Attribute("href", "/Account/Register") } : []
            ]).Class("auth-card"));
            page.Content.Add(new Appearance(collapsible: true));

            // Advertise configured methods only.
            if (_authentication?.PlexLoginEnabled == true)
            {
                page.Content.Add(new Section(new Form<AuthenticationParametersV1>(form => form.Submit("account.plex.start"),
                    Url.Page("/Account/Login/Login", "Plex", new { returnUrl = SafeReturnUrl(Request.Query["ReturnUrl"]) }), token)).Class("auth-card"));
            }

            if (_plex is not null)
            {
                page.Content.Add(new A(new Text("account.plex.authorize")).Attribute("href", _plex.AuthorizationUrl).Attribute("target", "_blank").Attribute("rel", "noopener noreferrer"));
            }

            if (_plexChallengeId is { } challengeId)
            {
                page.Content.Add(new Section(new P(new Text("account.plex.pending")), new Form<CompletePlexParametersV1>(form =>
                {
                    form.Hidden(x => x.AuthChallengePublicId);
                    form.Submit("account.plex.complete");
                }, Url.Page("/Account/Login/Login", "PlexComplete", new { returnUrl = SafeReturnUrl(Request.Query["ReturnUrl"]) }), token, new CompletePlexParametersV1(challengeId))).Class("auth-card"));
            }

            if (_requiresApproval) page.Content.Add(new P(new Text("account.plex.approval")).Attribute("role", "status"));
        }

        public async Task<IActionResult> OnGetAsync(string? returnUrl)
        {
            // Enter setup on fresh instances.
            if ((await setup.ExecuteAsync<ReadSetupResultV1>(cancellationToken: HttpContext.RequestAborted)).RequiresOwner)
            {
                return RedirectToPage("/Setup/Start/Start");
            }

            if (User.Identity?.IsAuthenticated == true)
            {
                return LocalRedirect(SafeReturnUrl(returnUrl));
            }

            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _authentication = await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: HttpContext.RequestAborted);
            _allowsRegistration = (await presentation.ExecuteAsync<InstancePresentationDetailsV1>(new ReadInstancePresentationParametersV1(), HttpContext.RequestAborted)).AllowsRegistration;
        }

        public async Task<IActionResult> OnPostAsync(string? email, string? password, string? returnUrl)
        {
            _email = email ?? "";
            await LoadDataAsync();

            // Invoke the canonical service.
            var result = await login.ExecuteAsync<LoginResultV1>(new LoginDataV1(_email, password ?? ""), HttpContext.RequestAborted);
            await ServiceApi.ApplyAuthenticationAsync(HttpContext, result, antiforgery);

            // Preserve only internal destinations.
            return result.ProfileId is null ? RedirectToPage("/Account/Profiles/Profiles", new { returnUrl = SafeReturnUrl(returnUrl) }) : LocalRedirect(SafeReturnUrl(returnUrl));
        }

        public async Task<IActionResult> OnPostPlexAsync()
        {
            await LoadDataAsync();
            _plex = await startPlex.ExecuteAsync<StartPlexResultV1>(cancellationToken: HttpContext.RequestAborted);
            _plexChallengeId = _plex.AuthChallengePublicId;
            await ServiceApi.ApplyAuthenticationAsync(HttpContext, _plex, antiforgery);
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostPlexCompleteAsync(Guid authChallengePublicId, string? returnUrl)
        {
            _plexChallengeId = authChallengePublicId;
            await LoadDataAsync();
            var result = await completePlex.ExecuteAsync<LoginResultV1>(new CompletePlexParametersV1(authChallengePublicId), default, HttpContext.RequestAborted);
            await ServiceApi.ApplyAuthenticationAsync(HttpContext, result, antiforgery);
            if (result.IsAuthenticated)
            {
                return result.ProfileId is null ? RedirectToPage("/Account/Profiles/Profiles", new { returnUrl = SafeReturnUrl(returnUrl) }) : LocalRedirect(SafeReturnUrl(returnUrl));
            }

            _requiresApproval = result.RequiresApproval;
            if (_requiresApproval) _plexChallengeId = null;
            PreparePage();
            return Page();
        }

        private string SafeReturnUrl(string? returnUrl) => Url.IsLocalUrl(returnUrl) && !returnUrl!.StartsWith("/Account/Login", StringComparison.OrdinalIgnoreCase) ? returnUrl : "/";
    }
}
