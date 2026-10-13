using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Account.V1;
using Jularr.Backend.Service.Admin.Account.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Users
{
    public sealed class UsersModel(ReadAccountsV1 accounts) : JularrPage
    {
        private PageResult<AccountDetailsV1>? _accounts;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("admin.users.title");
            page.Navigation("nav.admin");
            page.Content.Add(AdminNavigation.Requests);

            // Canonical read-only directory.
            if (_accounts is { } accounts)
            {
                page.Content.Add(new Table<AccountDetailsV1>(accounts.Items)
                    .Text(item => item.DisplayName, "admin.accounts.name")
                    .Text(item => item.Email, "account.field.email")
                    .Resource(item => item.AccountRoleTypeId, "admin.accounts.role", role => "admin.accounts.role." + role.ToString().ToLowerInvariant())
                    .Resource(item => item.IsEnabled, "admin.accounts.status", enabled => enabled ? "admin.accounts.enabled" : "admin.accounts.disabled")
                    .Link(item => item.Id, "common.open", id => "/Admin/Accounts?accountId=" + id.ToString(CultureInfo.InvariantCulture), "common.open"));

                if (accounts.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", "/Admin/Requests/Users?pageNumber=" + (accounts.Page - 1)));
                if (accounts.Items.Count == accounts.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", "/Admin/Requests/Users?pageNumber=" + (accounts.Page + 1)));
            }

            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _accounts = await accounts.ExecuteAsync<PageResult<AccountDetailsV1>>(new ReadAccountsParametersV1(PageNumber), HttpContext.RequestAborted);
        }
    }
}
