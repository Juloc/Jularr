# Die Ergebnisse

Gemessen am 2026-10-21 und 2026-10-22 auf diesem Rechner (Windows, Docker Desktop, Spielplatz).
Anbieter der Messung: **ProtonVPN im Gratis-Tarif**, Server `NL-FREE#281` (Niederlande),
WireGuard, Konfiguration aus dem Proton-Dashboard.

## Der Tunnel

| Messung | Ergebnis |
| --- | --- |
| Adresse im Tunnel | **93.88.156.47**, Nootdorp (Südholland), **AS199218 Proton AG** |
| Adresse des Hosts | 149.172.64.180, Bietigheim-Bissingen, Vodafone BW – der Tunnel verdeckt sie |
| DNS | der Nameserver des Anbieters im Tunnel (10.2.0.1), keine Anfrage geht daneben vorbei |
| IPv6 | **kein IPv6** – der Gratis-Tarif gibt keines; der Client bleibt sonst an der IPv6-Adresse eines Trackers hängen |
| Tunnel nach Abbruch | der Container läuft weiter, die Firewall bleibt zu: **keine Verbindung, kein Download** |
| Torrent-Client im Container | `qBittorrent` v5.3.0, WebUI-API antwortet |
| Torrent im Tunnel | **keine Peers**: der Tracker antwortet, der Client meldet aber `timed out`; nach 3 Minuten `availability` 0, 0 Byte |
| Torrent ohne Tunnel | 4–6 MB/s (direkt), die Datei landet im gemounteten Ordner |

**Der Kill-Switch hält.** Mit gestopptem Tunnel scheitern die Anfragen der Probe – sie rutschen
nicht unverschlüsselt hinaus. Im Tunnel-Netzraum gemessen, nicht von außen.

**Der Gratis-Tarif von Proton trägt keine Torrents.** Proton führt „P2P-Unterstützung" selbst
als Premium-Funktion; die Messung bestätigt es: der Tracker antwortet, aber es kommen keine
Peers. Für **gesperrte Direktquellen** (die Light-Novel-Seiten) reicht der Gratis-Tarif, weil
eine EPUB-Datei wenige Megabyte kostet.

## Was beim Messen auffiel und im Lab steht

1. **Die MTU bestimmt die Brauchbarkeit.** Mit der ermittelten MTU 1320 kommen kleine Antworten
   an, große TLS-Handshakes nicht; die Gesundheitsprüfung des Tunnels schlägt fehl und der
   Tunnel wird ständig neu aufgebaut. Mit 1320 schlugen die Handshakes fehl, mit 1280 laufen sie –
   `VPN_MTU` in der `compose.yaml`.
2. **IPv6 im Container aus.** Ohne IPv6 versucht der Client die IPv6-Adresse des Trackers und
   wartet auf eine Antwort, die nie kommt. `sysctls` in `compose.vpn.yaml`.
3. **Der Name server im Tunnel** (`DNS_SERVER=off` mit `DNS_UPSTREAM_PLAIN_ADDRESSES=10.2.0.1:53`).
   Mit dem eigenen DoT-Server von Gluetun (Port 853) blockiert Proton die Abfrage, und die
   Gesundheitsprüfung hält den Tunnel nicht.
4. **Der Steuerkanal ist privat** (Gluetun ab 3.39.1): `HTTP_CONTROL_SERVER_AUTH_DEFAULT_ROLE`
   mit dem Schlüssel, und die Routen heißen `GET /v1/publicip/ip` und `PUT /v1/vpn/status`.
5. **Die WireGuard-Datei gehört nach `/gluetun/wireguard/wg0.conf`** – nicht nach
   `/gluetun/custom.conf` und nicht als `VPN_CUSTOM_CONFIG_PATH` (das ist der Weg für OpenVPN).
6. **Der Torrent-Client vergibt bei jedem Start ein neues Passwort**; `sync-qbittorrent-password.ps1`
   übernimmt es in die `.env` des Labs, sonst schlägt die Anmeldung der Messung fehl.
7. **Die Messung läuft im Netzraum des Tunnels** (`network_mode: service:vpn`). Nur von dort aus
   ist der Kill-Switch beweisbar; von außen gemessen sähe man nur die Adresse wechseln.

## Der Ablauf

```
# 1) Die Konfiguration des Anbieters bereitlegen (Datei aus dem Dashboard des Anbieters)
powershell -ExecutionPolicy Bypass -File lab/torrent-vpn/prepare-config.ps1 -Config <datei>
# oder bei Proton: nur den einen Wert aus dem Dashboard
powershell -ExecutionPolicy Bypass -File lab/torrent-vpn/prepare-proton.ps1 -Key <schluessel>

# 2) Starten und messen
docker compose -f lab/torrent-vpn/compose.yaml -f lab/torrent-vpn/compose.vpn.yaml up -d
docker compose -f lab/torrent-vpn/compose.yaml -f lab/torrent-vpn/compose.vpn.yaml --profile probe run --rm probe
```

## Was das für Jularr heißt

1. **Der Torrent-Client läuft im Container** – die Automatisierung ist machbar. Jularr spricht ihn
   über die WebUI-API an (eigener Adapter, austauschbar ✔).
2. **Der VPN gehört in den Container, nicht in Jularr** – so bleibt Jularr unabhängig vom Anbieter ✔.
   Die Regel „ohne VPN kein Download" ist eine Eigenschaft des Containers (Kill-Switch) ✔, Jularr
   merkt nur „Quelle nicht erreichbar" ✔.
3. **Für Torrents braucht es einen Anbieter, der P2P erlaubt** – der Gratis-Tarif von Proton nicht,
   Premium (derzeit 1,11 €/Monat) schon, oder ein eigener Server. Das ist die offene Entscheidung.
4. **IPv6 gehört abgeschaltet oder mitgenommen** – sonst läuft ein Teil des Verkehrs am VPN vorbei,
   und genau daran würde der Kill-Switch scheitern.
