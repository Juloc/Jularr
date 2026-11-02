using System.Globalization;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Modules.Live;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Acquisition
{
    // The Servers and External clients pages of the Downloads group (the owner's downloader mockup, panels 3 and 7): the heading with the add button, a row of kinds
    // (External clients), the list of connections and the opened one's settings beside what is known about it.
    public sealed partial class AcquisitionModel
    {
        // The kind External clients is narrowed to; none shows them all.
        [BindProperty(SupportsGet = true)]
        public AcquisitionConnectionType? Kind { get; set; }

        // How many connections of each kind the page has, for the row of kinds.
        private IReadOnlyList<AcquisitionConnectionDetailsV1> _allOfScope = [];

        private void DefineDownloaders(Page page, string? csrf, IReadOnlyList<AcquisitionConnectionDetailsV1> items)
        {
            // The heading and the add button: one kind adds directly, several are chosen in a dialog.
            var offered = Offered.ToList();
            var head = new List<IUiComponent> { new H1(new Text(Scope == "Servers" ? "downloads.servers.title" : "downloads.clients.title")), new Span().Class("downloaders-head__spacer") };
            if (offered.Count == 1)
            {
                head.Add(new A(Glyph("plus", 16), new Text(offered[0].LabelKey)).Class("button button--primary").Attribute("href", Address(type: offered[0].Type)));
            }
            else if (offered.Count > 1)
            {
                head.Add(Modal.Opener(new Button(Glyph("plus", 16), new Text("downloads.clients.add")).Primary(), "downloaders-add"));
                page.Content.Add(new Modal(assets, "downloaders-add", "downloads.clients.add", [new Div([.. offered.Select(addition =>
                    (IUiComponent)new A(Glyph("plus", 14), new Text("acquisition.connection.type." + addition.Type)).Class("button").Attribute("href", Address(type: addition.Type)))]).Class("downloaders-kinds")]));
            }

            page.Content.Add(new Div([.. head]).Class("downloaders-head"));

            // External clients: All and each kind there is, with its count.
            if (Scope == "Clients" && _allOfScope.Count > 0)
            {
                var tabs = new List<NavigationTab> { new("downloads.all", DownloadersAddress(null), Kind is null, Count: _allOfScope.Count) };
                tabs.AddRange(_allOfScope.GroupBy(item => item.AcquisitionConnectionTypeId).Select(group =>
                    new NavigationTab("acquisition.connection.type." + group.Key, DownloadersAddress(group.Key), Kind == group.Key, Count: group.Count())));
                page.Content.Add(new ListToolbar(new NavigationTabs(tabs, "downloads.clients.kinds", "filters")));
            }

            if (items.Count == 0)
            {
                page.Content.Add(new Div(new Div(new IconTile(Glyph(Scope == "Servers" ? "network" : "link", 22), UiTone.Neutral),
                    new P(new Text("acquisition.connection.none." + Scope)).Attribute("role", "status")).Class("list-empty")).Class("list"));
                return;
            }

            // The list: every connection with its state and what matters at a glance; a name opens it below.
            var opened = _selected ?? items[0];
            var columns = Scope == "Servers"
                ? new List<ListColumn> { new("downloads.server", ListRole.Main), new("downloads.state", ListRole.Status), new("downloads.connections"), new("downloads.speed"),
                    new("downloads.missingArticles", ListRole.Data, 2), new("acquisition.connection.priority", ListRole.Data, 1) }
                : new List<ListColumn> { new("downloads.name", ListRole.Main), new("downloads.kind", ListRole.Data, 2), new("downloads.state", ListRole.Status),
                    new("acquisition.connection.priority", ListRole.Data, 1) };
            var rows = items.Select(item =>
            {
                var cells = new List<IUiComponent>
                {
                    new ListIdentity(new IconTile(Glyph(Scope == "Servers" ? "network" : "download", 18), UiTone.Info),
                        new A(new Truncated(item.Name)).Class("list-identity__title").Attribute("href", Address(item.Id)), new ValueText(Where(item)))
                };
                if (Scope == "Servers")
                {
                    var figures = ServerFigures(item);
                    cells.AddRange([StateChipOf(item, State(item)), LiveServer(item, figures, "connections"), LiveServer(item, figures, "speed"), LiveServer(item, figures, "missing")]);
                }
                else
                {
                    cells.AddRange([new Span(new Text("acquisition.connection.type." + item.AcquisitionConnectionTypeId)).Class("list-value"), StateChipOf(item, State(item))]);
                }

                cells.Add(new Span(new ValueText(item.Priority.ToString(CultureInfo.CurrentCulture))).Class("list-value"));
                var row = new ListRow(cells);
                return item.Id == opened.Id ? row.Tinted(UiTone.Accent) : row;
            }).ToList();
            page.Content.Add(new ListPanel(Scope == "Servers" ? "downloads.servers.title" : "downloads.clients.title", columns, rows)
                .Modifier("downloaders-list downloaders-list--" + Scope.ToLowerInvariant()).Paged(primary: true));

            page.Content.Add(Detail(opened, csrf));
        }

        // The opened connection: its name, state and actions in the head, its settings on the left (saved by the pill), what is known about it on the right.
        private IUiComponent Detail(AcquisitionConnectionDetailsV1 item, string? csrf)
        {
            var actions = new List<IUiComponent>
            {
                Post("Test", item.Id, csrf, [], new Button(Glyph("pulse", 14), new Text("acquisition.connection.test")).Compact().Soft().Submit())
            };
            if (item.AcquisitionConnectionTypeId == AcquisitionConnectionType.Sabnzbd)
            {
                actions.Add(new A(Glyph("download", 14), new Text("console.open")).Class("button button--compact button--soft").Attribute("href", Address(item.Id) + "&Pane=downloads"));
            }

            // Every download client has its tab on the Downloads page; a SABnzbd or qBittorrent is a tab of its own.
            if (DownloadClientKinds.Of(item.AcquisitionConnectionTypeId) is { } kind)
            {
                actions.Add(new A(Glyph("download", 14), new Text("admin.menu.downloads")).Class("button button--compact button--soft")
                    .Attribute("href", "/Admin/Downloads?Client=" + kind + (kind is DownloadClientKind.Sabnzbd or DownloadClientKind.Qbittorrent ? "&ConnectionId=" + item.Id.ToString(CultureInfo.InvariantCulture) : "")));
            }

            actions.Add(new A(Glyph("trash", 14), new Text("acquisition.connection.delete")).Class("button button--compact button--warn").Attribute("href", Address(item.Id) + "&delete=true"));
            actions.Add(Post("Toggle", item.Id, csrf, [("isEnabled", item.IsEnabled ? "false" : "true")], new SwitchButton(item.IsEnabled, "acquisition.connection.active")));
            var head = new Div(new IconTile(Glyph(Scope == "Servers" ? "network" : "download", 18), UiTone.Info), new H2(new ValueText(item.Name)), StateChipOf(item, State(item)),
                new Span().Class("downloaders-head__spacer"), new Div([.. actions]).Class("downloaders-detail__actions")).Class("downloaders-detail__head");

            var known = new List<IUiComponent> { new H3(new Text(Scope == "Servers" ? "downloads.servers.live" : "downloads.clients.connection")), new FactList(Facts(item)) };
            if (Scope == "Clients")
            {
                known.Add(new H3(new Text("downloads.clients.functions")));
                known.Add(new Ul([.. Functions(item.AcquisitionConnectionTypeId).Select(key => (IUiComponent)new Li(Glyph("check", 14), new Text(key)))]).Class("downloaders-functions"));
            }

            return new Section(head, new Div(new Div(ConnectionForm(item.AcquisitionConnectionTypeId, csrf, item)).Class("downloaders-detail__form"),
                new Div([.. known]).Class("downloaders-detail__known")).Class("downloaders-detail__body")).Class("downloaders-detail").Attribute("id", "connection-" + item.Id.ToString(CultureInfo.InvariantCulture));
        }

        // What Jularr knows of the connection now: for a news server its last pass, for a client its address and the last test; the torrent engine its proven lock.
        private IReadOnlyList<(string, IUiComponent)> Facts(AcquisitionConnectionDetailsV1 item)
        {
            var facts = new List<(string, IUiComponent)> { ("downloads.state", StateChipOf(item, State(item))) };
            if (item.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet)
            {
                var figures = ServerFigures(item);
                facts.AddRange([("downloads.connections", LiveServer(item, figures, "connections")), ("downloads.speed", LiveServer(item, figures, "speed")),
                    ("downloads.missingArticles", LiveServer(item, figures, "missing"))]);
            }
            else if (item.BaseUrl is { } address)
            {
                facts.Add(("downloads.address", new ValueText(address)));
            }

            facts.Add(("acquisition.connection.lastTest", Time(item.CheckedAt)));
            if (item.AcquisitionConnectionTypeId == AcquisitionConnectionType.Torrent && _torrentEngine is { } engine)
            {
                facts.Add(("acquisition.torrent.lock", new Text(engine.IsLockVerified ? "acquisition.torrent.lockVerified" : "acquisition.torrent.lockFailed")));
                facts.Add(("acquisition.torrent.vpn", new Text(engine.HasVerifiedTunnel ? "acquisition.torrent.vpnVerified" : "acquisition.torrent.vpnNone")));
            }

            return facts;
        }

        // What Jularr can tell a client of this kind, as the Downloads page offers it.
        private static IEnumerable<string> Functions(AcquisitionConnectionType type)
        {
            if (DownloadClientKinds.Of(type) is not { } kind) yield break;
            if (DownloadClientKinds.AddsNzb(kind)) yield return "downloads.function.addNzb";
            if (DownloadClientKinds.AddsTorrent(kind)) yield return "downloads.function.addTorrent";
            yield return "downloads.function.pause";
            if (DownloadClientKinds.HasPriorities(kind)) yield return "downloads.function.priorities";
            yield return DownloadClientKinds.Reorders(kind) ? "downloads.function.reorder" : "downloads.function.moveToTop";
            if (DownloadClientKinds.LimitsSpeed(kind)) yield return "downloads.function.speedLimit";
        }

        // A news server's address, a client's web address.
        private static string Where(AcquisitionConnectionDetailsV1 item) => item.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet
            ? item.Host + (item.Port is { } port ? ":" + port.ToString(CultureInfo.InvariantCulture) : "") : item.BaseUrl ?? "";

        private IReadOnlyDictionary<string, string> ServerFigures(AcquisitionConnectionDetailsV1 item) =>
            LiveEntities.NewsServer(texts, _newsServers.FirstOrDefault(server => server.AcquisitionConnectionId == item.Id) ?? new NewsServerV1(item.Id, 0, 0, 0, 0, item.MaxConnections ?? 8)).Text;

        private static IUiComponent LiveServer(AcquisitionConnectionDetailsV1 item, IReadOnlyDictionary<string, string> figures, string name) =>
            new Span(new ValueText(figures.TryGetValue(name, out var text) ? text : "")).Class("list-value").Attribute("data-live", LiveKeys.NewsServer(item.Id)).Attribute("data-live-text", name);

        private string DownloadersAddress(AcquisitionConnectionType? kind) => "/Admin/Downloads/Clients" + (kind is { } value ? "?Kind=" + value : "");
    }
}
