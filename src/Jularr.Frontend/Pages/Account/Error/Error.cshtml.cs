using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Account.Error
{
    // Where the host re-executes a failed browser request (Program.cs: status code pages and the exception handler), also for a POST that failed.
    // Never shows exception text, paths or SQL: a 5xx page names the request and the time only.
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    public sealed class ErrorModel(FrontendAssets assets, UiText texts) : JularrPage
    {
        private int _code = 404;

        protected override void Define(Page page)
        {
            page.Shell = !ErrorState.NeedsStandaloneLayout(_code);
            page.Title("error.page", texts["error." + _code.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".title"]);
            page.Asset("Pages/Shared/ErrorState.entry.ts");

            var original = OriginalAddress();
            var retry = HttpMethods.IsGet(Request.Method) ? original : null;
            var signIn = "/Account/Login" + (original is null ? "" : "?ReturnUrl=" + Uri.EscapeDataString(original));
            var reference = _code >= 500 ? HttpContext.TraceIdentifier + " · " + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture) : null;
            page.Content.Add(new ErrorState(assets, _code, retry, signIn, reference, !page.Shell, original ?? Request.Path.Value));
        }

        public IActionResult OnGet(int? code) => Show(code);

        public IActionResult OnPost(int? code) => Show(code);

        private IActionResult Show(int? code)
        {
            _code = ErrorState.Normalize(code ?? 404);

            // A re-executed request keeps the status it failed with; a visit to the address itself answers with the status it shows.
            if (HttpContext.Features.Get<IStatusCodeReExecuteFeature>() is null) Response.StatusCode = code ?? 404;

            PreparePage();
            return Page();
        }

        // The address that failed, for Retry and the way back after signing in; only a local one.
        private string? OriginalAddress()
        {
            var address = HttpContext.Features.Get<IStatusCodeReExecuteFeature>() is { } status
                ? status.OriginalPathBase + status.OriginalPath + status.OriginalQueryString : null;

            return address is { Length: > 0 } && address[0] == '/' && (address.Length == 1 || address[1] != '/' && address[1] != '\\')
                && !address.StartsWith("/Error/", StringComparison.OrdinalIgnoreCase) && !address.Any(char.IsControl) ? address : null;
        }
    }
}
