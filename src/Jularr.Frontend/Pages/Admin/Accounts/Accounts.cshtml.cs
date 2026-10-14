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
        ReadProfileMediaAccessV1 readMedia, UpdateProfileMediaAccessV1 updateMedia, ResetProfilePinV1 resetPin, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private PageResult<AccountDetailsV1>? _accounts;
        private AccountDetailsV1? _selected;
        private UpdateAccountDataV1? _input;
        private PageResult<AccountProfileDetailsV1>? _profiles;
        private AccountProfileParametersV1? _assignment;
        private bool _removingProfile;
        private ILookup<long, ProfileMediaAccessV1> _media = Array.Empty<ProfileMediaAccessV1>().ToLookup(item => item.ProfileId);
        private long? _mediaProfileId;
        private long? _pinProfileId;
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
                        form.Text(item => item.DisplayName, "admin.accounts.name");
                        form.Select(item => item.AccountRoleTypeId, "admin.accounts.role", roles);
                        form.Select(item => item.IsEnabled, "admin.accounts.status", states);
                        form.Select(item => item.MayConfirmSources, "admin.accounts.mayConfirmSources",
                            [new SelectOption("True", texts["common.yes"]), new SelectOption("False", texts["common.no"])]);
                        form.Submit("common.save");
                    }, Address(_selected.Id), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                        _input ?? new UpdateAccountDataV1(_selected.DisplayName, _selected.AccountRoleTypeId, _selected.IsEnabled, _selected.MayConfirmSources), _input is null ? null : FormError));
                    split.Secondary.Add(new P(new Text("admin.accounts.sessionWarning")));

                    // Assign existing profile access.
                    split.Secondary.Add(new H2(new Text("admin.accounts.profiles")));
                    split.Secondary.Add(new Form<AccountProfileParametersV1>(form =>
                    {
                        form.Number(item => item.ProfileId, "admin.accounts.profileId");
                        form.Submit("admin.accounts.profileAdd");
                    }, Address(_selected.Id) + "&handler=AddProfile", antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                        _removingProfile ? null : _assignment, _assignment is null || _removingProfile ? null : FormError));

                    foreach (var profile in _profiles?.Items ?? [])
                    {
                        split.Secondary.Add(new P(new ValueText(profile.DisplayName + " · " + profile.ProfileId.ToString(CultureInfo.InvariantCulture))));
                        if (_media[profile.ProfileId].ToArray() is { Length: > 0 } media)
                        {
                            split.Secondary.Add(new Form<UpdateProfileMediaAccessDataV1>(form =>
                            {
                                form.Choices(item => item.BlockedKeys, "admin.accounts.profileMediaBlocked",
                                    media.Select(item => new SelectOption(item.Key, texts[MediaTypeLabel.Key(item.Key, item.IsSubtype)])));
                                form.Submit("common.save");
                            }, Address(_selected.Id) + "&handler=ProfileMedia&profileId=" + profile.ProfileId.ToString(CultureInfo.InvariantCulture), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                                new UpdateProfileMediaAccessDataV1([.. media.Where(item => item.IsBlocked).Select(item => item.Key)]), _mediaProfileId == profile.ProfileId ? FormError : null));
                        }

                        // A forgotten PIN is reset here.
                        if (profile.HasPin && texts.CanManageProfiles())
                        {
                            split.Secondary.Add(new Form<ProfilePinParametersV1>(form =>
                            {
                                form.Hidden(item => item.ProfileId);
                                form.Submit("admin.accounts.profilePinReset");
                            }, Address(_selected.Id) + "&handler=ResetPin", antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                                new ProfilePinParametersV1(profile.ProfileId), _pinProfileId == profile.ProfileId ? FormError : null));
                        }

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
                                new AccountProfileParametersV1(_selected.Id, profile.ProfileId), _removingProfile && _assignment?.ProfileId == profile.ProfileId ? FormError : null));
                        }
                    }

                    if (ProfilePageNumber > 1) split.Secondary.Add(new A(new Text("common.previous")).Attribute("href", Address(_selected.Id) + "&ProfilePageNumber=" + (ProfilePageNumber - 1)));
                    if (_profiles is { } && _profiles.Items.Count == _profiles.PageSize) split.Secondary.Add(new A(new Text("common.next")).Attribute("href", Address(_selected.Id) + "&ProfilePageNumber=" + (ProfilePageNumber + 1)));

                    // Inspect and revoke account sessions.
                    split.Secondary.Add(new H2(new Text("settings.sessions")));
                    foreach (var session in _sessions?.Items ?? [])
                    {
                        split.Secondary.Add(new P(new Text(session.IsCurrent ? "session.current" : "session.other"),
                            new Span(new ValueText((session.DeviceName is { } device ? " · " + DeviceNames.Show(device, texts) + " (" + session.DevicePlatform + ")" : "") + " · " + session.CreatedAt.ToString("u", CultureInfo.InvariantCulture)))));
                        split.Secondary.Add(new Form<RevokeAccountSessionParametersV1>(form =>
                        {
                            form.Hidden(item => item.SessionPublicId);
                            form.Submit("session.revoke");
                        }, Address(_selected.Id) + "&handler=RevokeSession", antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                            new RevokeAccountSessionParametersV1(_selected.Id, session.Id)));
                    }

                    if (SessionPageNumber > 1) split.Secondary.Add(new A(new Text("common.previous")).Attribute("href", Address(_selected.Id) + "&SessionPageNumber=" + (SessionPageNumber - 1)));
                    if (_sessions is { } && _sessions.Items.Count == _sessions.PageSize) split.Secondary.Add(new A(new Text("common.next")).Attribute("href", Address(_selected.Id) + "&SessionPageNumber=" + (SessionPageNumber + 1)));
                    if (_revocation is not null && FormError is not null) split.Secondary.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
                }
                else if (FormError is not null)
                {
                    split.Primary.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
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

        public async Task<IActionResult> OnPostAsync(string? displayName, AccountRoleType accountRoleTypeId, [BindRequired] bool isEnabled, bool? mayConfirmSources)
        {
            _input = new UpdateAccountDataV1(displayName ?? "", accountRoleTypeId, isEnabled, mayConfirmSources);
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

        public async Task<IActionResult> OnPostProfileMediaAsync(long profileId, string[]? blockedKeys)
        {
            _mediaProfileId = profileId;
            await updateMedia.ExecuteAsync<UpdateResult>(new ProfileMediaAccessParametersV1(profileId), new UpdateProfileMediaAccessDataV1(blockedKeys ?? []), HttpContext.RequestAborted);
            return LocalRedirect(Address(AccountId));
        }

        public async Task<IActionResult> OnPostResetPinAsync(long profileId)
        {
            _pinProfileId = profileId;
            await resetPin.ExecuteAsync<UpdateResult>(new ProfilePinParametersV1(profileId), default, HttpContext.RequestAborted);
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
                _media = (await readMedia.ExecuteAsync<BaseDataList<ProfileMediaAccessV1>>(new AccountProfileMediaParametersV1(AccountId.Value), token)).ToLookup(item => item.ProfileId);

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
