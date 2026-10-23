using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Provider;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Setup.Providers
{
    // The providers step: every provider is a card with its switch and its key right in it, saved together by Continue; the media servers connect from their own cards.
    public sealed class ProvidersModel(ReadProvidersV1 providers, SaveSetupProvidersV1 save, ReadSetupProgressV1 readProgress, ReadPlexServerV1 readPlex, SavePlexServerV1 savePlex,
        ReadMediaServerV1 readServer, SaveMediaServerV1 saveServer, FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        // Providers that take a key; IGDB and AniList add the application id the key belongs to.
        private static readonly string[] KeyProviders = ["tmdb", "tvdb", "igdb", "anilist", "hardcover", "acoustid"];
        private static readonly string[] Pairs = ["igdb", "anilist"];
        private static readonly string[] Order = ["tmdb", "tvdb", "anilist", "openlibrary", "musicbrainz", "igdb", "hardcover", "acoustid"];

        private IReadOnlyList<ProviderDetailsV1> _providers = [];
        private SetupProgressV1? _progress;
        private PlexServerDetailsV1? _plex;
        private readonly Dictionary<MediaServerType, MediaServerDetailsV1> _servers = [];
        private SaveSetupProvidersDataV1? _input;
        private string? _failedServer;
        private SavePlexServerDataV1? _plexInput;
        private SaveMediaServerDataV1? _serverInput;

        protected override void Define(Page page)
        {
            page.Title("setup.step.Providers");
            page.Asset("Framework/Components/PasswordReveal.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            if (_progress is not { } progress) return;

            // The media servers, each connecting with its address and key.
            var servers = new List<IUiComponent> { PlexCard(csrf) };
            if (texts.Can(null, "ProgressSync", "Library")) servers.AddRange(_servers.Keys.Select(type => ServerCard(type, csrf)));
            page.Content.Add(new CardGrid([.. servers]).Class("card-grid setup-servers"));

            // AniList's application belongs to the profiles' sign-in; the catalog itself needs no key, so its fields exist only with the AniList module.
            var anilist = texts.Can(null, "AniList");
            var shown = _providers.Where(provider => Order.Contains(provider.Key)).OrderBy(provider => Array.IndexOf(Order, provider.Key)).ToList();
            var input = _input ?? new SaveSetupProvidersDataV1(progress.Version, new BaseDataList<SetupProviderInputV1>(shown.Select(provider => new SetupProviderInputV1(provider.Key, provider.IsEnabled, null, null))));
            var form = new Form<SaveSetupProvidersDataV1>(fields =>
            {
                fields.Hidden(item => item.Version);
                fields.Wrap(cards => new CardGrid([.. cards]).Class("card-grid setup-providers"), grid =>
                {
                    foreach (var provider in shown)
                    {
                        grid.Wrap(parts => ProviderCard(provider, parts), inside => inside.Rows(item => item.Providers, (row, item) =>
                        {
                            row.Hidden(data => data.Key);
                            row.Switch(data => data.IsEnabled, "admin.providers.state", control => new Div(control).Class("feature-card__row"));
                            var keyed = provider.Key != "anilist" || anilist;
                            if (Pairs.Contains(provider.Key) && keyed) row.Text(data => data.ClientId, ClientIdText(provider.Key));
                            if (KeyProviders.Contains(provider.Key) && keyed)
                            {
                                row.Password(data => data.Secret, Pairs.Contains(provider.Key) ? ClientSecretText(provider.Key) : "admin.providers.secret", "new-password",
                                    new FieldHelp(Placeholder: provider.HasSecret ? "setup.providers.keySaved" : null, Trailing: new PasswordReveal(assets)));
                            }
                        }, item => item.Key == provider.Key));
                    }
                });
                fields.Submit("setup.next");
            }, "/Setup/Providers", csrf, input, _failedServer is null ? FormError : null);
            page.Content.Add(form);
        }

        // A provider as a card: what it serves, whether a key is stored, and the fields of the form in its body.
        private IUiComponent ProviderCard(ProviderDetailsV1 provider, IReadOnlyList<IUiComponent> parts)
        {
            var needsKey = KeyProviders.Contains(provider.Key) && (provider.Key != "anilist" || texts.Can(null, "AniList"));
            var chip = !needsKey ? null : provider.HasSecret ? new StateChip(new Text("providers.key.saved"), UiTone.Success, new Icon("check", assets, 14)) : new StateChip(new Text("providers.key.missing"), UiTone.Warning, new Icon("alert", assets, 14));
            var name = texts.Has("provider.name." + provider.Key) ? texts["provider.name." + provider.Key] : provider.Key;
            var line = texts.Has("provider.hint." + provider.Key) ? texts["provider.hint." + provider.Key] : null;
            return new FeatureCard(new IconTile(new Icon(provider.Key == "anilist" ? "anilist" : "globe", assets, 18), UiTone.Accent), name, line, chip, parts, []);
        }

        // IGDB asks for the Twitch application's id and secret, AniList's pair is the one the profiles sign in with.
        private static string ClientIdText(string key) => key == "anilist" ? "admin.providers.anilistClientId" : "admin.providers.clientId";

        private static string ClientSecretText(string key) => key == "anilist" ? "admin.providers.anilistClientSecret" : "admin.providers.clientSecret";

        private IUiComponent PlexCard(string? csrf)
        {
            var connected = _plex?.ExternalId is not null;
            var chip = new StateChip(new Text(connected ? "providers.state.connected" : "providers.state.setup"), connected ? UiTone.Success : UiTone.Neutral, new Icon(connected ? "check" : "link", assets, 14));
            var input = _plexInput ?? new SavePlexServerDataV1(_plex?.BaseUrl ?? "", null);
            var form = new Form<SavePlexServerDataV1>(fields =>
            {
                fields.Text(item => item.BaseUrl, "admin.plex.baseUrl");
                fields.Password(item => item.Token, "admin.plex.token", "new-password", new FieldHelp(Trailing: new PasswordReveal(assets)));
                fields.Submit(new Button(new Text(connected ? "setup.providers.reconnect" : "admin.plex.connect")).Soft());
            }, "/Setup/Providers?handler=Plex", csrf, input, _failedServer == "plex" ? FormError : null);
            return new FeatureCard(new IconTile(new Icon("plex", assets, 18), UiTone.Accent), texts["provider.name.plex"], texts.Has("provider.hint.plex") ? texts["provider.hint.plex"] : null, chip, [form], []);
        }

        private IUiComponent ServerCard(MediaServerType type, string? csrf)
        {
            var server = _servers[type];
            var connected = server.ExternalId is not null;
            var key = type.ProviderKey();
            var chip = new StateChip(new Text(connected ? "providers.state.connected" : "providers.state.setup"), connected ? UiTone.Success : UiTone.Neutral, new Icon(connected ? "check" : "link", assets, 14));
            var input = _failedServer == key && _serverInput is not null ? _serverInput : new SaveMediaServerDataV1(server.BaseUrl ?? "", null);
            var form = new Form<SaveMediaServerDataV1>(fields =>
            {
                fields.Text(item => item.BaseUrl, "admin.mediaServer.baseUrl", help: new FieldHelp(Placeholder: "hint.mediaServer.baseUrl.placeholder"));
                fields.Password(item => item.ApiKey, "admin.mediaServer.apiKey", "new-password", new FieldHelp(Trailing: new PasswordReveal(assets)));
                fields.Submit(new Button(new Text(connected ? "setup.providers.reconnect" : "admin.plex.connect")).Soft());
            }, "/Setup/Providers?handler=MediaServer&mediaServerTypeId=" + type, csrf, input, _failedServer == key ? FormError : null);
            return new FeatureCard(new IconTile(new Icon("server", assets, 18), UiTone.Accent), texts["provider.name." + key], texts.Has("provider.hint." + key) ? texts["provider.hint." + key] : null, chip, [form], []);
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        // Every provider is saved with its switch; a key left empty stays as it is, so saving never removes one.
        public async Task<IActionResult> OnPostAsync(long version, List<ProviderRow>? providers)
        {
            _input = new SaveSetupProvidersDataV1(version, new BaseDataList<SetupProviderInputV1>((providers ?? []).Select(item => new SetupProviderInputV1(item.Key ?? "", item.IsEnabled,
                string.IsNullOrWhiteSpace(item.Secret) ? null : item.Secret, string.IsNullOrWhiteSpace(item.ClientId) ? null : item.ClientId))));
            await save.ExecuteAsync<UpdateResult>(new SetupProgressParametersV1(), _input, HttpContext.RequestAborted);
            return LocalRedirect("/Setup/Progress");
        }

        public async Task<IActionResult> OnPostPlexAsync(string? baseUrl, string? token)
        {
            _failedServer = "plex";
            _plexInput = new SavePlexServerDataV1(baseUrl ?? "", string.IsNullOrWhiteSpace(token) ? null : token);
            await savePlex.ExecuteAsync<PlexServerTestV1>(new PlexServerParametersV1(), _plexInput, HttpContext.RequestAborted);
            return LocalRedirect("/Setup/Providers");
        }

        public async Task<IActionResult> OnPostMediaServerAsync(MediaServerType mediaServerTypeId, string? baseUrl, string? apiKey)
        {
            _failedServer = mediaServerTypeId.ProviderKey();
            _serverInput = new SaveMediaServerDataV1(baseUrl ?? "", string.IsNullOrWhiteSpace(apiKey) ? null : apiKey);
            await saveServer.ExecuteAsync<MediaServerTestV1>(new MediaServerParametersV1(mediaServerTypeId), _serverInput, HttpContext.RequestAborted);
            return LocalRedirect("/Setup/Providers");
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _progress = await readProgress.ExecuteAsync<SetupProgressV1>(new SetupProgressParametersV1(), ct);
            _providers = (await providers.ExecuteAsync<PageResult<ProviderDetailsV1>>(new ReadProvidersParametersV1(1, 100), ct)).Items;
            _plex = await readPlex.ExecuteAsync<PlexServerDetailsV1>(new PlexServerParametersV1(), ct);
            if (!texts.Can(null, "ProgressSync", "Library")) return;
            foreach (var type in Enum.GetValues<MediaServerType>()) _servers[type] = await readServer.ExecuteAsync<MediaServerDetailsV1>(new MediaServerParametersV1(type), ct);
        }

        public sealed record ProviderRow(string? Key, bool IsEnabled, string? Secret, string? ClientId);
    }
}
