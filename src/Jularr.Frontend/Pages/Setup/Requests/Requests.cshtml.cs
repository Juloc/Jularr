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

namespace Jularr.Frontend.Pages.Setup.Requests
{
    // The requests step, as people ask for titles in Seerr: which media may be requested, whether a request is approved at once and how many a person may make.
    // These are the settings of the default request rule; other rules are added later under Administration.
    public sealed class RequestsModel(ReadRequestRulesV1 rules, UpdateRequestRuleV1 update, UpdateSetupProgressV1 progress, ReadSetupProgressV1 readProgress, ReadAcquisitionProfilesV1 profiles, ReadInstanceMediaV1 media, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private RequestRuleDetailsV1? _default;
        private IReadOnlyList<AcquisitionProfileDetailsV1> _profiles = [];
        private IReadOnlyList<InstanceMediaDetailsV1> _media = [];
        private SaveRequestRuleDataV1? _input;

        protected override void Define(Page page)
        {
            page.Title("setup.step.Requests");
            if (_default is not { } rule) return;
            var input = _input ?? new SaveRequestRuleDataV1(rule.Name, rule.Description, rule.RequestLimit, rule.PeriodDays, rule.IsAutomaticApproval, rule.AllowedMediaKeys, rule.AcquisitionProfileIds, rule.IsDefault, rule.Version);
            page.Content.Add(new Div(new Form<SaveRequestRuleDataV1>(form =>
            {
                form.Hidden(item => item.Version);
                form.Hidden(item => item.Name);
                form.Hidden(item => item.IsDefault);
                form.Choices(item => item.AllowedMediaKeys, "request.rule.media", _media.Select(item => new SelectOption(item.Key, texts[MediaTypeLabel.Key(item.Key, item.IsSubtype)])));
                form.Select(item => item.IsAutomaticApproval, "request.rule.approval", [new SelectOption("False", texts["request.rule.manual"]), new SelectOption("True", texts["request.rule.automatic"])]);
                form.Number(item => item.RequestLimit, "request.rule.limit");
                form.Number(item => item.PeriodDays, "request.rule.period", affix: FieldAffix.Unit("unit.days"));
                if (_profiles.Count > 0) form.Choices(item => item.AcquisitionProfileIds, "request.rule.profiles", _profiles.Select(profile => new SelectOption(profile.Id.ToString(CultureInfo.InvariantCulture), profile.Name)));
                form.Submit("setup.next");
            }, "/Setup/Requests", antiforgery.GetAndStoreTokens(HttpContext).RequestToken, input, FormError)).Class("ui-section setup-requests"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? name, int? requestLimit, [BindRequired] int periodDays, [BindRequired] bool isAutomaticApproval, string[]? allowedMediaKeys, long[]? acquisitionProfileIds,
            bool isDefault, long? version)
        {
            await LoadDataAsync();
            _input = new SaveRequestRuleDataV1(name ?? "", _default?.Description, requestLimit, periodDays, isAutomaticApproval, allowedMediaKeys ?? [], acquisitionProfileIds ?? [], isDefault, version);
            await update.ExecuteAsync<UpdateResult>(new RequestRuleParametersV1(_default?.Id ?? 0), _input, HttpContext.RequestAborted);

            // The step is done; the flow goes on with the next one that applies.
            var next = SetupWizard.After(await readProgress.ExecuteAsync<SetupProgressV1>(new SetupProgressParametersV1(), HttpContext.RequestAborted), texts, "Requests");
            if (next?.Step is { } step) await progress.ExecuteAsync<UpdateResult>(new SetupProgressParametersV1(), new UpdateSetupProgressDataV1(step), HttpContext.RequestAborted);
            return LocalRedirect(next?.Path ?? "/Setup/Progress");
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _default = (await rules.ExecuteAsync<BaseDataList<RequestRuleDetailsV1>>(new ReadRequestRulesParametersV1(), ct)).FirstOrDefault(rule => rule.IsDefault);
            _profiles = texts.Can(null, "Acquisition") ? await profiles.ExecuteAsync<BaseDataList<AcquisitionProfileDetailsV1>>(new ReadAcquisitionProfilesParametersV1(), ct) : [];
            _media = await media.ExecuteAsync<BaseDataList<InstanceMediaDetailsV1>>(new ReadInstanceMediaParametersV1(), ct);
        }
    }
}
