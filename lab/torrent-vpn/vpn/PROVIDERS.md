# Die Anbieter im Vergleich

Stand: 2026-10-22, nach den Angaben der Anbieter (geprüft, nicht selbst gemessen).

## Was Privado wirklich gibt (geprüft am 2026-10-22)

| Was | Free | Premium |
| --- | --- | --- |
| Konfigurations-Generator (`.ovpn`/WireGuard) | **nein** – die KB sagt wörtlich: „The generator is only available for Premium customers. Freemium users can use OpenVPN and Wireguard only through the PrivadoVPN app." | ja, im Dashboard unter „Manual Configuration" |
| SOCKS5-Gateway | **nein** – die KB verkauft den Proxy als Premium-Feature | ja, die Zugangsdaten stehen im Dashboard |
| Linux-Programm | **nein** – die Download-Seite verweist für Linux auf die manuellen Anleitungen | dasselbe |
| Daten | 10 GB im Monat | unbegrenzt |
| Verbindungen | 1 gleichzeitig | 10 gleichzeitig |
| Server | 10 (Free) | alle Standorte |

Zwei Wege bleiben, beide ohne Eingriff in den Anbieter:

1. **Die Konfiguration aus dem Ordner der App holen.** Die App baut für die Verbindung
   intern ein OpenVPN- oder WireGuard-Profil; das liegt nach dem Verbinden als Datei auf der
   Platte. `find-privado-config.ps1` sucht danach und `prepare-config.ps1` macht daraus die
   Form, die der Container braucht. Das ist ein Dateikopieren, kein Zugriff auf eine Schnittstelle
   des Anbieters.
2. **Premium kaufen** (derzeit 1,11 €/Monat). Dann gibt es den offiziellen Generator und
   offiziell dokumentierte Anleitungen für qBittorrent, Deluge, uTorrent, Vuze und BiglyBT
   über das SOCKS5-Gateway.

Was nicht geht: die Dashboard-Schnittstelle nachzubauen, um den Generator auch im Gratis-Tarif
zu benutzen. Die Nutzungsbedingungen (Stand 1. Mai 2026) untersagen das ausdrücklich – 8.2 (h)
automatisierte Zugriffe, 8.2 (i) das Umgehen von Nutzungsgrenzen und 6.1 (c) Reverse Engineering.

## Die Anbieter im Vergleich

| Anbieter | Kostenlos | Konfigurationsdatei | Linux-Client | P2P im Gratis-Tarif | Bemerkung |
| --- | --- | --- | --- | --- | --- |
| Privado | 10 GB/Monat | nur Premium | nein, nur manuell | nicht zugesagt | die App baut die Konfiguration selbst |
| hide.me | unbegrenzt | **ja** | App | prüfen | gut für den Konfigurationsweg |
| Windscribe | begrenzt | **ja** | App und CLI | **nein** (im Gratis-Tarif deaktiviert) | gut für den Konfigurationsweg, nicht für Torrents |
| Proton | unbegrenzt | **ja** | App und CLI | **nein** | dito |
| beliebiger Anbieter | je nach Anbieter | **ja** | – | je nach Anbieter | der tragfähige Weg für Gluetun |

## Der Weg, den das Lab prüft

Gluetun nimmt eine Konfigurationsdatei eines Anbieters, der eine im Gratis-Tarif herausgibt.
Das ist der Weg, den Jularr später nur als *Konfiguration* braucht: der Anbieter ist austauschbar,
weil Jularr ihn nicht kennt – der Container kennt ihn.

## Die Grenzen

- Ohne VPN-Verbindung darf kein Download weiterlaufen: das ist der Kill-Switch von Gluetun plus
  die Regel, dass die Firewall des Tunnels die ausgehenden Ports des Torrent-Clients sperrt.
- Die kostenlosen Tarife haben oft kein P2P – dort ist der Torrent nur ein Messtest der Mechanik,
  nicht des Durchsatzes. Für gesperrte Quellen, die direkt ausgeliefert werden, reichen die
  10 GB des Gratis-Tarifs lange.
- IPv6 muss im Tunnel mitgenommen oder abgeschaltet sein, sonst läuft ein Teil des Verkehrs
  am VPN vorbei.
