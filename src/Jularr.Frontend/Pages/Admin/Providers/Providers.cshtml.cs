using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Providers
{
    public sealed class ProvidersModel(ReadProvidersV1 providers, UpdateProviderV1 update, SaveProviderSecretV1 saveSecret, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private PageResult<ProviderDetailsV1>? _providers;
        private UpdateProviderDataV1? _input;
        private long? _providerId;

        protected override void Define(Page page)
        {
            page.Title("admin.providers.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Providers/Providers.entry.scss");
            page.Content.Add(new P(new Text("admin.providers.scope")));

            var options = new[] { new SelectOption("True", texts["admin.providers.enabled"]), new SelectOption("False", texts["admin.providers.disabled"]) };

            // Render only real registry rows.
            foreach (var provider in _providers?.Items ?? [])
            {
                page.Content.Add(new P(new ValueText(provider.Key)));
                page.Content.Add(new Form<UpdateProviderDataV1>(form =>
                {
                    form.Hidden(item => item.ExpectedIsEnabled);
                    form.Select(item => item.IsEnabled, "admin.providers.state", options);
                    form.Submit("common.save");
                }, "/Admin/Providers?providerId=" + provider.Id.ToString(CultureInfo.InvariantCulture), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    _providerId == provider.Id ? _input : new UpdateProviderDataV1(provider.IsEnabled, provider.IsEnabled), _providerId == provider.Id ? Error : null));

                // Metadata providers need a key; it is never shown again.
                if (provider.Key == "tmdb")
                {
                    page.Content.Add(new P(new Text(provider.HasSecret ? "admin.providers.secretSet" : "admin.providers.secretMissing")));
                    page.Content.Add(new Form<SaveProviderSecretDataV1>(form =>
                    {
                        form.Password(item => item.Secret, "admin.providers.secret", "new-password");
                        form.Submit("common.save");
                    }, "/Admin/Providers?providerId=" + provider.Id.ToString(CultureInfo.InvariantCulture) + "&handler=Secret", antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                        new SaveProviderSecretDataV1(null)));
                }
            }

            if (_providers?.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", "/Admin/Providers?pageNumber=" + (_providers.Page - 1)));
            if (_providers is { } && _providers.Items.Count == _providers.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", "/Admin/Providers?pageNumber=" + (_providers.Page + 1)));
            if (Error is not null && _providerId is null) page.Content.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
        }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(long providerId, [BindRequired] bool isEnabled, [BindRequired] bool expectedIsEnabled)
        {
            _providerId = providerId;
            _input = new UpdateProviderDataV1(isEnabled, expectedIsEnabled);
            await update.ExecuteAsync<UpdateResult>(new UpdateProviderParametersV1(providerId), _input, HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Providers/Providers");
        }
        public async Task<IActionResult> OnPostSecretAsync(long providerId, string? secret)
        {
            _providerId = providerId;
            await saveSecret.ExecuteAsync<UpdateResult>(new UpdateProviderParametersV1(providerId), new SaveProviderSecretDataV1(string.IsNullOrWhiteSpace(secret) ? null : secret),
                HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Providers/Providers");
        }

        protected override async Task LoadDataAsync()
        {
            _providers = await providers.ExecuteAsync<PageResult<ProviderDetailsV1>>(new ReadProvidersParametersV1(PageNumber), HttpContext.RequestAborted);
        }
    }
}
