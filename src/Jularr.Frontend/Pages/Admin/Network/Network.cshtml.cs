using System.Globalization;
using Jularr.Backend.Data.Admin.Network.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Network.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Network
{
    // The VPN profiles as cards: what each profile is, what its tunnel measured while it runs, and Check, Stop and Delete; a new profile is a dialog.
    public sealed class NetworkModel(ReadVpnProfilesV1 read, ImportVpnProfileV1 import, DeleteVpnProfileV1 delete, CheckVpnProfileV1 check, StopVpnProfileV1 stop,
        FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private const string Address = "/Admin/Network";
        private const string AddDialog = "vpn-add";
        private IReadOnlyList<VpnProfileV1> _profiles = [];
        private string? _name;
        private string? _configuration;
        private bool _importing;

        protected override void Define(Page page)
        {
            page.Title("admin.vpn.title");
            page.Navigation("nav.admin");
            page.Asset("Framework/Components/Modal.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            page.Asset("Framework/Components/FileInput.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            page.Content.Add(new CardGrid([.. _profiles.Select(profile => Card(profile, csrf))]));
            foreach (var profile in _profiles)
            {
                page.Content.Add(Modal.Confirm(assets, "vpn-delete-" + Id(profile.Id), "admin.vpn.deleteTitle", "admin.vpn.delete",
                    Address + "?handler=Delete&vpnProfileId=" + Id(profile.Id) + "&version=" + Id(profile.Version), csrf, new VpnProfileParametersV1(profile.Id)));
            }

            // A profile is a wg0.conf, uploaded or pasted; its keys never come back.
            page.Content.Add(new Modal(assets, AddDialog, "admin.vpn.add", [new Form<ImportVpnProfileDataV1>(form =>
            {
                form.Text(item => item.Name, "admin.vpn.name");
                form.File(item => item.File, "admin.vpn.file", assets);
                form.Multiline(item => item.Configuration, "admin.vpn.configuration");
                form.Cancel();
                form.Submit("admin.vpn.import");
            }, Address + "?handler=Import", csrf, new ImportVpnProfileDataV1(_name ?? "", null, _configuration), _importing ? FormError : null)], open: _importing && FormError is not null));
            if (FormError is not null && !_importing) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));

            page.Content.Add(new ActionDock(assets, "admin.vpn.actions",
                [Modal.Opener(new Button(new Icon("plus", assets, 16), new Span(new Text("admin.vpn.add")).Class("action-dock__text")).Primary(), AddDialog)]));
        }

        // One profile: its endpoint in the head with the tunnel's state, the measured facts under it, the actions at its foot.
        private IUiComponent Card(VpnProfileV1 profile, string? csrf)
        {
            var tunnel = profile.Tunnel;
            var (key, tone) = tunnel.Phase switch
            {
                VpnTunnelPhase.Connected => ("admin.vpn.connected", UiTone.Success),
                VpnTunnelPhase.Connecting => ("admin.vpn.connecting", UiTone.Warning),
                VpnTunnelPhase.Failed => ("admin.vpn.failed", UiTone.Danger),
                _ => ("admin.vpn.stopped", UiTone.Neutral)
            };

            var facts = new List<(string, IUiComponent)>
            {
                ("admin.vpn.addresses", new ValueText(string.Join(", ", profile.TunnelAddresses))),
                ("admin.vpn.dns", new Span(new ValueText(string.Join(", ", profile.DnsServers) + " · "), new Text("admin.vpn.dnsThroughTunnel"))),
                ("admin.vpn.ipv6", new Text(profile.HasIpv6Route ? "admin.vpn.ipv6Tunnel" : "admin.vpn.ipv6Blocked")),
                ("admin.vpn.killSwitch", new Text("admin.vpn.killSwitchBound"))
            };
            if (tunnel.Phase != VpnTunnelPhase.Stopped)
            {
                facts.Add(("admin.vpn.exit", tunnel.ExitAddress is { } exit
                    ? new Span(new ValueText(exit + (tunnel.ExitCountryCode is { } country ? " · " + country : "")), new ValueText(" · "),
                        new Text(tunnel.ExitDiffersFromHost ? "admin.vpn.exitHidden" : "admin.vpn.exitNotCompared"))
                    : Dim()));
                facts.Add(("admin.vpn.exitCheckedAt", Time(tunnel.ExitCheckedAt)));
                facts.Add(("admin.vpn.alive", Time(tunnel.AliveAt)));
                facts.Add(("admin.vpn.handshake", Time(tunnel.HandshakeAt)));
                facts.Add(("admin.vpn.traffic", new ValueText(ByteSize.Format(tunnel.ReceivedBytes) + " ↓ · " + ByteSize.Format(tunnel.SentBytes) + " ↑")));
                facts.Add(("admin.vpn.leases", new ValueText(tunnel.Leases.ToString(CultureInfo.CurrentCulture))));
            }

            var body = new List<IUiComponent> { new FactList(facts) };
            if (tunnel.ErrorKey is { } error) body.Add(new P(new Text("errors." + error)).Class("feature-card__note"));

            var start = new List<IUiComponent> { Post("Check", profile.Id, csrf, new Button(new Icon("pulse", assets, 14), new Text("admin.vpn.check")).Compact().Soft().Submit()) };
            if (tunnel.Phase != VpnTunnelPhase.Stopped)
                start.Add(Post("Stop", profile.Id, csrf, new Button(new Icon("power", assets, 14), new Text("admin.vpn.stop")).Compact().Soft().Submit()));
            start.Add(Modal.Opener(new Button(new Icon("trash", assets, 14), new Text("admin.vpn.delete")).Compact().Warn(), "vpn-delete-" + Id(profile.Id)));
            return new FeatureCard(new IconTile(new Icon("shield-check", assets, 18), tone == UiTone.Neutral ? UiTone.Accent : tone), profile.Name,
                profile.EndpointHost + ":" + profile.EndpointPort.ToString(CultureInfo.InvariantCulture), new StateChip(new Text(key), tone), body, start, null,
                "vpn-" + Id(profile.Id)).Wide();
        }

        private static IUiComponent Post(string handler, long id, string? csrf, IUiComponent button) =>
            new PostForm(Address + "?handler=" + handler + "&vpnProfileId=" + Id(id), csrf, [], button);

        private static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static IUiComponent Dim() => new Span(new ValueText("–")).Class("list-value list-value--dim");

        private static IUiComponent Time(DateTime? value) => value is { } time ? new Span(new LocalTime(time)).Class("list-value") : Dim();

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostImportAsync(string? name, IFormFile? file, string? configuration)
        {
            _importing = true;
            _name = name;
            _configuration = configuration;
            await import.ExecuteAsync<CreateVpnProfileResultV1>(new ImportVpnProfileDataV1(name ?? "",
                file is { Length: > 0 } ? new UploadedFile(file.FileName, file.Length, file.OpenReadStream) : null, configuration), HttpContext.RequestAborted);
            return LocalRedirect(Address);
        }

        public async Task<IActionResult> OnPostCheckAsync(long vpnProfileId)
        {
            var tunnel = await check.ExecuteAsync<VpnTunnelV1>(new VpnProfileParametersV1(vpnProfileId), default, HttpContext.RequestAborted);
            Hint(tunnel.Phase == VpnTunnelPhase.Connected ? "admin.vpn.checked" : "admin.vpn.checkFailed");
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostStopAsync(long vpnProfileId)
        {
            await stop.ExecuteAsync<UpdateResult>(new VpnProfileParametersV1(vpnProfileId), default, HttpContext.RequestAborted);
            Hint("admin.vpn.stopDone");
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostDeleteAsync(long vpnProfileId, long version)
        {
            await delete.ExecuteAsync<UpdateResult>(new DeleteVpnProfileParametersV1(vpnProfileId, version), default, HttpContext.RequestAborted);
            return LocalRedirect(Address);
        }

        protected override async Task LoadDataAsync()
        {
            _profiles = await read.ExecuteAsync<BaseDataList<VpnProfileV1>>(new ReadVpnProfilesParametersV1(), HttpContext.RequestAborted);
        }
    }
}
