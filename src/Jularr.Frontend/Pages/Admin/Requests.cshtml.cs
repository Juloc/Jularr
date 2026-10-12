using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin
{
    public sealed class RequestsModel(ReadAuthenticationV1 authentication) : JularrPage
    {
        private bool _canReadRules;

        protected override void Define(Page page)
        {
            page.Title("admin.requests.title");
            page.Navigation("nav.admin");
            page.Content.Add(AdminNavigation.RequestsFor(_canReadRules));

            // Queue awaits canonical request data.
        }

        public async Task<IActionResult> OnGetAsync()
        {
            // Consume verified backend grants.
            var result = await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: HttpContext.RequestAborted);
            _canReadRules = result.Permissions.Contains(ServiceApi.RulesReadPolicyName, StringComparer.Ordinal);

            PreparePage();
            return Page();
        }
    }
}
