# Lab: Torrent + VPN im Container

Wozu: bevor Jularr einen Torrent-Client oder ein VPN einbaut, wird gemessen, ob es im
Container überhaupt läuft und was ohne VPN noch weiterläuft.

## Die Fragen und ihre Messung

| Frage | Wie gemessen wird |
| --- | --- |
| Läuft ein Torrent-Client im Container? | `qBittorrent` als Container, ein lawful Torrent (das Ubuntu-Server-ISO), Download über die eigene API |
| Funktioniert ein VPN im Container? | `Gluetun` als Container mit einer Anbieter-Konfiguration (WireGuard/OpenVPN) |
| Läuft der Download ohne Tunnel weiter? | Kill-Switch: Tunnel und Firewall werden geschlossen, dann Adresse und Torrent-Status erneut lesen |
| Gibt es Lecks? | öffentliche Adresse über den Tunnel und daneben, DNS-Antwort, IPv6 über und neben dem Tunnel |
| Wie schnell? | der Torrent-Status meldet die Raten; der Provider bestimmt sie |

## Der Weg

```
docker compose -f lab/torrent-vpn/compose.yaml up -d qbittorrent
docker compose -f lab/torrent-vpn/compose.yaml --profile probe run --rm probe
docker compose -f lab/torrent-vpn/compose.yaml down -v
```

## Die VPN-Arten

1. **Mit Konfiguration** (`VPN_TYPE=wireguard` oder `openvpn`, `VPN_CUSTOM_CONFIG_PATH`): der
   Weg über Gluetun. Der Anbieter muss eine Konfigurationsdatei geben. Die Datei kommt von Hand
   aus dem Dashboard des Anbieters oder aus dem Ordner der App; `prepare-config.ps1` bringt sie
   in die Form, die der Container braucht.
2. **Über das Gateway eines Anbieters** (SOCKS5 auf dem Host): der Client spricht den Proxy an.
   Das ist der Weg, den Privado für qBittorrent und die anderen Clients beschreibt – dort aber
   nur im Premium-Tarif.

Was jeder Anbieter im Gratis-Tarif hergibt, steht in `vpn/PROVIDERS.md`.

## Die zwei Helfer

```
powershell -ExecutionPolicy Bypass -File lab/torrent-vpn/find-privado-config.ps1
powershell -ExecutionPolicy Bypass -File lab/torrent-vpn/prepare-config.ps1 -Config <datei>
```

`find-privado-config.ps1` sucht die Konfiguration, die die Privado-App auf diesem Rechner
bereits erzeugt hat, und meldet nur den Pfad, das Verfahren und den Server – keine Schlüssel.
`prepare-config.ps1` prüft die Datei, legt sie als `vpn/jularr.conf` ab und schreibt
`vpn/vpn.env` mit dem Verfahren und, falls die Konfiguration danach fragt, den Zugangsdaten.
Beide rufen keine Schnittstelle des Anbieters auf.

## Was nicht ins Repository gehört

`lab/torrent-vpn/vpn/` enthält die Konfiguration des Anbieters (Schlüssel, IDs) und die
Zugangsdaten. Sie wird nicht eingecheckt; im Beispiel steht nur die Form:

```
# WireGuard
[Interface]
PrivateKey = <geheim>
Address = 10.14.0.2/32
DNS = 10.14.0.1
[Peer]
PublicKey = <vom Anbieter>
AllowedIPs = 0.0.0.0/0
Endpoint = <vom Anbieter>:51820
```

## Die Ergebnisse

| Messung | Ergebnis |
| --- | --- |
| Torrent-Client im Container | siehe `RESULT.md` |
| VPN mit Konfiguration | siehe `RESULT.md` |
| Kill-Switch | siehe `RESULT.md` |
| DNS-/IPv6-Leck | siehe `RESULT.md` |
| Geschwindigkeit | siehe `RESULT.md` |
