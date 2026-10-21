# Lab: Torrent + VPN im Container

Wozu: bevor Jularr einen Torrent-Client oder ein VPN einbaut, wird gemessen, ob es im
Container überhaupt läuft und was ohne VPN noch weiterläuft.

## Die Fragen und ihre Messung

| Frage | Wie gemessen wird |
| --- | --- |
| Läuft ein Torrent-Client im Container? | `qBittorrent` als Container, ein lawful Torrent (das Debian-Netzinstallations-ISO), Download über die eigene API |
| Funktioniert ein VPN im Container? | `Gluetun` als Container mit einer Anbieter-Konfiguration (WireGuard/OpenVPN) |
| Läuft der Download ohne Tunnel weiter? | Kill-Switch: Tunnel und Firewall werden geschlossen, dann Adresse und Torrent-Status erneut lesen |
| Gibt es Lecks? | öffentliche Adresse über den Tunnel und daneben, DNS-Antwort, IPv6 über und neben dem Tunnel |
| Wie schnell? | der Torrent-Status meldet die Raten; der Provider bestimmt sie |

## Der Weg

```
docker compose -f lab/torrent-vpn/compose.yaml up -d vpn qbittorrent
docker compose -f lab/torrent-vpn/compose.yaml --profile probe run --rm probe
docker compose -f lab/torrent-vpn/compose.yaml down -v
```

## Die VPN-Arten

1. **Mit Konfiguration** (`VPN_TYPE=wireguard`, `VPN_CUSTOM_CONFIG_PATH`): der Weg über
   Gluetun. Der Anbieter muss eine Konfigurationsdatei geben. Privado und hide.me geben im
   kostenlosen Tarif keine; dort führt der Weg über die offizielle App.
2. **Ohne Konfiguration** (offizielle App im Container): Privado Free und hide.me Free.
   Sie brauchen ein Konto und eine App; der Weg über einen Gateway-Port ist die Alternative.
   **Diese Variante braucht Zugangsdaten und ist mit dem Lab nicht allein prüfbar.**

## Was nicht ins Repository gehört

`lab/torrent-vpn/vpn/` enthält die Konfiguration des Anbieters (Schlüssel, IDs). Sie wird
nicht eingecheckt; im Beispiel steht nur die Form:

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
