# Die Ergebnisse

Gemessen am 2026-10-21 und 2026-10-22 auf diesem Rechner (Windows, Docker Desktop, Spielplatz).

## Was gemessen wurde

| Messung | Ergebnis |
| --- | --- |
| Torrent-Client im Container | **läuft**: `qBittorrent` v5.3.0 (linuxserver) im Container, WebUI-API antwortet |
| Torrent hinzufügen | **läuft**: der Ubuntu-Server-Torrent (ein lawful Torrent) wird über die API angenommen |
| Download | **läuft**: 4–6 MB/s, die Datei landet im gemounteten Verzeichnis, der Fortschritt steigt |
| Ablage | das Volume des Containers ist der Ordner des Wirts – der Download ist von außen sichtbar |
| Anbieter im Container | **läuft**: Gluetun nimmt den eingebauten ProtonVPN-Anbieter, wählt einen Gratis-Server (Niederlande) und baut den Tunnel auf |
| Kill-Switch, Tunnel kommt nicht hoch | **hält**: ohne Handshake gibt es **keine** Verbindung, **kein** DNS, **keinen** Download – nicht einmal das Paketmirror ist erreichbar. Es geht nichts unverschlüsselt hinaus. |
| Anbieter aus Deutschland | Privado und Windscribe antworten auf ihrem Konto-Host nicht (siehe `vpn/PROVIDERS.md`), hide.me und Proton antworten |

## Der Weg zum echten Tunnel

Gluetun bringt ProtonVPN mit. Es braucht **einen** Wert aus dem Proton-Dashboard, keine
Konfigurationsdatei und keine Schnittstelle von Proton:

```
# 1) Kostenloses Proton-Konto: https://account.protonvpn.com/de/signup?plan=free
# 2) https://account.proton.me/u/0/vpn/WireGuard  ->  die Zeile "PrivateKey" kopieren
# 3) Den Wert in das Lab geben:
powershell -ExecutionPolicy Bypass -File lab/torrent-vpn/prepare-proton.ps1 -Key <schluessel>

docker compose -f lab/torrent-vpn/compose.yaml -f lab/torrent-vpn/compose.vpn.yaml up -d
docker compose -f lab/torrent-vpn/compose.yaml -f lab/torrent-vpn/compose.vpn.yaml --profile probe run --rm probe
```

Der Schlüssel gilt für alle Server des Kontos; `FREE_ONLY=on` hält die Messung im Gratis-Tarif,
`SERVER_COUNTRIES` wählt den Ort.

## Was der Gratis-Tarif nicht kann

Proton weist „P2P-Unterstützung“ ausdrücklich als **Premium-Funktion** aus. Im Gratis-Tarif
verbindet sich der Tracker, aber es kommen keine Peers: der Torrent bleibt bei 0 Byte. Das misst
das Lab selbst, statt es zu vermuten. Für Torrents braucht es also einen Anbieter, der P2P im
Gratis-Tarif erlaubt, oder einen eigenen Server – für **gesperrte Direktquellen** (die Light-Novel-Seiten)
reicht der Gratis-Tarif, weil eine EPUB-Datei wenige Megabyte kostet.

## Was das für Jularr heißt

1. **Der Torrent-Client läuft im Container** – die Automatisierung ist machbar. Jularr spricht ihn
   über die WebUI-API an (eigener Adapter, austauschbar ✔).
2. **Der VPN gehört in den Container, nicht in Jularr** – so bleibt Jularr unabhängig vom Anbieter ✔.
   Die Regel „ohne VPN kein Download“ ist eine Eigenschaft des Containers (Kill-Switch) ✔, Jularr
   merkt nur „Quelle nicht erreichbar“ ✔.
3. **Die Messung läuft im Tunnel-Netzraum** – die Probe teilt sich den Netzraum mit dem Tunnel.
   Nur so ist der Kill-Switch beweisbar: ohne Tunnel scheitern die Anfragen, statt unverschlüsselt
   durchzurutschen.
4. **IPv6 muss im Tunnel mitgenommen oder abgeschaltet sein** – sonst läuft ein Teil des Verkehrs am
   VPN vorbei, und genau daran würde der Kill-Switch scheitern.
