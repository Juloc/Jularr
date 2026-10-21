# Die Ergebnisse

Gemessen am 2026-10-21 auf diesem Rechner (Windows, Docker Desktop, Spielplatz).

## Was gemessen wurde

| Messung | Ergebnis |
| --- | --- |
| Torrent-Client im Container | **läuft**: `qBittorrent` v5.3.0 (linuxserver) im Container, WebUI-API antwortet |
| Torrent hinzufügen | **läuft**: der Ubuntu-Server-Torrent (ein lawful Torrent) wird über die API angenommen |
| Download | **läuft**: 4–6 MB/s, die Datei landet im gemounteten Verzeichnis, der Fortschritt steigt |
| Ablage | das Volume des Containers ist der Ordner des Wirts – der Download ist von außen sichtbar |
| Adresse ohne Tunnel | die eigene Adresse des Hosts – **ohne konfigurierten VPN läuft der Download weiter** (das ist der Fall, den der Kill-Switch verhindern soll) |
| DNS | antwortet normal – ohne Tunnel kein Leck, weil gar kein Tunnel da ist |
| IPv6 | der Host hat kein IPv6; im Tunnel ist das noch zu messen |

## Was noch offen ist (braucht ein Konto)

Der VPN-Teil des Labs ist **nicht** messbar, solange keine Anbieter-Konfiguration vorliegt: Privado
und hide.me geben im kostenlosen Tarif keine reguläre `.ovpn`/WireGuard-Datei heraus. Mit einer
Konfiguration (Gluetun) laufen die restlichen Messungen: Adresse im Tunnel, DNS- und IPv6-Leck im
Tunnel, der Kill-Switch (Tunnel zu → keine Route → der Download steht) und die Geschwindigkeit im
Tunnel.

Der Ablauf dafür:

```
# Die Konfiguration des Anbieters nach lab/torrent-vpn/vpn/jularr.conf legen
docker compose -f lab/torrent-vpn/compose.yaml -f lab/torrent-vpn/compose.vpn.yaml up -d
docker compose -f lab/torrent-vpn/compose.yaml -f lab/torrent-vpn/compose.vpn.yaml --profile probe run --rm probe
```

## Was das für Jularr heißt

1. **Der Torrent-Client läuft im Container** – die Automatisierung ist machbar. Jularr spricht ihn
   über die WebUI-API an (eigener Adapter, austauschbar ✔).
2. **Der VPN gehört in den Container, nicht in Jularr** – so bleibt Jularr unabhängig vom Anbieter ✔.
   Die Regel „ohne VPN kein Download“ ist eine Eigenschaft des Containers (Kill-Switch) ✔, Jularr
   merkt nur „Quelle nicht erreichbar“ ✔.
3. **Die kostenlosen Tarife sind für Torrents der Engpass** – kein P2P heißt: entweder ein Anbieter
   mit P2P im Gratis-Tarif (muss geprüft werden) oder der Download läuft langsam.
4. **IPv6 muss im Tunnel mitgenommen oder abgeschaltet sein** – sonst läuft ein Teil des Verkehrs am
   VPN vorbei, und genau daran würde der Kill-Switch scheitern.
