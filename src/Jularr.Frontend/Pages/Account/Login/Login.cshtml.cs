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
            page.Asset("Framework/Components/PasswordReveal.entry.ts");
            var returnUrl = SafeReturnUrl(Request.Query["ReturnUrl"]);
            var action = Url.Page("/Account/Login/Login", new { returnUrl })!;
            var token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var forgot = new A(new Text("account.login.forgot")).Class("auth-forgot").Attribute("href", "#forgot-hint");

            var card = new List<IUiComponent>
            {
                new Brand(assets, login: true),
                new H1(new Text("account.login.title")).Class("visually-hidden")
            };
            if (TempData["BackupRestored"] is true) card.Add(new P(new Text("admin.backup.restored")).Attribute("role", "status"));
            card.Add(new Form<LoginDataV1>(form =>
            {
                form.Text(x => x.Email, "account.field.userName", "username", new FieldHelp("account.login.userPlaceholder", ""));
                form.Password(x => x.Password, "account.field.password", "current-password", new FieldHelp("", "", forgot, new PasswordReveal(assets)));
                form.Submit(Styled(new Button(new Text("account.login.submit")), "auth-submit"));
            }, action, token, new LoginDataV1(_email, ""), FormError));

            // An administrator resets passwords: Jularr sends no email.
            card.Add(new P(new Text("account.login.forgotHint")).Class("auth-hint").Attribute("id", "forgot-hint"));

            // Advertise configured methods only; none configured, no row.
            if (_authentication?.PlexLoginEnabled == true)
            {
                var providers = new List<IUiComponent>
                {
                    new Form<AuthenticationParametersV1>(form => form.Submit(Styled(new Button(new Icon("plex", assets, 20), new ValueText("Plex")), "auth-provider", "account.plex.start")),
                        Url.Page("/Account/Login/Login", "Plex", new { returnUrl }), token)
                };
                card.Add(new Div(new Text("account.login.providers")).Class("auth-divider"));
                card.Add(new Div([.. providers]).Class("auth-providers"));
            }

            if (_plex is not null)
            {
                card.Add(new A(new Text("account.plex.authorize")).Class("auth-provider").Attribute("href", _plex.AuthorizationUrl).Attribute("target", "_blank").Attribute("rel", "noopener noreferrer"));
            }

            if (_plexChallengeId is { } challengeId)
            {
                card.Add(new P(new Text("account.plex.pending")).Class("auth-hint auth-hint--open"));
                card.Add(new Form<CompletePlexParametersV1>(form =>
                {
                    form.Hidden(x => x.AuthChallengePublicId);
                    form.Submit(Styled(new Button(new Text("account.plex.complete")), "auth-provider"));
                }, Url.Page("/Account/Login/Login", "PlexComplete", new { returnUrl }), token, new CompletePlexParametersV1(challengeId)));
            }

            if (_requiresApproval) card.Add(new P(new Text("account.plex.approval")).Class("auth-hint auth-hint--open").Attribute("role", "status"));
            if (_allowsRegistration)
            {
                card.Add(new P(new Text("account.login.newHere"),
                    new A(new Text("account.login.requestAccess"), new ValueText(" "), new Span(new ValueText("↗")).Attribute("aria-hidden", "true")).Attribute("href", "/Account/Register"))
                    .Class("auth-request"));
            }

            page.Content.Add(new Section([.. card]).Class("login-card"));
            page.Content.Add(new AppearanceDock(assets));
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

        // The sign-in cards' own button looks replace the shared button's.
        private static Button Styled(Button button, string cssClass, string? labelKey = null)
        {
            button.Class(cssClass);
            if (labelKey is not null) button.TextAttribute("aria-label", labelKey);
            return button;
        }

        private string SafeReturnUrl(string? returnUrl) => Url.IsLocalUrl(returnUrl) && !returnUrl!.StartsWith("/Account/Login", StringComparison.OrdinalIgnoreCase) ? returnUrl : "/";
    }
}
