using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Account.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Service.Admin.Account.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin
{
    public sealed class AccountsModel(ReadAccountsV1 accounts, ReadAccountV1 account, UpdateAccountV1 update, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private PageResult<AccountDetailsV1>? _accounts;
        private AccountDetailsV1? _selected;
        private UpdateAccountDataV1? _input;

        [BindProperty(SupportsGet = true)]
        public long? AccountId { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("admin.accounts.title");
            page.Navigation("nav.admin");

            // Account administration, not quotas.
            if (_accounts is not null)
            {
                page.Content.Add(new Table<AccountDetailsV1>(_accounts.Items)
                    .Text(item => item.DisplayName, "admin.accounts.name")
                    .Text(item => item.Email, "account.field.email")
                    .Resource(item => item.AccountRoleTypeId, "admin.accounts.role", RoleKey)
                    .Resource(item => item.IsEnabled, "admin.accounts.status", enabled => enabled ? "admin.accounts.enabled" : "admin.accounts.disabled")
                    .Link(item => item.Id, "common.actions", id => Address(id), "common.edit"));
                if (PageNumber > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", Address(pageNumber: PageNumber - 1)));
                if (_accounts.HasMore is true) page.Content.Add(new A(new Text("common.next")).Attribute("href", Address(pageNumber: PageNumber + 1)));
            }

            if (_selected is not null)
            {
                page.Content.Add(new P(new ValueText(_selected.Email)));
                var roles = Enum.GetValues<AccountRoleType>().Select(role => new SelectOption(((byte)role).ToString(CultureInfo.InvariantCulture), texts[RoleKey(role)]));
                var states = new[] { new SelectOption("True", texts["admin.accounts.enabled"]), new SelectOption("False", texts["admin.accounts.disabled"]) };
                page.Content.Add(new Form<UpdateAccountDataV1>(form =>
                {
                    form.Hidden(item => item.UpdatedAt);
                    form.Text(item => item.DisplayName, "admin.accounts.name");
                    form.Select(item => item.AccountRoleTypeId, "admin.accounts.role", roles);
                    form.Select(item => item.IsEnabled, "admin.accounts.status", states);
                    form.Submit("common.save");
                }, Address(_selected.Id), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    _input ?? new UpdateAccountDataV1(_selected.DisplayName, _selected.AccountRoleTypeId, _selected.IsEnabled, _selected.UpdatedAt), Error));
                page.Content.Add(new P(new Text("admin.accounts.sessionWarning")));
            }
            else if (Error is not null)
            {
                page.Content.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
            }

            if (TempData["AccountSaved"] is true) page.Content.Add(new P(new Text("profile.settings.saved")).Attribute("role", "status"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? displayName, AccountRoleType accountRoleTypeId, bool isEnabled, DateTime updatedAt)
        {
            await LoadAsync();
            _input = new UpdateAccountDataV1(displayName ?? "", accountRoleTypeId, isEnabled, updatedAt);
            await update.ExecuteAsync<UpdateResult>(SelectedParameters(), _input, HttpContext.RequestAborted);
            TempData["AccountSaved"] = true;
            return LocalRedirect(Address(AccountId));
        }

        private AccountParametersV1 SelectedParameters() => new(AccountId ?? throw new KeyNotFoundException("No account was selected."));

        private async Task LoadAsync()
        {
            var token = HttpContext.RequestAborted;
            _accounts = await accounts.ExecuteAsync<PageResult<AccountDetailsV1>>(new ReadAccountsParametersV1(PageNumber), token);
            if (AccountId is not null) _selected = await account.ExecuteAsync<AccountDetailsV1>(SelectedParameters(), token);
        }

        private static string RoleKey(AccountRoleType role) => "admin.accounts.role." + role.ToString().ToLowerInvariant();

        private static string Address(long? accountId = null, int pageNumber = 1)
        {
            var address = "/Admin/Accounts?PageNumber=" + pageNumber.ToString(CultureInfo.InvariantCulture);
            if (accountId is not null) address += "&AccountId=" + accountId.Value.ToString(CultureInfo.InvariantCulture);
            return address;
        }
    }
}
