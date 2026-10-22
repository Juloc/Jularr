using System.Globalization;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Request.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Backend.Service.Admin.Request.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Rules
{
    public sealed class RulesModel(ReadRequestRulesV1 rules, CreateRequestRuleV1 create, UpdateRequestRuleV1 update, DeleteRequestRuleV1 delete,
        ReadAcquisitionProfilesV1 profiles, ReadInstanceMediaV1 media, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private IReadOnlyList<RequestRuleDetailsV1> _rules = [];
        private IReadOnlyList<AcquisitionProfileDetailsV1> _profiles = [];
        private IReadOnlyList<InstanceMediaDetailsV1> _media = [];
        private RequestRuleDetailsV1? _selected;
        private SaveRequestRuleDataV1? _input;

        [BindProperty(SupportsGet = true)]
        public long? RequestRuleId { get; set; }

        [BindProperty(SupportsGet = true)]
        public bool New { get; set; }

        protected override void Define(Page page)
        {
            page.Title("admin.rules.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Rules/Rules.entry.scss");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var editing = _selected is not null || New;

            page.Content.Add(new Split(split =>
            {
                split.Primary.Add(new A(new Text("request.rule.add")).Class("button button--primary").Attribute("href", Address(isNew: true)));
                split.Primary.Add(new Ul([.. _rules.Select(rule => new Li(RuleLink(rule)))]).Class("request-rules"));

                if (editing)
                {
                    split.Secondary.Add(RuleForm(csrf));
                    if (_selected is { IsDefault: false } selected)
                    {
                        split.Secondary.Add(new Form<DeleteRequestRuleParametersV1>(form =>
                        {
                            form.Hidden(item => item.Version);
                            form.Submit("request.rule.delete");
                        }, Address(selected.Id, handler: "Delete"), csrf, new DeleteRequestRuleParametersV1(selected.Id, selected.Version)));
                    }
                }
            }).MasterDetail(editing, Address()));

            if (FormError is not null && _input is null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        // Name, its limits and approval, then the media it allows.
        private HtmlElement RuleLink(RequestRuleDetailsV1 rule)
        {
            var limit = rule.RequestLimit is { } count
                ? string.Format(CultureInfo.InvariantCulture, texts["request.rule.summary"], count, rule.PeriodDays)
                : texts["request.rule.unlimited"];
            var approval = texts[rule.IsAutomaticApproval ? "request.rule.automatic" : "request.rule.manual"];
            var media = string.Join(" · ", rule.AllowedMediaKeys.Select(key => texts[MediaLabel(key)]));
            var title = new List<IUiComponent> { new Span(new ValueText(rule.Name)).Class("request-rules__name") };
            if (rule.IsDefault) title.Add(new Tag(new Text("request.rule.isDefault")).Accent());

            var link = new A(new Div([.. title]).Class("request-rules__title"), new P(new ValueText(limit + " · " + approval)), new P(new ValueText(media)).Class("request-rules__media"))
                .Class("request-rules__item").Attribute("href", Address(rule.Id));
            if (rule.Id == _selected?.Id) link.Attribute("aria-current", "true");
            return link;
        }

        private Form<SaveRequestRuleDataV1> RuleForm(string? csrf)
        {
            var input = _input ?? (_selected is { } selected
                ? new SaveRequestRuleDataV1(selected.Name, selected.Description, selected.RequestLimit, selected.PeriodDays, selected.IsAutomaticApproval, selected.AllowedMediaKeys,
                    selected.AcquisitionProfileIds, selected.IsDefault, selected.Version)
                : new SaveRequestRuleDataV1("", null, 10, 30, false, [.. _media.Select(item => item.Key)], [], false, null));

            return new Form<SaveRequestRuleDataV1>(form =>
            {
                form.Hidden(item => item.Version);
                form.Text(item => item.Name, "request.rule.name");
                form.Multiline(item => item.Description, "request.rule.description");
                form.Number(item => item.RequestLimit, "request.rule.limit");
                form.Number(item => item.PeriodDays, "request.rule.period", affix: FieldAffix.Unit("unit.days"));
                form.Select(item => item.IsAutomaticApproval, "request.rule.approval", Approvals(texts));
                form.Choices(item => item.AllowedMediaKeys, "request.rule.media", _media.Select(item => new SelectOption(item.Key, texts[MediaTypeLabel.Key(item.Key, item.IsSubtype)])));
                form.Choices(item => item.AcquisitionProfileIds, "request.rule.profiles",
                    _profiles.Select(profile => new SelectOption(profile.Id.ToString(CultureInfo.InvariantCulture), profile.Name)));
                // The default stays until another rule takes its place.
                if (input.IsDefault && _selected is { IsDefault: true }) form.Hidden(item => item.IsDefault);
                else form.Toggle(item => item.IsDefault, "request.rule.isDefault");
                form.Save();
            }, Address(RequestRuleId, New), csrf, input, _input is null ? null : FormError);
        }

        internal static SelectOption[] Approvals(UiText texts) =>
            [new SelectOption("False", texts["request.rule.manual"]), new SelectOption("True", texts["request.rule.automatic"])];

        private string MediaLabel(string key) => MediaTypeLabel.Key(key, _media.FirstOrDefault(item => item.Key == key)?.IsSubtype ?? false);

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? name, string? description, int? requestLimit, [BindRequired] int periodDays, [BindRequired] bool isAutomaticApproval,
            string[]? allowedMediaKeys, long[]? acquisitionProfileIds, bool isDefault, long? version)
        {
            _input = new SaveRequestRuleDataV1(name ?? "", description, requestLimit, periodDays, isAutomaticApproval, allowedMediaKeys ?? [], acquisitionProfileIds ?? [], isDefault, version);
            if (RequestRuleId is { } existing)
            {
                await update.ExecuteAsync<UpdateResult>(new RequestRuleParametersV1(existing), _input, HttpContext.RequestAborted);
            }
            else
            {
                RequestRuleId = (await create.ExecuteAsync<CreateRequestRuleResultV1>(new ReadRequestRulesParametersV1(), _input, HttpContext.RequestAborted)).Id;
            }

            return LocalRedirect(Address(RequestRuleId));
        }

        public async Task<IActionResult> OnPostDeleteAsync([BindRequired] long version)
        {
            var id = RequestRuleId ?? throw new KeyNotFoundException("No rule was selected.");
            await delete.ExecuteAsync<UpdateResult>(new DeleteRequestRuleParametersV1(id, version), default, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        protected override async Task LoadDataAsync()
        {
            var token = HttpContext.RequestAborted;
            _rules = await rules.ExecuteAsync<BaseDataList<RequestRuleDetailsV1>>(new ReadRequestRulesParametersV1(), token);
            _profiles = await profiles.ExecuteAsync<BaseDataList<AcquisitionProfileDetailsV1>>(new ReadAcquisitionProfilesParametersV1(), token);
            _media = await media.ExecuteAsync<BaseDataList<InstanceMediaDetailsV1>>(new ReadInstanceMediaParametersV1(), token);
            _selected = RequestRuleId is { } id ? _rules.FirstOrDefault(rule => rule.Id == id) ?? throw new KeyNotFoundException("The rule was not found.") : null;
        }

        private static string Address(long? id = null, bool isNew = false, string? handler = null)
        {
            var address = "/Admin/Rules";
            if (id is { } ruleId) address += "?RequestRuleId=" + ruleId.ToString(CultureInfo.InvariantCulture);
            else if (isNew) address += "?New=true";
            return handler is null ? address : address + (address.Contains('?') ? "&" : "?") + "handler=" + handler;
        }
    }
}
