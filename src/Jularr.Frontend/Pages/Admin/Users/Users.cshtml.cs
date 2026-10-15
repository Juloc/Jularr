using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Request.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Backend.Service.Admin.Request.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Admin.Rules;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Users
{
    public sealed class UsersModel(ReadRequestUsersV1 users, ReadAccountRequestRuleV1 readAccount, SaveAccountRequestRuleV1 save, ReadRequestRulesV1 rules,
        ReadAcquisitionProfilesV1 profiles, ReadInstanceMediaV1 media, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private PageResult<AccountRequestRuleV1>? _users;
        private AccountRequestRuleV1? _selected;
        private IReadOnlyList<RequestRuleDetailsV1> _rules = [];
        private IReadOnlyList<AcquisitionProfileDetailsV1> _profiles = [];
        private IReadOnlyList<InstanceMediaDetailsV1> _media = [];
        private SaveAccountRequestRuleDataV1? _input;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public long? AccountId { get; set; }

        protected override void Define(Page page)
        {
            page.Title("admin.users.title");
            page.Navigation("nav.admin");
            if (_users is not { } users)
            {
                return;
            }

            page.Content.Add(new Split(split =>
            {
                // The whole row opens its editor.
                split.Primary.Add(new Table<AccountRequestRuleV1>(users.Items)
                    .Content("admin.accounts.name", item => new A(new ValueText(item.DisplayName)).Attribute("href", Address(item.AccountId, users.Page)))
                    .Resource(item => item.AccountRoleTypeId, "admin.accounts.role", role => "admin.accounts.role." + role.ToString().ToLowerInvariant())
                    .Content("request.rule.profile", item => new ValueText(item.RequestRuleName + (item.IsInherited ? " · " + texts["request.rule.inherited"] : "")
                        + (item.IsOverridden ? " · " + texts["request.rule.overridden"] : "")))
                    .Content("request.rule.limitColumn", item => new ValueText(Limit(item)))
                    .Content("request.rule.approval", item => new Text(item.IsAutomaticApproval ? "request.rule.automatic" : "request.rule.manual")));

                if (users.Page > 1) split.Primary.Add(new A(new Text("common.previous")).Attribute("href", Address(null, users.Page - 1)));
                if (users.Items.Count == users.PageSize) split.Primary.Add(new A(new Text("common.next")).Attribute("href", Address(null, users.Page + 1)));
                if (_selected is { } selected) split.Secondary.Add(AccountForm(selected));
            }).MasterDetail(AccountId is not null, Address(null, PageNumber)));
        }

        private Form<SaveAccountRequestRuleDataV1> AccountForm(AccountRequestRuleV1 account)
        {
            var standard = _rules.FirstOrDefault(rule => rule.IsDefault)?.Name ?? "";
            var options = new[] { new SelectOption("", string.Format(CultureInfo.InvariantCulture, texts["request.rule.inheritDefault"], standard)) }
                .Concat(_rules.Select(rule => new SelectOption(rule.Id.ToString(CultureInfo.InvariantCulture), rule.Name)));
            var input = _input ?? new SaveAccountRequestRuleDataV1(account.IsInherited ? null : account.RequestRuleId, account.IsOverridden, account.RequestLimit, account.PeriodDays,
                account.IsAutomaticApproval, account.AllowedMediaKeys, account.AcquisitionProfileIds);

            // Without overriding, the selected rule's values apply and follow its later edits.
            return new Form<SaveAccountRequestRuleDataV1>(form =>
            {
                form.Select(item => item.RequestRuleId, "request.rule.profile", options);
                form.Toggle(item => item.IsOverridden, "request.rule.override");
                form.Number(item => item.RequestLimit, "request.rule.limit");
                form.Number(item => item.PeriodDays, "request.rule.period");
                form.Select(item => item.IsAutomaticApproval, "request.rule.approval", RulesModel.Approvals(texts));
                form.Choices(item => item.AllowedMediaKeys, "request.rule.media", _media.Select(item => new SelectOption(item.Key, texts[MediaTypeLabel.Key(item.Key, item.IsSubtype)])));
                form.Choices(item => item.AcquisitionProfileIds, "request.rule.profiles",
                    _profiles.Select(profile => new SelectOption(profile.Id.ToString(CultureInfo.InvariantCulture), profile.Name)));
                form.Submit("common.save");
            }, Address(account.AccountId, PageNumber), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, input, _input is null ? null : FormError);
        }

        private string Limit(AccountRequestRuleV1 item) => item.RequestLimit is { } count
            ? string.Format(CultureInfo.InvariantCulture, texts["request.rule.summary"], count, item.PeriodDays)
            : texts["request.rule.unlimited"];

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(long? requestRuleId, bool isOverridden, int? requestLimit, [BindRequired] int periodDays, [BindRequired] bool isAutomaticApproval,
            string[]? allowedMediaKeys, long[]? acquisitionProfileIds)
        {
            var accountId = AccountId ?? throw new KeyNotFoundException("No account was selected.");
            _input = new SaveAccountRequestRuleDataV1(requestRuleId, isOverridden, requestLimit, periodDays, isAutomaticApproval, allowedMediaKeys ?? [], acquisitionProfileIds ?? []);
            await save.ExecuteAsync<UpdateResult>(new AccountRequestRuleParametersV1(accountId), _input, HttpContext.RequestAborted);
            return LocalRedirect(Address(accountId, PageNumber));
        }

        protected override async Task LoadDataAsync()
        {
            var token = HttpContext.RequestAborted;
            _users = await users.ExecuteAsync<PageResult<AccountRequestRuleV1>>(new ReadRequestUsersParametersV1(PageNumber), token);

            // The first account opens on entry.
            var accountId = AccountId ?? _users.Items.FirstOrDefault()?.AccountId;
            if (accountId is { } id)
            {
                _selected = await readAccount.ExecuteAsync<AccountRequestRuleV1>(new AccountRequestRuleParametersV1(id), token);
                _rules = await rules.ExecuteAsync<BaseDataList<RequestRuleDetailsV1>>(new ReadRequestRulesParametersV1(), token);
                _profiles = await profiles.ExecuteAsync<BaseDataList<AcquisitionProfileDetailsV1>>(new ReadAcquisitionProfilesParametersV1(), token);
                _media = await media.ExecuteAsync<BaseDataList<InstanceMediaDetailsV1>>(new ReadInstanceMediaParametersV1(), token);
            }
        }

        private static string Address(long? accountId, int pageNumber = 1)
        {
            var address = "/Admin/Requests/Users?PageNumber=" + pageNumber.ToString(CultureInfo.InvariantCulture);
            return accountId is { } id ? address + "&AccountId=" + id.ToString(CultureInfo.InvariantCulture) : address;
        }
    }
}
