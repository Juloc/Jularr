using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Account.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Service.Admin.Account.V1;
using Jularr.Backend.Service.Core;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Accounts
{
    public sealed class AccountsModel(ReadAccountsV1 accounts, ReadAccountV1 account, UpdateAccountV1 update, ReadAccountProfilesV1 profiles,
        AddAccountProfileV1 addProfile, RemoveAccountProfileV1 removeProfile, ReadAccountSessionsV1 sessions, RevokeAccountSessionV1 revokeSession,
        IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private PageResult<AccountDetailsV1>? _accounts;
        private AccountDetailsV1? _selected;
        private UpdateAccountDataV1? _input;
        private PageResult<AccountProfileDetailsV1>? _profiles;
        private AccountProfileParametersV1? _assignment;
        private bool _removingProfile;
        private PageResult<AccountSessionDetailsV1>? _sessions;
        private RevokeAccountSessionParametersV1? _revocation;

        [BindProperty(SupportsGet = true)]
        public long? AccountId { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public int ProfilePageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public int SessionPageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("admin.accounts.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Accounts/Accounts.entry.scss");

            // Responsive list and editor.
            page.Content.Add(new Split(split =>
            {
                // Account administration, not quotas.
                if (_accounts is not null)
                {
                    split.Primary.Add(new Table<AccountDetailsV1>(_accounts.Items)
                        .Text(item => item.DisplayName, "admin.accounts.name")
                        .Text(item => item.Email, "account.field.email")
                        .Resource(item => item.AccountRoleTypeId, "admin.accounts.role", RoleKey)
                        .Resource(item => item.IsEnabled, "admin.accounts.status", enabled => enabled ? "admin.accounts.enabled" : "admin.accounts.disabled")
                        .Link(item => item.Id, "common.actions", id => Address(id), "common.edit"));
                    if (PageNumber > 1) split.Primary.Add(new A(new Text("common.previous")).Attribute("href", Address(pageNumber: PageNumber - 1)));
                    if (_accounts.Items.Count == _accounts.PageSize) split.Primary.Add(new A(new Text("common.next")).Attribute("href", Address(pageNumber: PageNumber + 1)));
                }

                if (_selected is not null)
                {
                    split.Secondary.Add(new P(new ValueText(_selected.Email)));
                    var roles = Enum.GetValues<AccountRoleType>().Select(role => new SelectOption(((byte)role).ToString(CultureInfo.InvariantCulture), texts[RoleKey(role)]));
                    var states = new[] { new SelectOption("True", texts["admin.accounts.enabled"]), new SelectOption("False", texts["admin.accounts.disabled"]) };
                    split.Secondary.Add(new Form<UpdateAccountDataV1>(form =>
                    {
                        form.Hidden(item => item.UpdatedAt);
                        form.Text(item => item.DisplayName, "admin.accounts.name");
                        form.Select(item => item.AccountRoleTypeId, "admin.accounts.role", roles);
                        form.Select(item => item.IsEnabled, "admin.accounts.status", states);
                        form.Submit("common.save");
                    }, Address(_selected.Id), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                        _input ?? new UpdateAccountDataV1(_selected.DisplayName, _selected.AccountRoleTypeId, _selected.IsEnabled, _selected.UpdatedAt), _input is null ? null : Error));
                    split.Secondary.Add(new P(new Text("admin.accounts.sessionWarning")));

                    // Assign existing profile access.
                    split.Secondary.Add(new H2(new Text("admin.accounts.profiles")));
                    split.Secondary.Add(new Form<AccountProfileParametersV1>(form =>
                    {
                        form.Number(item => item.ProfileId, "admin.accounts.profileId");
                        form.Submit("admin.accounts.profileAdd");
                    }, Address(_selected.Id) + "&handler=AddProfile", antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                        _removingProfile ? null : _assignment, _assignment is null || _removingProfile ? null : Error));

                    foreach (var profile in _profiles?.Items ?? [])
                    {
                        split.Secondary.Add(new P(new ValueText(profile.DisplayName + " · " + profile.ProfileId.ToString(CultureInfo.InvariantCulture))));
                        if (profile.OwnerAccountId == _selected.Id)
                        {
                            split.Secondary.Add(new P(new Text("admin.accounts.profileOwned")));
                        }
                        else
                        {
                            split.Secondary.Add(new Form<AccountProfileParametersV1>(form =>
                            {
                                form.Hidden(item => item.ProfileId);
                                form.Submit("admin.accounts.profileRemove");
                            }, Address(_selected.Id) + "&handler=RemoveProfile", antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                                new AccountProfileParametersV1(_selected.Id, profile.ProfileId), _removingProfile && _assignment?.ProfileId == profile.ProfileId ? Error : null));
                        }
                    }

                    if (ProfilePageNumber > 1) split.Secondary.Add(new A(new Text("common.previous")).Attribute("href", Address(_selected.Id) + "&ProfilePageNumber=" + (ProfilePageNumber - 1)));
                    if (_profiles is { } && _profiles.Items.Count == _profiles.PageSize) split.Secondary.Add(new A(new Text("common.next")).Attribute("href", Address(_selected.Id) + "&ProfilePageNumber=" + (ProfilePageNumber + 1)));

                    // Inspect and revoke account sessions.
                    split.Secondary.Add(new H2(new Text("settings.sessions")));
                    foreach (var session in _sessions?.Items ?? [])
                    {
                        split.Secondary.Add(new P(new Text(session.IsCurrent ? "session.current" : "session.other"), new Span(new ValueText(" · " + session.CreatedAt.ToString("u", CultureInfo.InvariantCulture)))));
                        split.Secondary.Add(new Form<RevokeAccountSessionParametersV1>(form =>
                        {
                            form.Hidden(item => item.SessionPublicId);
                            form.Submit("session.revoke");
                        }, Address(_selected.Id) + "&handler=RevokeSession", antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                            new RevokeAccountSessionParametersV1(_selected.Id, session.Id)));
                    }

                    if (SessionPageNumber > 1) split.Secondary.Add(new A(new Text("common.previous")).Attribute("href", Address(_selected.Id) + "&SessionPageNumber=" + (SessionPageNumber - 1)));
                    if (_sessions is { } && _sessions.Items.Count == _sessions.PageSize) split.Secondary.Add(new A(new Text("common.next")).Attribute("href", Address(_selected.Id) + "&SessionPageNumber=" + (SessionPageNumber + 1)));
                    if (_revocation is not null && Error is not null) split.Secondary.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
                }
                else if (Error is not null)
                {
                    split.Primary.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
                }
            }).MasterDetail(_selected is not null, Address()));

            if (TempData["AccountSaved"] is true) page.Content.Add(new P(new Text("profile.settings.saved")).Attribute("role", "status"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? displayName, AccountRoleType accountRoleTypeId, [BindRequired] bool isEnabled, DateTime updatedAt)
        {
            _input = new UpdateAccountDataV1(displayName ?? "", accountRoleTypeId, isEnabled, updatedAt);
            await update.ExecuteAsync<UpdateResult>(SelectedParameters(), _input, HttpContext.RequestAborted);
            TempData["AccountSaved"] = true;
            return LocalRedirect(Address(AccountId));
        }

        private AccountParametersV1 SelectedParameters() => new(AccountId ?? throw new KeyNotFoundException("No account was selected."));

        public async Task<IActionResult> OnPostAddProfileAsync(long profileId)
        {
            _assignment = new AccountProfileParametersV1(SelectedParameters().AccountId, profileId);
            await addProfile.ExecuteAsync<UpdateResult>(_assignment, default, HttpContext.RequestAborted);
            return LocalRedirect(Address(AccountId));
        }

        public async Task<IActionResult> OnPostRemoveProfileAsync(long profileId)
        {
            _removingProfile = true;
            _assignment = new AccountProfileParametersV1(SelectedParameters().AccountId, profileId);
            await removeProfile.ExecuteAsync<UpdateResult>(_assignment, default, HttpContext.RequestAborted);
            return LocalRedirect(Address(AccountId));
        }

        public async Task<IActionResult> OnPostRevokeSessionAsync(Guid sessionPublicId)
        {
            _revocation = new RevokeAccountSessionParametersV1(SelectedParameters().AccountId, sessionPublicId);
            var result = await revokeSession.ExecuteAsync<RevokeAccountSessionResultV1>(_revocation, default, HttpContext.RequestAborted);
            await ServiceApi.ApplyAuthenticationAsync(HttpContext, result, antiforgery);
            return result.EndsCurrentSession ? RedirectToPage("/Account/Login/Login") : LocalRedirect(Address(AccountId));
        }

        protected override async Task LoadDataAsync()
        {
            var token = HttpContext.RequestAborted;
            _accounts = await accounts.ExecuteAsync<PageResult<AccountDetailsV1>>(new ReadAccountsParametersV1(PageNumber), token);
            if (AccountId is not null)
            {
                _selected = await account.ExecuteAsync<AccountDetailsV1>(SelectedParameters(), token);
                _profiles = await profiles.ExecuteAsync<PageResult<AccountProfileDetailsV1>>(new ReadAccountProfilesParametersV1(AccountId.Value, ProfilePageNumber), token);
                _sessions = await sessions.ExecuteAsync<PageResult<AccountSessionDetailsV1>>(new ReadAccountSessionsParametersV1(AccountId.Value, SessionPageNumber), token);
            }
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
