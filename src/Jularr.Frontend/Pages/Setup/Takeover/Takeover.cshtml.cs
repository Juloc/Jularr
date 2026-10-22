using System.Globalization;
using System.Security.Cryptography;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Backend.Service.Admin.Storage.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Setup.Takeover
{
    // The takeover step: one card for each system with its address and key, a preview that changes nothing and then the takeover. The source is only ever read.
    public sealed class TakeoverModel(RunTakeoverV1 run, ReadStorageMountsV1 mounts, ReadStorageMountV1 mount, IAntiforgery antiforgery, UiText texts, FrontendAssets assets, IDataProtectionProvider protection, ReadProvidersV1 providers, ReadSetupProgressV1 readProgress, SaveSetupProvidersV1 saveProviders)
        : Admin.Takeover.TakeoverModel(run, mounts, mount, antiforgery, texts)
    {
        private static readonly TakeoverSourceType[] Sources = [TakeoverSourceType.Sonarr, TakeoverSourceType.Radarr, TakeoverSourceType.Readarr, TakeoverSourceType.Lidarr, TakeoverSourceType.Prowlarr];

        // What a source holds, and the icon of its card.
        private static readonly Dictionary<TakeoverSourceType, string> Icons = new()
        {
            [TakeoverSourceType.Sonarr] = "tv", [TakeoverSourceType.Radarr] = "film", [TakeoverSourceType.Readarr] = "book", [TakeoverSourceType.Lidarr] = "music", [TakeoverSourceType.Prowlarr] = "search"
        };

        // Outcomes that deserve a look before taking over: something is not taken, needs a key or has no clear match.
        private static readonly HashSet<string> Warnings = ["takeover_profile_unsupported", "takeover_format_skipped", "takeover_no_match", "takeover_no_id", "takeover_needs_key", "takeover_no_library",
            "takeover_not_monitored", "takeover_no_qualities", "takeover_next_run"];

        private ProviderDetailsV1? _tmdb;
        private TakeoverSourceType? _ran;
        private string? _seal;

        // The key waits sealed for ten minutes between the preview and the takeover, so it is typed once and never shown again.
        private ITimeLimitedDataProtector Sealer => protection.CreateProtector("Jularr.Setup.Takeover.Key").ToTimeLimitedDataProtector();

        protected override void Define(Page page)
        {
            page.Title("setup.step.Takeover");
            page.Asset("Framework/Components/PasswordReveal.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            var cards = new List<IUiComponent>();
            if (_tmdb is { HasSecret: false } tmdb) cards.Add(TmdbCard());
            cards.AddRange(Sources.Select(Card));
            page.Content.Add(new CardGrid([.. cards]).Class("card-grid setup-sources"));
        }

        // Titles are identified through TMDB, so a takeover of movies and series needs its key; it is typed here, in the card.
        private IUiComponent TmdbCard()
        {
            var form = new Form<SaveProviderSecretDataV1>(fields =>
            {
                fields.Password(item => item.Secret, "admin.providers.secret", "new-password", new FieldHelp(Trailing: new PasswordReveal(assets)));
                fields.Submit(new Button(new Text("common.save")).Soft());
            }, "/Setup/Takeover?handler=Tmdb", Csrf, new SaveProviderSecretDataV1(null), _ran is null ? FormError : null);
            return new FeatureCard(new IconTile(new Icon("globe", assets, 18), UiTone.Accent), Texts["provider.name.tmdb"], Texts["provider.hint.tmdb"],
                new StateChip(new Text("providers.key.missing"), UiTone.Warning, new Icon("alert", assets, 14)), [form], []).Class("feature-card setup-source");
        }

        private IUiComponent Card(TakeoverSourceType source)
        {
            var ran = _ran == source && Input is not null;
            var applied = ran && Input!.Apply && Items is not null;
            var (stateKey, tone, icon) = !ran || Items is null ? ("setup.takeover.state.idle", UiTone.Neutral, "link")
                : applied ? ("setup.takeover.state.done", UiTone.Success, "check") : ("setup.takeover.state.preview", UiTone.Info, "scan");
            var folders = ran ? Folders(Items) : [];
            var input = ran ? DisplayInput(Input, Items, folders) : new Jularr.Backend.Data.Admin.Acquisition.V1.RunTakeoverDataV1(source, "", "", false);
            var form = BuildForm(input, folders, "/Setup/Takeover?handler=Run", ran ? FormError : null, fields =>
            {
                fields.Hidden(item => item.TakeoverSourceTypeId);
                fields.Text(item => item.BaseUrl, "acquisition.connection.baseUrl", "url");
                fields.Password(item => item.ApiKey, "acquisition.connection.apiKey", "off", new FieldHelp(Trailing: new PasswordReveal(assets)));
                if (ran && _seal is not null) fields.Content(new Input().Attribute("type", "hidden").Attribute("name", "keySeal").Attribute("value", _seal));
            }, fields =>
            {
                fields.Submit(Choice(new Button(new Icon("scan", assets, 14), new Text("setup.takeover.preview")).Soft(), "false"));
                if (ran && Items is not null && !applied) fields.Submit(Choice(new Button(new Icon("download", assets, 14), new Text("setup.takeover.apply")).Primary(), "true"));
            });

            var body = new List<IUiComponent> { form };
            if (ran && Items is { } items) body.AddRange(Result(items, applied));
            return new FeatureCard(new IconTile(new Icon(Icons[source], assets, 18), UiTone.Accent), source.ToString(), Texts["setup.takeover.holds." + source],
                new StateChip(new Text(stateKey), tone, new Icon(icon, assets, 14)), body, []).Class(ran && Items is not null ? "feature-card setup-source setup-source--result" : "feature-card setup-source");
        }

        // The counters of what was found, what needs a look and every item found, by kind.
        private IEnumerable<IUiComponent> Result(IReadOnlyList<TakeoverItemV1> items, bool applied)
        {
            var shown = items.Where(item => item.LibraryRootId is null || item.OutcomeKey is "takeover_suggested" or "takeover_assigned").ToArray();
            var kinds = Enum.GetValues<TakeoverItemType>().Select(kind => (Kind: kind, Items: shown.Where(item => item.TakeoverItemTypeId == kind).ToArray())).Where(group => group.Items.Length > 0).ToArray();
            var tiles = kinds.Select(group => new KpiTile("setup.takeover.kind." + group.Kind, new ValueText(group.Items.Length.ToString("N0", CultureInfo.CurrentCulture)), new Icon(KindIcon(group.Kind), assets, 18), UiTone.Accent)).ToArray();
            if (tiles.Length == 0)
            {
                yield return new P(new Text("takeover.empty")).Class("setup-source__none");
                yield break;
            }

            yield return new KpiTiles(tiles).Strip();
            var warnings = shown.Where(item => Warnings.Contains(item.OutcomeKey)).ToArray();
            if (warnings.Length > 0)
            {
                yield return new Div(new Strong(new Text("setup.takeover.warnings"), new ValueText(" " + warnings.Length.ToString("N0", CultureInfo.CurrentCulture))).Class("setup-source__label"), ItemTable(warnings, true)).Class("setup-source__block");
            }

            // Everything found, one fold for each kind.
            foreach (var (kind, group) in kinds)
            {
                var added = group.Count(item => item.OutcomeKey is "takeover_added" or "takeover_wanted");
                var summary = new Summary(new Text("setup.takeover.kind." + kind), new ValueText(" " + group.Length.ToString("N0", CultureInfo.CurrentCulture)),
                    applied && added > 0 ? new Tag(new ValueText(string.Format(CultureInfo.CurrentCulture, Texts["setup.takeover.added"], added))).Accent() : new Span());
                yield return new Details(summary, ItemTable(group, false)).Class("setup-source__fold");
            }
        }

        // A button that posts whether this run takes over or only previews.
        private static Button Choice(Button button, string apply)
        {
            button.Submit().Attribute("name", "apply").Attribute("value", apply);
            return button;
        }

        private static string KindIcon(TakeoverItemType kind) => kind switch
        {
            TakeoverItemType.Indexer => "search",
            TakeoverItemType.Client => "download",
            TakeoverItemType.Movie => "film",
            TakeoverItemType.Series => "tv",
            TakeoverItemType.Album => "music",
            TakeoverItemType.Book => "book",
            TakeoverItemType.Profile => "sliders",
            TakeoverItemType.Format => "tag",
            _ => "folder"
        };

        private static IUiComponent ItemTable(IReadOnlyList<TakeoverItemV1> items, bool withKind)
        {
            var table = new Table<TakeoverItemV1>(items);
            if (withKind) table.Resource(item => item.TakeoverItemTypeId, "takeover.kind", kind => "takeover.kind." + kind);
            return table.Text(item => item.Name, "sources.name").Resource(item => item.OutcomeKey, "table.status", outcome => "takeover.outcome." + outcome);
        }

        public async Task<IActionResult> OnPostRunAsync([BindRequired] TakeoverSourceType takeoverSourceTypeId, string? baseUrl, string? apiKey, string? keySeal, [BindRequired] bool apply,
            string[]? rootFolders, List<ConnectionKeyRow>? connectionKeys, List<LibraryRow>? newLibraries, bool draft = false)
        {
            // A key typed for the preview comes back sealed; the takeover needs no second typing.
            _ran = takeoverSourceTypeId;
            if (string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(keySeal))
            {
                try
                {
                    apiKey = Sealer.Unprotect(keySeal);
                }
                catch (CryptographicException)
                {
                    apiKey = null;
                }
            }

            await RunAsync(takeoverSourceTypeId, baseUrl, apiKey, apply, rootFolders, connectionKeys, newLibraries, draft);
            if (!apply && !string.IsNullOrEmpty(apiKey)) _seal = Sealer.Protect(apiKey, TimeSpan.FromMinutes(10));
            return Page();
        }

        // The TMDB key, saved with the providers' own step service.
        public async Task<IActionResult> OnPostTmdbAsync(string? secret)
        {
            var progress = await readProgress.ExecuteAsync<SetupProgressV1>(new SetupProgressParametersV1(), HttpContext.RequestAborted);
            await saveProviders.ExecuteAsync<UpdateResult>(new SetupProgressParametersV1(), new SaveSetupProvidersDataV1(progress.Version, new BaseDataList<SetupProviderInputV1>([new SetupProviderInputV1("tmdb", true, secret, null)])), HttpContext.RequestAborted);
            return LocalRedirect("/Setup/Takeover");
        }

        // The Owner's choices stay while the page loads again after an error.
        protected override async Task LoadDataAsync()
        {
            await base.LoadDataAsync();
            _tmdb = (await providers.ExecuteAsync<PageResult<ProviderDetailsV1>>(new ReadProvidersParametersV1(1, 100), HttpContext.RequestAborted)).Items.FirstOrDefault(provider => provider.Key == "tmdb");
            if (Request.HasFormContentType && Request.Form.TryGetValue("takeoverSourceTypeId", out var value) && Enum.TryParse<TakeoverSourceType>(value, out var source)) _ran = source;
        }
    }
}
