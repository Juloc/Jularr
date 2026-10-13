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
    public sealed class ProvidersModel(ReadProvidersV1 providers, UpdateProviderV1 update, SaveProviderSecretV1 saveSecret, ReadPlexServerV1 readPlex, SavePlexServerV1 savePlex,
        SyncPlexServerV1 syncPlex, DisconnectPlexServerV1 disconnectPlex, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private PageResult<ProviderDetailsV1>? _providers;
        private PlexServerDetailsV1? _plex;
        private SavePlexServerDataV1? _plexInput;
        private UpdateProviderDataV1? _input;
        private long? _providerId;

        protected override void Define(Page page)
        {
            page.Title("admin.providers.title");
            page.Navigation("nav.admin");
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

                // Metadata providers need a key; it is never shown again. IGDB adds the Twitch application id.
                if (provider.Key is "tmdb" or "igdb")
                {
                    page.Content.Add(new P(new Text(provider.HasSecret ? "admin.providers.secretSet" : "admin.providers.secretMissing")));
                    page.Content.Add(new Form<SaveProviderSecretDataV1>(form =>
                    {
                        if (provider.Key == "igdb")
                        {
                            form.Text(item => item.ClientId, "admin.providers.clientId");
                        }

                        form.Password(item => item.Secret, provider.Key == "igdb" ? "admin.providers.clientSecret" : "admin.providers.secret", "new-password");
                        form.Submit("common.save");
                    }, "/Admin/Providers?providerId=" + provider.Id.ToString(CultureInfo.InvariantCulture) + "&handler=Secret", antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                        new SaveProviderSecretDataV1(null)));
                }

                // The owner's Plex server: availability, refresh after import and links.
                if (provider.Key == "plex" && _plex is { } plex)
                {
                    DefinePlex(page, plex);
                }
            }

            if (_providers?.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", "/Admin/Providers?pageNumber=" + (_providers.Page - 1)));
            if (_providers is { } && _providers.Items.Count == _providers.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", "/Admin/Providers?pageNumber=" + (_providers.Page + 1)));
            if (Error is not null && _providerId is null) page.Content.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
        }

        private void DefinePlex(Page page, PlexServerDetailsV1 plex)
        {
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            page.Content.Add(new P(plex.ExternalId is null
                ? new Text("admin.plex.notConnected")
                : new ValueText(string.Format(CultureInfo.InvariantCulture, texts["admin.plex.connected"], plex.LinkedCount,
                    plex.SyncedAt?.ToString("u", CultureInfo.InvariantCulture) ?? texts["admin.plex.notSynced"]))));
            if (TempData["PlexResult"] is string result) page.Content.Add(new P(new ValueText(result)).Attribute("role", "status"));
            page.Content.Add(new Form<SavePlexServerDataV1>(form =>
            {
                form.Text(item => item.BaseUrl, "admin.plex.baseUrl");
                form.Password(item => item.Token, "admin.plex.token", "new-password");
                form.Submit(plex.ExternalId is null ? "admin.plex.connect" : "common.save");
            }, "/Admin/Providers?handler=Plex", csrf, _plexInput ?? new SavePlexServerDataV1(plex.BaseUrl ?? "", null), _plexInput is null ? null : Error));
            if (plex.ExternalId is not null)
            {
                page.Content.Add(new Form<PlexServerParametersV1>(form => form.Submit("admin.plex.sync"), "/Admin/Providers?handler=PlexSync", csrf, new PlexServerParametersV1()));
                page.Content.Add(new Form<PlexServerParametersV1>(form => form.Submit("admin.plex.disconnect"), "/Admin/Providers?handler=PlexDisconnect", csrf, new PlexServerParametersV1()));
            }
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
        public async Task<IActionResult> OnPostSecretAsync(long providerId, string? secret, string? clientId)
        {
            _providerId = providerId;
            await saveSecret.ExecuteAsync<UpdateResult>(new UpdateProviderParametersV1(providerId), new SaveProviderSecretDataV1(string.IsNullOrWhiteSpace(secret) ? null : secret,
                string.IsNullOrWhiteSpace(clientId) ? null : clientId), HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Providers/Providers");
        }

        public async Task<IActionResult> OnPostPlexAsync(string? baseUrl, string? token)
        {
            _plexInput = new SavePlexServerDataV1(baseUrl ?? "", string.IsNullOrWhiteSpace(token) ? null : token);
            var test = await savePlex.ExecuteAsync<PlexServerTestV1>(new PlexServerParametersV1(), _plexInput, HttpContext.RequestAborted);
            TempData["PlexResult"] = string.Format(CultureInfo.InvariantCulture, texts["admin.plex.tested"], test.Version ?? "?", string.Join(", ", test.Sections));
            return RedirectToPage("/Admin/Providers/Providers");
        }

        public async Task<IActionResult> OnPostPlexSyncAsync()
        {
            var sync = await syncPlex.ExecuteAsync<PlexSyncResultV1>(new PlexServerParametersV1(), default, HttpContext.RequestAborted);
            TempData["PlexResult"] = string.Format(CultureInfo.InvariantCulture, texts["admin.plex.synced"], sync.LinkedCount, sync.CreatedCount);
            return RedirectToPage("/Admin/Providers/Providers");
        }

        public async Task<IActionResult> OnPostPlexDisconnectAsync()
        {
            await disconnectPlex.ExecuteAsync<UpdateResult>(new PlexServerParametersV1(), default, HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Providers/Providers");
        }

        protected override async Task LoadDataAsync()
        {
            _providers = await providers.ExecuteAsync<PageResult<ProviderDetailsV1>>(new ReadProvidersParametersV1(PageNumber), HttpContext.RequestAborted);
            _plex = await readPlex.ExecuteAsync<PlexServerDetailsV1>(new PlexServerParametersV1(), HttpContext.RequestAborted);
        }
    }
}
